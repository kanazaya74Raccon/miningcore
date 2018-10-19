using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using ProtoBuf;
using ZeroMQ;

namespace Miningcore.Mining
{
    /// <summary>
    /// Receives external shares from relays and re-publishes for consumption
    /// </summary>
    public class ShareReceiver
    {
        public ShareReceiver(IMasterClock clock, IMessageBus messageBus)
        {
            Contract.RequiresNonNull(clock, nameof(clock));
            Contract.RequiresNonNull(messageBus, nameof(messageBus));

            this.clock = clock;
            this.messageBus = messageBus;
        }

        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
        private readonly IMasterClock clock;
        private readonly IMessageBus messageBus;
        private ClusterConfig clusterConfig;
        private CompositeDisposable disposables = new CompositeDisposable();
        private readonly ConcurrentDictionary<string, PoolContext> pools = new ConcurrentDictionary<string, PoolContext>();
        private readonly BufferBlock<(string SocketUrl, ZMessage Message)> queue = new BufferBlock<(string SocketUrl, ZMessage Message)>();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        JsonSerializer serializer = new JsonSerializer
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        class PoolContext
        {
            public PoolContext(IMiningPool pool, ILogger logger)
            {
                Pool = pool;
                Logger = logger;
            }

            public readonly IMiningPool Pool;
            public readonly ILogger Logger;
            public DateTime? LastBlock;
            public long BlockHeight;
        }

        private void StartMessageReceiver()
        {
            Task.Run(() =>
            {
                Thread.CurrentThread.Name = "ShareReceiver Socket Poller";
                var timeout = TimeSpan.FromMilliseconds(1000);

                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        // setup sockets
                        var sockets = clusterConfig.ShareRelays
                            .Select(relay =>
                            {
                                var subSocket = new ZSocket(ZSocketType.SUB);
                                subSocket.SetupCurveTlsClient(relay.SharedEncryptionKey, logger);
                                subSocket.Connect(relay.Url);
                                subSocket.SubscribeAll();

                                if (subSocket.CurveServerKey != null)
                                    logger.Info($"Monitoring external stratum {relay.Url} using Curve public-key {subSocket.CurveServerKey.ToHexString()}");
                                else
                                    logger.Info($"Monitoring external stratum {relay.Url}");

                                return subSocket;
                            }).ToArray();

                        using (new CompositeDisposable(sockets))
                        {
                            var urls = clusterConfig.ShareRelays.Select(x => x.Url).ToArray();
                            var pollItems = sockets.Select(_ => ZPollItem.CreateReceiver()).ToArray();

                            while (!cts.IsCancellationRequested)
                            {
                                if (sockets.PollIn(pollItems, out var messages, out var error, timeout))
                                {
                                    for (var i = 0; i < messages.Length; i++)
                                    {
                                        var msg = messages[i];

                                        if (msg != null)
                                            queue.Post((urls[i], msg));
                                    }

                                    if (error != null)
                                        logger.Error(() => $"{nameof(ShareReceiver)}: {error.Name} [{error.Name}] during receive");
                                }
                            }
                        }
                    }

                    catch (Exception ex)
                    {
                        logger.Error(() => $"{nameof(ShareReceiver)}: {ex}");

                        if (!cts.IsCancellationRequested)
                            Thread.Sleep(5000);
                    }
                }
            }, cts.Token);
        }

        private void StartMessageProcessors()
        {
            for (var i = 0; i < Environment.ProcessorCount; i++)
                ProcessMessages();
        }

        private void ProcessMessages()
        {
            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var (socketUrl, msg) = await queue.ReceiveAsync(cts.Token);

                        using (msg)
                        {
                            ProcessMessage(socketUrl, msg);
                        }
                    }

                    catch (Exception ex)
                    {
                        logger.Error(ex);
                    }
                }
            }, cts.Token);
        }

        private void ProcessMessage(string url, ZMessage msg)
        {
            // extract frames
            var topic = msg[0].ToString(Encoding.UTF8);
            var flags = msg[1].ReadUInt32();
            var data = msg[2].Read();

            // validate
            if (string.IsNullOrEmpty(topic) || !pools.ContainsKey(topic))
            {
                logger.Warn(() => $"Received share for pool '{topic}' which is not known locally. Ignoring ...");
                return;
            }

            if (data?.Length == 0)
            {
                logger.Warn(() => $"Received empty data from {url}/{topic}. Ignoring ...");
                return;
            }

            // TMP FIX
            if ((flags & ShareRelay.WireFormatMask) == 0)
                flags = BitConverter.ToUInt32(BitConverter.GetBytes(flags).ToNewReverseArray());

            // deserialize
            var wireFormat = (ShareRelay.WireFormat)(flags & ShareRelay.WireFormatMask);

            Share share = null;

            switch (wireFormat)
            {
                case ShareRelay.WireFormat.Json:
                    using (var stream = new MemoryStream(data))
                    {
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            using (var jreader = new JsonTextReader(reader))
                            {
                                share = serializer.Deserialize<Share>(jreader);
                            }
                        }
                    }

                    break;

                case ShareRelay.WireFormat.ProtocolBuffers:
                    using (var stream = new MemoryStream(data))
                    {
                        share = Serializer.Deserialize<Share>(stream);
                        share.BlockReward = (decimal)share.BlockRewardDouble;
                    }

                    break;

                default:
                    logger.Error(() => $"Unsupported wire format {wireFormat} of share received from {url}/{topic} ");
                    break;
            }

            if (share == null)
            {
                logger.Error(() => $"Unable to deserialize share received from {url}/{topic}");
                return;
            }

            // store
            share.PoolId = topic;
            share.Created = clock.Now;
            messageBus.SendMessage(new ClientShare(null, share));

            // update poolstats from shares
            if (pools.TryGetValue(topic, out var poolContext))
            {
                var pool = poolContext.Pool;
                poolContext.Logger.Info(() => $"External {(!string.IsNullOrEmpty(share.Source) ? $"[{share.Source.ToUpper()}] " : string.Empty)}share accepted: D={Math.Round(share.Difficulty, 3)}");

                if (pool.NetworkStats != null)
                {
                    pool.NetworkStats.BlockHeight = (ulong)share.BlockHeight;
                    pool.NetworkStats.NetworkDifficulty = share.NetworkDifficulty;

                    if (poolContext.BlockHeight != share.BlockHeight)
                    {
                        pool.NetworkStats.LastNetworkBlockTime = clock.Now;
                        poolContext.BlockHeight = share.BlockHeight;
                        poolContext.LastBlock = clock.Now;
                    }

                    else
                        pool.NetworkStats.LastNetworkBlockTime = poolContext.LastBlock;
                }
            }

            else
                logger.Info(() => $"External {(!string.IsNullOrEmpty(share.Source) ? $"[{share.Source.ToUpper()}] " : string.Empty)}share accepted: D={Math.Round(share.Difficulty, 3)}");
        }

        #region API-Surface

        public void AttachPool(IMiningPool pool)
        {
            pools[pool.Config.Id] = new PoolContext(pool, LogUtil.GetPoolScopedLogger(typeof(ShareRecorder), pool.Config));
        }

        public void Start(ClusterConfig clusterConfig)
        {
            this.clusterConfig = clusterConfig;

            if (clusterConfig.ShareRelays != null)
            {
                StartMessageReceiver();
                StartMessageProcessors();
            }
        }

        public void Stop()
        {
            logger.Info(() => "Stopping ..");

            cts.Cancel();
            disposables.Dispose();

            logger.Info(() => "Stopped");
        }

        #endregion // API-Surface
    }
}
