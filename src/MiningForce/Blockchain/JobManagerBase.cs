﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using CodeContracts;
using NLog;
using MiningForce.Authorization;
using MiningForce.Blockchain.Bitcoin;
using MiningForce.Configuration;
using MiningForce.Daemon;
using MiningForce.Stratum;
using MiningForce.Util;

namespace MiningForce.Blockchain
{
    public abstract class JobManagerBase<TWorkerContext, TJob>
        where TWorkerContext: class, new()
    {
        protected JobManagerBase(IComponentContext ctx, DaemonClient daemon)
        {
	        Contract.RequiresNonNull(ctx, nameof(ctx));
	        Contract.RequiresNonNull(daemon, nameof(daemon));

			this.ctx = ctx;
            this.daemon = daemon;
		}

        protected readonly IComponentContext ctx;
	    protected PoolConfig poolConfig;
	    protected ClusterConfig clusterConfig;
        protected DaemonClient daemon;
        protected StratumServer stratum;
        private IWorkerAuthorizer authorizer;
        protected ILogger logger;
	    private TimeSpan jobRebroadcastTimeout;
	    protected DateTime? lastBlockUpdate;

		protected readonly ConditionalWeakTable<StratumClient, TWorkerContext> workerContexts =
            new ConditionalWeakTable<StratumClient, TWorkerContext>();

		protected readonly Dictionary<string, TJob> validJobs = new Dictionary<string, TJob>();
	    protected TJob currentJob;
	    protected object jobLock = new object();
	    private long jobId;

	    #region API-Surface

		public IObservable<object> Jobs { get; private set; }

	    public void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
	    {
		    Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
			Contract.RequiresNonNull(clusterConfig, nameof(clusterConfig));

		    this.logger = LogUtil.GetPoolScopedLogger(typeof(BitcoinJobManager), poolConfig);
			this.poolConfig = poolConfig;
		    this.clusterConfig = clusterConfig;

		    daemon.Configure(poolConfig);
	    }

		public async Task StartAsync(StratumServer stratum)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(stratum, nameof(stratum));

	        logger.Info(() => $"[{LogCategory}] Launching ...");

			this.stratum = stratum;
	        this.jobRebroadcastTimeout = TimeSpan.FromSeconds(poolConfig.JobRebroadcastTimeout);

			SetupAuthorizer();
            await StartDaemonAsync();
            await EnsureDaemonsSynchedAsync();
            await PostStartInitAsync();
            SetupJobStream();

            logger.Info(() => $"[{LogCategory}] Online");
        }

        #endregion // API-Surface

        protected virtual void SetupAuthorizer()
        {
            authorizer = ctx.ResolveNamed<IWorkerAuthorizer>(poolConfig.Authorizer.ToString());
        }

        /// <summary>
        /// Authorizes workers using configured authenticator
        /// </summary>
        /// <param name="worker">StratumClient requesting authorization</param>
        /// <param name="workername">Name of worker requesting authorization</param>
        /// <param name="password">The password</param>
        /// <returns></returns>
        public virtual Task<bool> AuthenticateWorkerAsync(StratumClient worker, string workername, string password)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(workername), $"{nameof(workername)} must not be empty");

            return authorizer.AuthorizeAsync((IBlockchainJobManager) this, worker.RemoteEndpoint, workername, password);
        }
        
        protected virtual async Task StartDaemonAsync()
        {
            while (!await IsDaemonHealthy())
            {
                logger.Info(() => $"[{LogCategory}] Waiting for daemons to come online ...");

                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            logger.Info(() => $"[{LogCategory}] All daemons online");

	        while (!await IsDaemonConnected())
	        {
		        logger.Info(() => $"[{LogCategory}] Waiting for daemons to connect to peers ...");

		        await Task.Delay(TimeSpan.FromSeconds(10));
	        }
		}

		protected virtual void SetupJobStream()
        {
            Jobs = Observable.Create<object>(observer =>
            {
                var interval = TimeSpan.FromMilliseconds(poolConfig.BlockRefreshInterval);
                var abort = false;

                var task = new Task(async () =>
                {
                    while (!abort)
                    {
                        try
                        {
                            var now = DateTime.UtcNow;

							// force an update of current job if we haven't received a new block for a while
	                        var forceUpdate = lastBlockUpdate.HasValue && (now - lastBlockUpdate) > jobRebroadcastTimeout;

							if(forceUpdate)
								logger.Debug(()=> $"[{LogCategory}] No new blocks for {jobRebroadcastTimeout.TotalSeconds} seconds - updating transactions & rebroadcasting work");

							if (await UpdateJobs(forceUpdate) || forceUpdate)
							{
								var isNew = !forceUpdate;

								if (isNew)
									logger.Info(() => $"[{LogCategory}] New block detected");

								lastBlockUpdate = now;

	                            // emit new job params
								var jobParams = GetJobParamsForStratum(isNew);
                                observer.OnNext(jobParams);
                            }

	                        if (forceUpdate)
		                        await UpdateNetworkStats();

                            Thread.Sleep(interval);
                        }
                        catch (Exception ex)
                        {
                            logger.Warn(() => $"[{LogCategory}] Error during job polling: {ex.Message}");
                        }
                    }
                }, TaskCreationOptions.LongRunning);

                task.Start();

                return Disposable.Create(()=> abort = true);
            })
            .Publish()
            .RefCount();
        }

        protected TWorkerContext GetWorkerContext(StratumClient client)
        {
            TWorkerContext context;

            lock (workerContexts)
            {
                if (!workerContexts.TryGetValue(client, out context))
                {
                    context = new TWorkerContext();
                    workerContexts.Add(client, context);
                }
            }

            return context;
        }

	    protected string NextJobId()
	    {
		    return Interlocked.Increment(ref jobId).ToString("x", CultureInfo.InvariantCulture);
	    }

	    protected virtual string LogCategory { get; } = "Job Manager";

		protected abstract Task<bool> IsDaemonHealthy();
	    protected abstract Task<bool> IsDaemonConnected();

		protected abstract Task EnsureDaemonsSynchedAsync();
        protected abstract Task PostStartInitAsync();

		/// <summary>
		/// Refresh network stats
		/// </summary>
	    protected abstract Task UpdateNetworkStats();

		/// <summary>
		/// Query coin-daemon for job (block) updates and returns true if a new job (block) was detected
		/// </summary>
		protected abstract Task<bool> UpdateJobs(bool forceUpdate);

	    /// <summary>
	    /// Packages current job parameters for stratum update
	    /// </summary>
	    /// <returns></returns>
	    protected abstract object GetJobParamsForStratum(bool isNew);
    }
}
