﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using CodeContracts;
using Microsoft.Extensions.Logging;
using MiningForce.Blockchain.Bitcoin.DaemonResponses;
using MiningForce.Configuration;
using MiningForce.Configuration.Extensions;
using MiningForce.Crypto;
using MiningForce.Crypto.Hashing;
using MiningForce.Crypto.Hashing.Special;
using MiningForce.Extensions;
using MiningForce.MininigPool;
using MiningForce.Stratum;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace MiningForce.Blockchain.Bitcoin
{
    public class BitcoinJobManager : JobManagerBase<BitcoinWorkerContext, BitcoinJob>,
        IBlockchainJobManager
    {
        public BitcoinJobManager(
            IComponentContext ctx, 
            ILogger<BitcoinJobManager> logger,
            BlockchainDaemon daemon,
            ExtraNonceProvider extraNonceProvider,
	        PoolConfig poolConfig) : base(ctx, logger, daemon, poolConfig)
        {
			this.extraNonceProvider = extraNonceProvider;
        }

	    private BitcoinAddress poolAddress;
        private readonly ExtraNonceProvider extraNonceProvider;
        private readonly NetworkStats networkStats = new NetworkStats();
        private bool isPoS;
        private bool isTestNet;
        private bool isRegTestNet;
        private bool hasSubmitBlockMethod;
	    private double shareMultiplier;
		private IHashAlgorithm coinbaseHasher;
		private IHashAlgorithm headerHasher;
	    private IHashAlgorithm blockHasher;

		private const string GetInfoCommand = "getinfo";
        private const string GetMiningInfoCommand = "getmininginfo";
        private const string GetPeerInfoCommand = "getpeerinfo";
        private const string ValidateAddressCommand = "validateaddress";
        private const string GetDifficultyCommand = "getdifficulty";
        private const string GetBlockTemplateCommand = "getblocktemplate";
        private const string SubmitBlockCommand = "submitblock";
        private const string GetBlockchainInfoCommand = "getblockchaininfo";
	    private const string GetBlockCommand = "getblock";

		private static readonly object[] getBlockTemplateParams = 
        {
            new
            {
                capabilities = new[] {"coinbasetxn", "workid", "coinbase/append"},
                rules = new[] {"segwit"}
            },
        };

	    #region API-Surface

		public async Task<bool> ValidateAddressAsync(string address)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(address), $"{nameof(address)} must not be empty");

            var result = await daemon.ExecuteCmdAnyAsync<ValidateAddress>(ValidateAddressCommand, new[] { address });
            return result.Response != null && result.Response.IsValid;
        }

        public Task<object[]> HandleWorkerSubscribeAsync(StratumClient worker)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            
            // setup worker context
            var context = GetWorkerContext(worker);

			// assign unique ExtraNonce1 to worker (miner)
			context.ExtraNonce1 = extraNonceProvider.Next().ToBigEndian().ToString("x4");

            // setup response data
            var responseData = new object[]
            {
                context.ExtraNonce1,
                extraNonceProvider.Size
            };

            return Task.FromResult(responseData);
        }

        public async Task<IShare> HandleWorkerSubmitShareAsync(StratumClient worker, object submission, double stratumDifficulty)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.RequiresNonNull(submission, nameof(submission));

	        var submitParams = submission as object[];
			if(submitParams == null)
				throw new StratumException(StratumError.Other, "invalid params");

			// extract params
			var workername = submitParams[0] as string;
	        var jobId = submitParams[1] as string;
	        var extraNonce2 = submitParams[2] as string;
	        var nTime = submitParams[3] as string;
	        var nonce = submitParams[4] as string;

	        BitcoinJob job;
	        DateTime now = DateTime.UtcNow;

			lock (jobLock)
			{
				validJobs.TryGetValue(jobId, out job);
			}

			if(job == null)
		        throw new StratumException(StratumError.JobNotFound, "job not found");

			// get worker context
			var context = GetWorkerContext(worker);

			// validate & process
			var share = job.ProcessShare(context.ExtraNonce1, extraNonce2, nTime, nonce);

			// if candidate, submit & check if accepted by network
			if (share.IsBlockCandidate)
			{
				await SubmitBlockAsync(share);
				var acceptResponse = await CheckAcceptedAsync(share);

				// is still a block candidate?
				share.IsBlockCandidate = acceptResponse.Accepted;

				if (share.IsBlockCandidate)
				{
					logger.Info(() => $"[{poolConfig.Coin.Type}] Block '{share.BlockHash}' has been accepted by network");

					// persist the coinbase transaction-hash to allow the payment processor
					// to verify later on that the pool has received the reward for the block
					// along with the amount
					share.BlockVerificationData = acceptResponse.CoinbaseTransaction;
				}
			}

		    else
		    {
			    // otherwise check difficulty
			    var ratio = share.Difficulty / stratumDifficulty;
				if(ratio < 0.99)
					throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({share.Difficulty})");
			}

			// enrich share with common data
	        share.Coin = poolConfig.Coin.Type;
			share.Worker = workername;
		    share.IpAddress = worker.RemoteEndpoint.Address.ToString();
	        share.Submitted = now;

			return share;
        }

	    public NetworkStats NetworkStats => networkStats;

        #endregion // API-Surface

        #region Overrides

        protected override async Task<bool> IsDaemonHealthy()
        {
            var responses = await daemon.ExecuteCmdAllAsync<GeneralInfo>(GetInfoCommand);

            return responses.All(x => x.Error == null);
        }

        protected override async Task EnsureDaemonsSynchedAsync()
        {
            var syncPendingNotificationShown = false;

            while (true)
            {
                var responses = await daemon.ExecuteCmdAllAsync<BlockTemplate>(
                    GetBlockTemplateCommand, getBlockTemplateParams);

                var isSynched = responses.All(x => x.Error == null || x.Error.Code != -10);

                if (isSynched)
                {
                    logger.Info(() => $"[{poolConfig.Coin.Type}] Daemons are fully synched with blockchain");
                    break;
                }

                if(!syncPendingNotificationShown)
                { 
                    logger.Info(() => $"[{poolConfig.Coin.Type}] Daemons still syncing with network (download blockchain) - manager will be started once synced");
                    syncPendingNotificationShown = true;
                }

                await ShowDaemonSyncProgressAsync();

                // delay retry by 5s
                await Task.Delay(5000);
            }
        }

        protected override async Task PostStartInitAsync()
        {
			var tasks = new Task[] 
            {
                daemon.ExecuteCmdAnyAsync<ValidateAddress>(ValidateAddressCommand, 
                    new[] { poolConfig.Address }),
                daemon.ExecuteCmdAnyAsync<JToken>(GetDifficultyCommand),
                daemon.ExecuteCmdAnyAsync<GeneralInfo>(GetInfoCommand),
                daemon.ExecuteCmdAnyAsync<MiningInfo>(GetMiningInfoCommand),
                daemon.ExecuteCmdAnyAsync<object>(SubmitBlockCommand),
                daemon.ExecuteCmdAnyAsync<BlockchainInfo>(GetBlockchainInfoCommand),
            };

            var batchTask = Task.WhenAll(tasks);
            await batchTask;

            if (!batchTask.IsCompletedSuccessfully)
            {
                logger.Error(()=> $"[{poolConfig.Coin.Type}] Init RPC failed", batchTask.Exception);
                throw new PoolStartupAbortException();
            }

            // extract results
            var validateAddressResponse = ((Task<DaemonResponse<ValidateAddress>>) tasks[0]).Result;
            var difficultyResponse = ((Task<DaemonResponse<JToken>>)tasks[1]).Result;
            var infoResponse = ((Task<DaemonResponse<GeneralInfo>>)tasks[2]).Result;
            var miningInfoResponse = ((Task<DaemonResponse<MiningInfo>>)tasks[3]).Result;
            var submitBlockResponse = ((Task<DaemonResponse<object>>)tasks[4]).Result;
            var blockchainInfoResponse = ((Task<DaemonResponse<BlockchainInfo>>)tasks[5]).Result;

            // validate pool-address for pool-fee payout
            if (!validateAddressResponse.Response.IsValid)
                throw new PoolStartupAbortException($"[{poolConfig.Coin.Type}] Daemon reports pool-address '{poolConfig.Address}' as invalid");

			if (!validateAddressResponse.Response.IsMine)
				throw new PoolStartupAbortException($"[{poolConfig.Coin.Type}] Daemon does not own pool-address '{poolConfig.Address}'");

			isPoS = difficultyResponse.Response.Values().Any(x=> x.Path == "proof-of-stake");

            // POS coins must use the pubkey in coinbase transaction, and pubkey is only given if address is owned by wallet
            if (isPoS && string.IsNullOrEmpty(validateAddressResponse.Response.PubKey))
                throw new PoolStartupAbortException($"[{poolConfig.Coin.Type}] The pool-address is not from the daemon wallet - this is required for POS coins");

	        poolAddress = BitcoinAddress.Create(poolConfig.Address);

			// chain detection
			isTestNet = blockchainInfoResponse.Response.Chain == "test";
            isRegTestNet = blockchainInfoResponse.Response.Chain == "regtest";

            // block submission RPC method
            if (submitBlockResponse.Error?.Message?.ToLower() == "method not found")
                hasSubmitBlockMethod = false;
            else if (submitBlockResponse.Error?.Code == -1)
                hasSubmitBlockMethod = true;
            else
                throw new PoolStartupAbortException($"[{poolConfig.Coin.Type}] Unable detect block submission RPC method");

            // update stats
            networkStats.Network = isTestNet ? "Testnet" : (isRegTestNet ? "Regtest" : "Main");
            networkStats.BlockHeight = infoResponse.Response.Blocks;
            networkStats.Difficulty = miningInfoResponse.Response.Difficulty;
            networkStats.HashRate = miningInfoResponse.Response.NetworkHashps;
            networkStats.ConnectedPeers = infoResponse.Response.Connections;
            networkStats.RewardType = !isPoS ? "POW" : "POS";

	        SetupCrypto();
		}

	    protected override async Task<bool> UpdateJobs(bool forceUpdate)
        {
			var result = await GetBlockTemplateAsync();

	        lock (jobLock)
	        {
		        var isNew = currentJob == null ||
		                (currentJob.BlockTemplate.PreviousBlockhash != result.PreviousBlockhash ||
		                 currentJob.BlockTemplate.Height < result.Height);

		        if (isNew || forceUpdate)
		        {
			        currentJob = new BitcoinJob(poolConfig, poolAddress, extraNonceProvider, isPoS, shareMultiplier,
				        coinbaseHasher, headerHasher, blockHasher, result, NextJobId());

			        currentJob.Init();

			        if (isNew)
			        {
				        validJobs.Clear();

				        // update stats
						networkStats.LastBlockTime = DateTime.UtcNow;
			        }

			        validJobs[currentJob.JobId] = currentJob;
		        }

		        return isNew;
	        }
		}

		protected override object GetJobParamsForStratum(bool isNew)
        {
	        lock (jobLock)
	        {
		        return currentJob?.GetJobParams(isNew);
	        }
        }

		#endregion // Overrides

		private async Task<BlockTemplate> GetBlockTemplateAsync()
        {
            var result = await daemon.ExecuteCmdAnyAsync<BlockTemplate>(
                GetBlockTemplateCommand, getBlockTemplateParams);

            return result.Response;
        }

		private async Task ShowDaemonSyncProgressAsync()
        {
            var infos = await daemon.ExecuteCmdAllAsync<GeneralInfo>(GetInfoCommand);

            if (infos.Length > 0)
            {
                var blockCount = infos
                    .Max(x => x.Response?.Blocks);

                if (blockCount.HasValue)
                {
                    // get list of peers and their highest block height to compare to ours
                    var peerInfo = await daemon.ExecuteCmdAnyAsync<PeerInfo[]>(GetPeerInfoCommand);
                    var peers = peerInfo.Response;

                    if (peers != null && peers.Length > 0)
                    {
                        var totalBlocks = peers
                            .OrderBy(x => x.StartingHeight)
                            .First().StartingHeight;

                        var percent = ((double)totalBlocks / blockCount) * 100;
                        this.logger.Info(() => $"[{poolConfig.Coin.Type}] Daemons have downloaded {percent}% of blockchain from {peers.Length} peers");
                    }
                }
            }
        }

	    private async Task SubmitBlockAsync(BitcoinShare share)
	    {
		    logger.Info(() => $"[{poolConfig.Coin.Type}] Submitting block '{share.BlockHash}' to network");

			if (hasSubmitBlockMethod)
			    await daemon.ExecuteCmdAnyAsync<string>(SubmitBlockCommand, new[] { share.BlockHex });
			else
				await daemon.ExecuteCmdAnyAsync<JToken>(GetBlockTemplateCommand, new { mode = "submit", data = share.BlockHex });
		}

		private async Task<(bool Accepted, string CoinbaseTransaction)> CheckAcceptedAsync(BitcoinShare share)
	    {
		    var result = await daemon.ExecuteCmdAnyAsync<DaemonResponses.Block>(GetBlockCommand, new[] { share.BlockHash });
		    var accepted = result.Error == null && result.Response.Hash == share.BlockHash;

			return (accepted, result.Response.Transactions.FirstOrDefault());
	    }

		private void SetupCrypto()
	    {
			switch (poolConfig.Coin.Type)
		    {
				case CoinType.Bitcoin:
					coinbaseHasher = new Sha256Double();
					headerHasher = coinbaseHasher;
					blockHasher = new DigestReverser(coinbaseHasher);
					shareMultiplier = 1;
					break;

				default:
				    logger.Error(() => $"[{poolConfig.Coin.Type}] Coin Type '{poolConfig.Coin.Type}' not supported by this Job Manager");
				    throw new PoolStartupAbortException();
		    }
		}
	}
}
