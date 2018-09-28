using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using MiningCore.Blockchain.Bitcoin;
using MiningCore.Blockchain.Bitcoin.DaemonResponses;
using MiningCore.Blockchain.ZCash.Configuration;
using MiningCore.Blockchain.ZCash.DaemonResponses;
using MiningCore.Configuration;
using MiningCore.Contracts;
using MiningCore.DaemonInterface;
using MiningCore.Extensions;
using MiningCore.JsonRpc;
using MiningCore.Messaging;
using MiningCore.Notifications;
using MiningCore.Stratum;
using MiningCore.Time;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.OpenAsset;

namespace MiningCore.Blockchain.ZCash
{
    public class ZCashJobManager<TJob> : BitcoinJobManager<TJob, ZCashBlockTemplate>
        where TJob : ZCashJob, new()
    {
        public ZCashJobManager(
            IComponentContext ctx,
            IMasterClock clock,
            IMessageBus messageBus,
            IExtraNonceProvider extraNonceProvider) : base(ctx, clock, messageBus, extraNonceProvider)
        {
            getBlockTemplateParams = new object[]
            {
                new
                {
                    capabilities = new[] { "coinbasetxn", "workid", "coinbase/append" },
                }
            };
        }

        protected ZCashChainConfig chainConfig;
        private ZCashPoolConfigExtra zcashExtraPoolConfig;

        #region Overrides of JobManagerBase<TJob>

        public override void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
        {
            zcashExtraPoolConfig = poolConfig.Extra.SafeExtensionDataAs<ZCashPoolConfigExtra>();

            base.Configure(poolConfig, clusterConfig);
        }

        #endregion

        #region Overrides of BitcoinJobManager<TJob,ZCashBlockTemplate>

        protected override void PostChainIdentifyConfigure()
        {
            if (ZCashConstants.Chains.TryGetValue(poolConfig.Coin.Type, out var coinbaseTx))
                coinbaseTx.TryGetValue(networkType, out chainConfig);

            base.PostChainIdentifyConfigure();
        }

        #endregion

        public override async Task<bool> ValidateAddressAsync(string address, CancellationToken ct)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(address), $"{nameof(address)} must not be empty");

            // handle t-addr
            if (await base.ValidateAddressAsync(address, ct))
                return true;

            // handle z-addr
            var result = await daemon.ExecuteCmdAnyAsync<ValidateAddressResponse>(ct,
                ZCashCommands.ZValidateAddress, new[] { address });

            return result.Response != null && result.Response.IsValid;
        }

        protected override async Task<DaemonResponse<ZCashBlockTemplate>> GetBlockTemplateAsync()
        {
            logger.LogInvoke();

            var subsidyResponse = await daemon.ExecuteCmdAnyAsync<ZCashBlockSubsidy>(BitcoinCommands.GetBlockSubsidy);

            var result = await daemon.ExecuteCmdAnyAsync<ZCashBlockTemplate>(
                BitcoinCommands.GetBlockTemplate, getBlockTemplateParams);

            if (subsidyResponse.Error == null && result.Error == null && result.Response != null)
                result.Response.Subsidy = subsidyResponse.Response;
            else
                result.Error = new JsonRpcException(-1, $"{BitcoinCommands.GetBlockSubsidy} failed", null);

            return result;
        }

        public override object[] GetSubscriberData(StratumClient worker)
        {
            Contract.RequiresNonNull(worker, nameof(worker));

            var context = worker.ContextAs<BitcoinWorkerContext>();

            // assign unique ExtraNonce1 to worker (miner)
            context.ExtraNonce1 = extraNonceProvider.Next();

            // setup response data
            var responseData = new object[]
            {
                context.ExtraNonce1
            };

            return responseData;
        }

        protected override IDestination AddressToDestination(string address)
        {
            if (!chainConfig.UsesZCashAddressFormat)
                return base.AddressToDestination(address);

            var decoded = Encoders.Base58.DecodeData(address);
            var hash = decoded.Skip(2).Take(20).ToArray();
            var result = new KeyId(hash);
            return result;
        }

        public override async Task<Share> SubmitShareAsync(StratumClient worker, object submission,
            double stratumDifficultyBase, CancellationToken ct)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.RequiresNonNull(submission, nameof(submission));

            logger.LogInvoke(new[] { worker.ConnectionId });

            if (!(submission is object[] submitParams))
                throw new StratumException(StratumError.Other, "invalid params");

            var context = worker.ContextAs<BitcoinWorkerContext>();

            // extract params
            var workerValue = (submitParams[0] as string)?.Trim();
            var jobId = submitParams[1] as string;
            var nTime = submitParams[2] as string;
            var extraNonce2 = submitParams[3] as string;
            var solution = submitParams[4] as string;

            if (string.IsNullOrEmpty(workerValue))
                throw new StratumException(StratumError.Other, "missing or invalid workername");

            if (string.IsNullOrEmpty(solution))
                throw new StratumException(StratumError.Other, "missing or invalid solution");

            ZCashJob job;

            lock(jobLock)
            {
                job = validJobs.FirstOrDefault(x => x.JobId == jobId);
            }

            if (job == null)
                throw new StratumException(StratumError.JobNotFound, "job not found");

            // extract worker/miner/payoutid
            var split = workerValue.Split('.');
            var minerName = split[0];
            var workerName = split.Length > 1 ? split[1] : null;

            // validate & process
            var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, solution);

            // if block candidate, submit & check if accepted by network
            if (share.IsBlockCandidate)
            {
                logger.Info(() => $"Submitting block {share.BlockHeight} [{share.BlockHash}]");

                var acceptResponse = await SubmitBlockAsync(share, blockHex);

                // is it still a block candidate?
                share.IsBlockCandidate = acceptResponse.Accepted;

                if (share.IsBlockCandidate)
                {
                    logger.Info(() => $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {minerName}");

                    blockSubmissionSubject.OnNext(Unit.Default);

                    // persist the coinbase transaction-hash to allow the payment processor
                    // to verify later on that the pool has received the reward for the block
                    share.TransactionConfirmationData = acceptResponse.CoinbaseTransaction;
                }

                else
                {
                    // clear fields that no longer apply
                    share.TransactionConfirmationData = null;
                }
            }

            // enrich share with common data
            share.PoolId = poolConfig.Id;
            share.IpAddress = worker.RemoteEndpoint.Address.ToString();
            share.Miner = minerName;
            share.Worker = workerName;
            share.UserAgent = context.UserAgent;
            share.Source = clusterConfig.ClusterName;
            share.NetworkDifficulty = job.Difficulty;
            share.Difficulty = share.Difficulty / ShareMultiplier;
            share.Created = clock.Now;

            return share;
        }
    }
}
