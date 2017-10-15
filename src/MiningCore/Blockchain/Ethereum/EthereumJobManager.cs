﻿/* 
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the "Software"), to deal in the Software without restriction, 
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, 
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, 
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial 
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Autofac;
using MiningCore.Blockchain.Ethereum.Configuration;
using MiningCore.Blockchain.Ethereum.DaemonResponses;
using MiningCore.Configuration;
using MiningCore.Crypto.Hashing.Ethash;
using MiningCore.DaemonInterface;
using MiningCore.Extensions;
using MiningCore.Payments;
using MiningCore.Stratum;
using MiningCore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Block = MiningCore.Blockchain.Ethereum.DaemonResponses.Block;
using Contract = MiningCore.Contracts.Contract;
using EC = MiningCore.Blockchain.Ethereum.EthCommands;

namespace MiningCore.Blockchain.Ethereum
{
    public class EthereumJobManager : JobManagerBase<EthereumJob>
    {
        public EthereumJobManager(
            IComponentContext ctx) :
            base(ctx)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
        }

        private DaemonEndpointConfig[] daemonEndpoints;
        private DaemonClient daemon;
        private EthereumNetworkType networkType;
        private ParityChainType chainType;
        private EthashFull ethash;
        private readonly EthereumExtraNonceProvider extraNonceProvider = new EthereumExtraNonceProvider();

        private const int MaxBlockBacklog = 3;
        protected readonly Dictionary<string, EthereumJob> validJobs = new Dictionary<string, EthereumJob>();
        private EthereumPoolConfigExtra extraPoolConfig;

        protected async Task<bool> UpdateJob()
        {
            try
            {
                var blockTemplate = await GetBlockTemplateAsync();

                // may happen if daemon is currently not connected to peers
                if (blockTemplate == null || blockTemplate.Header.Length == 0)
                    return false;

                lock (jobLock)
                {
                    var isNew = currentJob == null ||
                                currentJob.BlockTemplate.ParentHash != blockTemplate.ParentHash ||
                                currentJob.BlockTemplate.Height < blockTemplate.Height ||
                                currentJob.BlockTemplate.Seed != blockTemplate.Seed;

                    if (isNew)
                    {
                        var jobId = NextJobId("x8");

                        // update template
                        currentJob = new EthereumJob(jobId, blockTemplate);

                        // add jobs
                        validJobs[jobId] = currentJob;

                        // remove old ones
                        var obsoleteKeys = validJobs.Keys
                            .Where(key=> validJobs[key].BlockTemplate.Height < currentJob.BlockTemplate.Height - MaxBlockBacklog).ToArray();

                        foreach (var key in obsoleteKeys)
                            validJobs.Remove(key);

                        // update stats
                        BlockchainStats.LastNetworkBlockTime = DateTime.UtcNow;
                    }

                    return isNew;
                }
            }

            catch (Exception ex)
            {
                logger.Error(ex, () => $"[{LogCat}] Error during {nameof(UpdateJob)}");
            }

            return false;
        }

        private async Task<EthereumBlockTemplate> GetBlockTemplateAsync()
        {
            var commands = new[]
            {
                new DaemonCmd(EC.GetBlockByNumber, new[] { (object) "pending", true }),
                new DaemonCmd(EC.GetWork),
            };

            var results = await daemon.ExecuteBatchAnyAsync(commands);

            if (results.Any(x => x.Error != null))
            {
                var errors = results.Where(x => x.Error != null)
                    .ToArray();

                if (errors.Any())
                { 
                    logger.Warn(() => $"[{LogCat}] Error(s) refreshing blocktemplate: {string.Join(", ", errors.Select(y => y.Error.Message))})");
                    return null;
                }
            }

            // extract results
            var block = results[0].Response.ToObject<Block>();
            var work = results[1].Response.ToObject<string[]>();

            // only parity returns the 4th element (block height)
            if (work.Length < 3)
            {
                logger.Warn(() => $"[{LogCat}] Error(s) refreshing blocktemplate: getWork did not return blockheight. Are you really connecting to a Parity daemon?");
                return null;
            }

            // make sure block matches work
            var height = work[3].IntegralFromHex<ulong>();

            if (height != block.Height)
            {
                logger.Warn(() => $"[{LogCat}] Error(s) refreshing blocktemplate: getWork result not related to pending block");
                return null;
            }

            var result = new EthereumBlockTemplate
            {
                Header = work[0],
                Seed = work[1],
                Target = BigInteger.Parse("0" + work[2].Substring(2), NumberStyles.HexNumber),
                Difficulty = block.Difficulty.IntegralFromHex<ulong>(),
                Height = block.Height.Value,
                ParentHash = block.ParentHash,
            };

            return result;
        }

        private async Task ShowDaemonSyncProgressAsync()
        {
            var infos = await daemon.ExecuteCmdAllAsync<JToken>(EC.GetSyncState);
            var firstValidResponse = infos.FirstOrDefault(x => x.Error == null && x.Response != null)?.Response;

            if (firstValidResponse != null)
            {
                // eth_syncing returns false if not synching 
                if (firstValidResponse.Type == JTokenType.Boolean)
                    return;

                var syncStates = infos.Where(x => x.Error == null && x.Response != null && firstValidResponse.Type == JTokenType.Object)
                    .Select(x => x.Response.ToObject<SyncState>())
                    .ToArray();

                if (syncStates.Any())
                { 
                    // get peer count
                    var response = await daemon.ExecuteCmdAllAsync<string>(EC.GetPeerCount);
                    var validResponses = response.Where(x => x.Error == null && x.Response != null).ToArray();
                    var peerCount = validResponses.Any() ? validResponses.Max(x => x.Response.IntegralFromHex<uint>()) : 0;

                    if (syncStates.Any(x => x.WarpChunksAmount != 0))
                    {
                        var warpChunkAmount = syncStates.Min(x => x.WarpChunksAmount);
                        var warpChunkProcessed = syncStates.Max(x => x.WarpChunksProcessed);
                        var percent = (double)warpChunkProcessed / warpChunkAmount * 100;

                        logger.Info(() => $"[{LogCat}] Daemons have downloaded {percent:0.00}% of warp-chunks from {peerCount} peers");
                    }

                    else if (syncStates.Any(x => x.HighestBlock != 0))
                    {
                        var lowestHeight = syncStates.Min(x => x.CurrentBlock);
                        var totalBlocks = syncStates.Max(x => x.HighestBlock);
                        var percent = (double) lowestHeight / totalBlocks * 100;

                        logger.Info(() => $"[{LogCat}] Daemons have downloaded {percent:0.00}% of blockchain from {peerCount} peers");
                    }
                }
            }
        }

        private async Task<bool> SubmitBlockAsync(EthereumShare share)
        {
            // submit work
            var response = await daemon.ExecuteCmdAnyAsync<object>(EC.SubmitWork, new[]
            {
                share.FullNonceHex,
                share.HeaderHash,
                share.MixHash
            });

            if (response.Error != null || (bool?) response.Response == false)
            {
                var error = response.Error?.Message ?? response?.Response?.ToString();

                logger.Warn(() => $"[{LogCat}] Block {share.BlockHeight} submission failed with: {error}");
                return false;
            }

            return true;
        }

        private object[] GetJobParamsForStratum(bool isNew)
        {
            lock (jobLock)
            {
                return new object[]
                {
                    currentJob.Id,
                    currentJob.BlockTemplate.Seed,
                    currentJob.BlockTemplate.Header,
                    isNew
                };
            }
        }

        #region API-Surface

        public IObservable<object> Jobs { get; private set; }

        public override void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
        {
            extraPoolConfig = poolConfig.Extra.SafeExtensionDataAs<EthereumPoolConfigExtra>();

            // extract standard daemon endpoints
            daemonEndpoints = poolConfig.Daemons
                .Where(x => string.IsNullOrEmpty(x.Category))
                .ToArray();

            base.Configure(poolConfig, clusterConfig);

            // ensure dag location is configured
            var dagDir = !string.IsNullOrEmpty(extraPoolConfig?.DagDir) ?
                Environment.ExpandEnvironmentVariables(extraPoolConfig.DagDir) :
                Dag.GetDefaultDagDirectory();

            // create it if necessary
            Directory.CreateDirectory(dagDir);

            // setup ethash
            ethash = new EthashFull(3, dagDir);
        }

        public bool ValidateAddress(string address)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(address), $"{nameof(address)} must not be empty");

            if (EthereumConstants.ZeroHashPattern.IsMatch(address) ||
                !EthereumConstants.ValidAddressPattern.IsMatch(address))
                return false;

            return true;
        }

        public void PrepareWorker(StratumClient<EthereumWorkerContext> client)
        {
            client.Context.ExtraNonce1 = extraNonceProvider.Next();
        }

        public async Task<IShare> SubmitShareAsync(StratumClient<EthereumWorkerContext> worker,
            string[] request, double stratumDifficulty, double stratumDifficultyBase)
        {
            // var miner = request[0];
            var jobId = request[1];
            var nonce = request[2];
            EthereumJob job;

            // stale?
            lock (jobLock)
            {
                if(!validJobs.TryGetValue(jobId, out job))
                    throw new StratumException(StratumError.MinusOne, "stale share");
            }

            // validate & process
            var share = await job.ProcessShareAsync(worker, nonce, ethash);

            // if block candidate, submit & check if accepted by network
            if (share.IsBlockCandidate)
            {
                logger.Info(() => $"[{LogCat}] Submitting block {share.BlockHeight}");

                share.IsBlockCandidate = await SubmitBlockAsync(share);

                if (share.IsBlockCandidate)
                {
                    logger.Info(() => $"[{LogCat}] Daemon accepted block {share.BlockHeight}");
                }
            }

            // enrich share with common data
            share.PoolId = poolConfig.Id;
            share.NetworkDifficulty = BlockchainStats.NetworkDifficulty;
            share.StratumDifficulty = stratumDifficulty * EthereumConstants.Pow2x32;
            share.StratumDifficultyBase = stratumDifficultyBase * EthereumConstants.Pow2x32;
            share.Created = DateTime.UtcNow;

            return share;
        }

        public async Task UpdateNetworkStatsAsync()
        {
            var commands = new[]
            {
                new DaemonCmd(EC.GetBlockByNumber, new[] { (object) "latest", true }),
                new DaemonCmd(EC.GetPeerCount),
            };

            var results = await daemon.ExecuteBatchAnyAsync(commands);

            if (results.Any(x => x.Error != null))
            {
                var errors = results.Where(x => x.Error != null)
                    .ToArray();

                if (errors.Any())
                    logger.Warn(() => $"[{LogCat}] Error(s) refreshing network stats: {string.Join(", ", errors.Select(y => y.Error.Message))})");
            }

            // extract results
            var block = results[0].Response.ToObject<Block>();
            var peerCount = results[1].Response.ToObject<string>().IntegralFromHex<int>();

            BlockchainStats.BlockHeight = block.Height.HasValue ? (long)block.Height.Value : -1;
            BlockchainStats.NetworkDifficulty = block.Difficulty.IntegralFromHex<ulong>();
            BlockchainStats.NetworkHashRate = 0;    // TODO
            BlockchainStats.ConnectedPeers = peerCount;
        }

        public BlockchainStats BlockchainStats { get; } = new BlockchainStats();

        #endregion // API-Surface

        #region Overrides

        protected override string LogCat => "Ethereum Job Manager";

        protected override void ConfigureDaemons()
        {
            var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

            daemon = new DaemonClient(jsonSerializerSettings);
            daemon.Configure(daemonEndpoints);
        }

        protected override async Task<bool> AreDaemonsHealthy()
        {
            var responses = await daemon.ExecuteCmdAllAsync<Block>(EC.GetBlockByNumber, new[] { (object) "pending", true });

            if (responses.Where(x => x.Error?.InnerException?.GetType() == typeof(DaemonClientException))
                .Select(x => (DaemonClientException)x.Error.InnerException)
                .Any(x => x.Code == HttpStatusCode.Unauthorized))
                logger.ThrowLogPoolStartupException($"Daemon reports invalid credentials", LogCat);

            return responses.All(x => x.Error == null);
        }

        protected override async Task<bool> AreDaemonsConnected()
        {
            var response = await daemon.ExecuteCmdAnyAsync<string>(EC.GetPeerCount);

            return response.Error == null && response.Response.IntegralFromHex<uint>() > 0;
        }

        protected override async Task EnsureDaemonsSynchedAsync()
        {
            var syncPendingNotificationShown = false;

            while (true)
            {
                var responses = await daemon.ExecuteCmdAllAsync<object>(EC.GetSyncState);

                var isSynched = responses.All(x => x.Error == null && 
                    x.Response is bool && (bool) x.Response == false);

                if (isSynched)
                {
                    logger.Info(() => $"[{LogCat}] All daemons synched with blockchain");
                    break;
                }

                if (!syncPendingNotificationShown)
                {
                    logger.Info(() => $"[{LogCat}] Daemons still syncing with network. Manager will be started once synced");
                    syncPendingNotificationShown = true;
                }

                await ShowDaemonSyncProgressAsync();

                // delay retry by 5s
                await Task.Delay(5000);
            }
        }

        protected override async Task PostStartInitAsync()
        {
            var commands = new[]
            {
                new DaemonCmd(EC.GetNetVersion),
                new DaemonCmd(EC.GetAccounts),
                new DaemonCmd(EC.GetCoinbase),
                new DaemonCmd(EC.ParityVersion),
                new DaemonCmd(EC.ParityChain),
            };

            var results = await daemon.ExecuteBatchAnyAsync(commands);

            if (results.Any(x => x.Error != null))
            {
                if(results[4].Error != null)
                    logger.ThrowLogPoolStartupException($"Looks like you are NOT running 'Parity' as daemon which is not supported - https://parity.io/", LogCat);

                var errors = results.Where(x => x.Error != null)
                    .ToArray();

                if (errors.Any())
                    logger.ThrowLogPoolStartupException($"Init RPC failed: {string.Join(", ", errors.Select(y => y.Error.Message))}", LogCat);
            }

            // extract results
            var netVersion = results[0].Response.ToObject<string>();
            var accounts = results[1].Response.ToObject<string[]>();
            var coinbase = results[2].Response.ToObject<string>();
            var parityVersion = results[3].Response.ToObject<JObject>();
            var parityChain = results[4].Response.ToObject<string>();

            // ensure pool owns wallet
            if (!accounts.Contains(poolConfig.Address) || coinbase != poolConfig.Address)
                logger.ThrowLogPoolStartupException($"Daemon does not own pool-address '{poolConfig.Address}'", LogCat);

            EthereumUtils.DetectNetworkAndChain(netVersion, parityChain, out networkType, out chainType);

            ConfigureRewards();

            // update stats
            BlockchainStats.RewardType = "POW";
            BlockchainStats.NetworkType = $"{chainType}";

            await UpdateNetworkStatsAsync();

            // make sure we have a current DAG
            while (true)
            {
                var blockTemplate = await GetBlockTemplateAsync();

                if (blockTemplate == null)
                {
                    logger.Info(() => $"[{LogCat}] Waiting for first valid block template");

                    await Task.Delay(TimeSpan.FromSeconds(5));
                    continue;
                }

                await ethash.GetDagAsync(blockTemplate.Height);
                break;
            }

            SetupJobUpdates();
        }

        private void ConfigureRewards()
        {
            // Donation to Miningcore development
            if (clusterConfig.DevDonation > 0)
            {
                string address = null;

                if (chainType == ParityChainType.Mainnet && networkType == EthereumNetworkType.Main)
                    address = EthereumConstants.EthDevAddress;
                else if (chainType == ParityChainType.Classic && networkType == EthereumNetworkType.Main)
                    address = EthereumConstants.EtcDevAddress;

                if (!string.IsNullOrEmpty(address))
                {
                    poolConfig.RewardRecipients = poolConfig.RewardRecipients.Concat(new[]
                    {
                        new RewardRecipient
                        {
                            Type = RewardRecipientType.Dev,
                            Address = address,
                            Percentage = clusterConfig.DevDonation,
                        }
                    }).ToArray();
                }
            }
        }

        protected virtual void SetupJobUpdates()
        {
            // periodically update block-template from daemon
            Jobs = Observable.Interval(TimeSpan.FromMilliseconds(poolConfig.BlockRefreshInterval))
                .Select(_ => Observable.FromAsync(UpdateJob))
                .Concat()
                .Do(isNew =>
                {
                    if (isNew)
                        logger.Info(() => $"[{LogCat}] New block detected");
                })
                .Where(isNew => isNew)
                .Select(_ => GetJobParamsForStratum(true))
                .Publish()
                .RefCount();
        }

        #endregion // Overrides
    }
}
