﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using CodeContracts;
using MiningForce.Blockchain.Bitcoin.DaemonResponses;
using MiningForce.Configuration;
using MiningForce.Crypto;
using MiningForce.Crypto.Hashing.Algorithms;
using MiningForce.Crypto.Hashing.Special;
using MiningForce.DaemonInterface;
using MiningForce.Extensions;
using MiningForce.Stratum;
using MiningForce.Util;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace MiningForce.Blockchain.Bitcoin
{
    public class BitcoinJobManager : JobManagerBase<BitcoinWorkerContext, BitcoinJob>,
        IBlockchainJobManager
    {
        public BitcoinJobManager(
            IComponentContext ctx, 
            DaemonClient daemon,
            ExtraNonceProvider extraNonceProvider) : 
			base(ctx, daemon)
        {
	        Contract.RequiresNonNull(ctx, nameof(ctx));
	        Contract.RequiresNonNull(daemon, nameof(daemon));
	        Contract.RequiresNonNull(extraNonceProvider, nameof(extraNonceProvider));

			this.extraNonceProvider = extraNonceProvider;
        }

        private readonly ExtraNonceProvider extraNonceProvider;
        private readonly BlockchainStats blockchainStats = new BlockchainStats();
	    private IDestination poolAddressDestination;
		private bool isPoS;
	    private BitcoinNetworkType networkType;
        private bool hasSubmitBlockMethod;
	    private double difficultyNormalizationFactor;
		private IHashAlgorithm coinbaseHasher;
		private IHashAlgorithm headerHasher;
	    private IHashAlgorithm blockHasher;

	    private readonly IHashAlgorithm sha256s = new Sha256S();
	    private readonly IHashAlgorithm sha256d = new Sha256D();
	    private readonly IHashAlgorithm sha256dReverse = new DigestReverser(new Sha256D());

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

            var result = await daemon.ExecuteCmdAnyAsync<ValidateAddressResponse>(BitcoinCommands.ValidateAddress, new[] { address });
            return result.Response != null && result.Response.IsValid;
        }

        public Task<object[]> SubscribeWorkerAsync(StratumClient worker)
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

        public async Task<IShare> SubmitShareAsync(StratumClient worker, object submission, double stratumDifficulty)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.RequiresNonNull(submission, nameof(submission));

	        var submitParams = submission as object[];
			if(submitParams == null)
				throw new StratumException(StratumError.Other, "invalid params");

			// extract params
			var workername = (submitParams[0] as string)?.Trim();
	        var jobId = submitParams[1] as string;
	        var extraNonce2 = submitParams[2] as string;
	        var nTime = submitParams[3] as string;
	        var nonce = submitParams[4] as string;

	        BitcoinJob job;

	        lock (jobLock)
			{
				validJobs.TryGetValue(jobId, out job);
			}

			if(job == null)
		        throw new StratumException(StratumError.JobNotFound, "job not found");

			// under testnet or regtest conditions network difficulty may be lower than statum diff
	        var minDiff = Math.Min(blockchainStats.NetworkDifficulty, stratumDifficulty);

			// get worker context
			var context = GetWorkerContext(worker);

			// validate & process
			var share = job.ProcessShare(context.ExtraNonce1, extraNonce2, nTime, nonce, minDiff);

			// if block candidate, submit & check if accepted by network
			if (share.IsBlockCandidate)
			{
				logger.Info(() => $"[{LogCat}] Submitting block {share.BlockHash}");

				var acceptResponse = await SubmitBlockAsync(share);

				// is it still a block candidate?
				share.IsBlockCandidate = acceptResponse.Accepted;

				if (share.IsBlockCandidate)
				{
					logger.Info(() => $"[{LogCat}] Daemon accepted block {share.BlockHash}");

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
			share.Worker = workername;
	        share.NetworkDifficulty = blockchainStats.NetworkDifficulty;
			share.Created = DateTime.UtcNow;

			return share;
        }

	    public BlockchainStats BlockchainStats => blockchainStats;
 
		#endregion // API-Surface

		#region Overrides

		protected override string LogCat => "Bitcoin Job Manager";

		protected override async Task<bool> IsDaemonHealthy()
        {
            var responses = await daemon.ExecuteCmdAllAsync<GetInfoResponse>(BitcoinCommands.GetInfo);

            return responses.All(x => x.Error == null);
        }

	    protected override async Task<bool> IsDaemonConnected()
	    {
		    var response = await daemon.ExecuteCmdAnyAsync<GetInfoResponse>(BitcoinCommands.GetInfo);

		    return response.Error == null && response.Response.Connections > 0;
	    }

		protected override async Task EnsureDaemonsSynchedAsync()
        {
            var syncPendingNotificationShown = false;

            while (true)
            {
                var responses = await daemon.ExecuteCmdAllAsync<GetBlockTemplateResponse>(
                    BitcoinCommands.GetBlockTemplate, getBlockTemplateParams);

                var isSynched = responses.All(x => x.Error == null || x.Error.Code != -10);

                if (isSynched)
                {
                    logger.Info(() => $"[{LogCat}] All daemons synched with blockchain");
                    break;
                }

                if(!syncPendingNotificationShown)
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
		        new DaemonCmd(BitcoinCommands.ValidateAddress, new[] {poolConfig.Address}),
		        new DaemonCmd(BitcoinCommands.GetDifficulty),
		        new DaemonCmd(BitcoinCommands.SubmitBlock),
		        new DaemonCmd(BitcoinCommands.GetBlockchainInfo)
	        };

	        var results = await daemon.ExecuteBatchAnyAsync(commands);

	        if (results.Any(x => x.Error != null))
	        {
		        var resultList = results.ToList();
				var errors = results.Where(x => x.Error != null && commands[resultList.IndexOf(x)].Method != BitcoinCommands.SubmitBlock).ToArray();
				
				if(errors.Any())
					logger.ThrowLogPoolStartupException($"Init RPC failed: {string.Join(", ", errors.Select(y=> y.Error.Message))}", LogCat);
	        }

			// extract results
			var validateAddressResponse = results[0].Response.ToObject<ValidateAddressResponse>();
            var difficultyResponse = results[1].Response.ToObject<JToken>(); 
            var submitBlockResponse = results[2];
			var blockchainInfoResponse = results[3].Response.ToObject<GetBlockchainInfoResponse>();

            // validate pool-address for pool-fee payout
            if (!validateAddressResponse.IsValid)
                logger.ThrowLogPoolStartupException($"Daemon reports pool-address '{poolConfig.Address}' as invalid", LogCat);

			if (!validateAddressResponse.IsMine)
				logger.ThrowLogPoolStartupException($"Daemon does not own pool-address '{poolConfig.Address}'", LogCat);

			isPoS = difficultyResponse.Values().Any(x=> x.Path == "proof-of-stake");

			// Create pool address script from response
	        if (isPoS)
		        poolAddressDestination = new PubKey(validateAddressResponse.PubKey);
			else
		        poolAddressDestination = BitcoinUtils.AddressToScript(validateAddressResponse.Address);

			// chain detection
			if (blockchainInfoResponse.Chain.ToLower() == "test")
				networkType = BitcoinNetworkType.Test;
	        else if (blockchainInfoResponse.Chain.ToLower() == "regtest")
		        networkType = BitcoinNetworkType.RegTest;
			else
				networkType = BitcoinNetworkType.Main;

			// update stats
	        blockchainStats.NetworkType = networkType.ToString();
	        blockchainStats.RewardType = isPoS ? "POS" : "POW";

			// block submission RPC method
			if (submitBlockResponse.Error?.Message?.ToLower() == "method not found")
                hasSubmitBlockMethod = false;
            else if (submitBlockResponse.Error?.Code == -1)
                hasSubmitBlockMethod = true;
            else
                logger.ThrowLogPoolStartupException($"Unable detect block submission RPC method", LogCat);

            await UpdateNetworkStats();

	        SetupCrypto();
		}

	    protected override async Task<bool> UpdateJob(bool forceUpdate)
        {
			var response = await GetBlockTemplateAsync();

	        // may happen if daemon is currently not connected to peers
	        if (response.Error != null)
			{ 
		        logger.Warn(() => $"[{LogCat}] Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
				return false;
			}

	        var blockTemplate = response.Response;

	        lock (jobLock)
	        {
		        var isNew = currentJob == null ||
		                (currentJob.BlockTemplate.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
		                 currentJob.BlockTemplate.Height < blockTemplate.Height);

		        if (isNew || forceUpdate)
		        {
			        currentJob = new BitcoinJob(blockTemplate, NextJobId(),
						poolConfig, clusterConfig, poolAddressDestination, networkType, extraNonceProvider, isPoS, 
						difficultyNormalizationFactor,
						coinbaseHasher, headerHasher, blockHasher);

			        currentJob.Init();

			        if (isNew)
			        {
				        validJobs.Clear();

				        // update stats
						blockchainStats.LastNetworkBlockTime = DateTime.UtcNow;
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

		private async Task<DaemonResponse<GetBlockTemplateResponse>> GetBlockTemplateAsync()
        {
            var result = await daemon.ExecuteCmdAnyAsync<GetBlockTemplateResponse>(
	            BitcoinCommands.GetBlockTemplate, getBlockTemplateParams);

			return result;
        }

		private async Task ShowDaemonSyncProgressAsync()
        {
            var infos = await daemon.ExecuteCmdAllAsync<GetInfoResponse>(BitcoinCommands.GetInfo);

            if (infos.Length > 0)
            {
                var blockCount = infos
                    .Max(x => x.Response?.Blocks);

                if (blockCount.HasValue)
                {
                    // get list of peers and their highest block height to compare to ours
                    var peerInfo = await daemon.ExecuteCmdAnyAsync<GetPeerInfoResponse[]>(BitcoinCommands.GetPeerInfo);
                    var peers = peerInfo.Response;

                    if (peers != null && peers.Length > 0)
                    {
                        var totalBlocks = peers
                            .OrderBy(x => x.StartingHeight)
                            .First().StartingHeight;

                        var percent = ((double)totalBlocks / blockCount) * 100;
                        logger.Info(() => $"[{LogCat}] Daemons have downloaded {percent:0.#}% of blockchain from {peers.Length} peers");
                    }
                }
            }
        }

		private async Task<(bool Accepted, string CoinbaseTransaction)> SubmitBlockAsync(BitcoinShare share)
	    {
			// execute command batch
		    var results = await daemon.ExecuteBatchAnyAsync(
			    hasSubmitBlockMethod ? 
					new DaemonCmd(BitcoinCommands.SubmitBlock, new[] { share.BlockHex }) :
				    new DaemonCmd(BitcoinCommands.GetBlockTemplate, new { mode = "submit", data = share.BlockHex }),

			    new DaemonCmd(BitcoinCommands.GetBlock, new[] { share.BlockHash }));

			// did submission succeed?
		    var submitResult = results[0];
		    var submitError = submitResult.Error?.Message ?? submitResult.Response?.ToString();

			if (!string.IsNullOrEmpty(submitError))
		    {
			    logger.Warn(()=> $"[{LogCat}] Block submission failed with: {submitError}");
			    return (false, null);
		    }

			// was it accepted?
		    var acceptResult = results[1];
			var block = acceptResult.Response?.ToObject<GetBlockResponse>();
			var accepted = acceptResult.Error == null && block?.Hash == share.BlockHash;

			return (accepted, block?.Transactions.FirstOrDefault());
	    }

	    protected override async Task UpdateNetworkStats()
	    {
		    var results = await daemon.ExecuteBatchAnyAsync(
			    new DaemonCmd(BitcoinCommands.GetInfo),
			    new DaemonCmd(BitcoinCommands.GetMiningInfo)
		    );

		    if (results.Any(x => x.Error != null))
		    {
			    var errors = results.Where(x => x.Error != null).ToArray();

			    if (errors.Any())
				    logger.Warn(() => $"[{LogCat}] Error(s) refreshing network stats: {string.Join(", ", errors.Select(y => y.Error.Message))}");
		    }

		    var infoResponse = results[0].Response.ToObject<GetInfoResponse>();
		    var miningInfoResponse = results[1].Response.ToObject<GetMiningInfoResponse>();

		    blockchainStats.BlockHeight = infoResponse.Blocks;
		    blockchainStats.NetworkDifficulty = miningInfoResponse.Difficulty;
		    blockchainStats.NetworkHashRate = miningInfoResponse.NetworkHashps;
		    blockchainStats.ConnectedPeers = infoResponse.Connections;
	    }

		private void SetupCrypto()
		{
			switch (poolConfig.Coin.Type)
			{
				// SHA256
				case CoinType.BTC:
				case CoinType.NMC:
				case CoinType.PPC:
					coinbaseHasher = sha256d;
					headerHasher = sha256d;
					blockHasher = sha256dReverse;
					difficultyNormalizationFactor = 1;
					break;
				
				// Scrypt
				case CoinType.LTC:
				case CoinType.DOGE:
				case CoinType.EMC2:
				case CoinType.DGB:
				case CoinType.VIA:
					coinbaseHasher = sha256d;
					headerHasher = new Scrypt(1024, 1);
					blockHasher = !isPoS ? sha256dReverse : new DigestReverser(headerHasher);
					difficultyNormalizationFactor = Math.Pow(2, 16) / 1000;
					break;

				// Groestl
				case CoinType.GRS:
					coinbaseHasher = sha256s;
					headerHasher = new Groestl();
					blockHasher = sha256dReverse;
					difficultyNormalizationFactor = Math.Pow(2, 8) / 1000;
					break;

				default:
					logger.ThrowLogPoolStartupException("Coin Type '{poolConfig.Coin.Type}' not supported by this Job Manager", LogCat);
					break;
			}
		}
	}
}
