/*
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
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using MiningCore.Blockchain.Bitcoin;
using MiningCore.Blockchain.ZCash.DaemonResponses;
using MiningCore.Configuration;
using MiningCore.JsonRpc;
using MiningCore.Messaging;
using MiningCore.Persistence;
using MiningCore.Persistence.Repositories;
using MiningCore.Stratum;
using MiningCore.Time;
using MiningCore.Util;
using Newtonsoft.Json;

namespace MiningCore.Blockchain.ZCash
{
    public class ZCashPoolBase<TJob> : BitcoinPoolBase<TJob, ZCashBlockTemplate>
        where TJob : ZCashJob, new()
    {
        public ZCashPoolBase(IComponentContext ctx,
            JsonSerializerSettings serializerSettings,
            IConnectionFactory cf,
            IStatsRepository statsRepo,
            IMapper mapper,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus)
        {
        }

        private ZCashChainConfig chainConfig;
        private double hashrateDivisor;

        protected override BitcoinJobManager<TJob, ZCashBlockTemplate> CreateJobManager()
        {
            return ctx.Resolve<ZCashJobManager<TJob>>(
                new TypedParameter(typeof(IExtraNonceProvider), new ZCashExtraNonceProvider()));
        }

        #region Overrides of BitcoinPoolBase<TJob,ZCashBlockTemplate>

        /// <param name="ct"></param>
        /// <inheritdoc />
        protected override async Task SetupJobManager(CancellationToken ct)
        {
            await base.SetupJobManager(ct);

            if (ZCashConstants.Chains.TryGetValue(poolConfig.Coin.Type, out var coinbaseTx))
                coinbaseTx.TryGetValue(manager.NetworkType, out chainConfig);

            hashrateDivisor = (double) new BigRational(chainConfig.Diff1b,
                ZCashConstants.Chains[CoinType.ZEC][manager.NetworkType].Diff1b);
        }

        #endregion

        protected override async Task OnSubscribeAsync(StratumClient client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;
            var context = client.ContextAs<BitcoinWorkerContext>();

            if (request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var requestParams = request.ParamsAs<string[]>();

            var data = new object[]
                {
                    client.ConnectionId,
                }
                .Concat(manager.GetSubscriberData(client))
                .ToArray();

            await client.RespondAsync(data, request.Id);

            // setup worker context
            context.IsSubscribed = true;
            context.UserAgent = requestParams?.Length > 0 ? requestParams[0].Trim() : null;
        }

        protected override async Task OnAuthorizeAsync(StratumClient client, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
        {
            await base.OnAuthorizeAsync(client, tsRequest, ct);

            var context = client.ContextAs<BitcoinWorkerContext>();

            if (context.IsAuthorized)
            {
                // send intial update
                await client.NotifyAsync(ZCashStratumMethods.SetTarget, new object[] { EncodeTarget(context.Difficulty) });
                await client.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
            }
        }

        private async Task OnSuggestTargetAsync(StratumClient client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;
            var context = client.ContextAs<BitcoinWorkerContext>();

            if (request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var requestParams = request.ParamsAs<string[]>();
            var target = requestParams.FirstOrDefault();

            if (!string.IsNullOrEmpty(target))
            {
                if (System.Numerics.BigInteger.TryParse(target, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var targetBig))
                {
                    var newDiff = (double) new BigRational(chainConfig.Diff1b, targetBig);
                    var poolEndpoint = poolConfig.Ports[client.PoolEndpoint.Port];

                    if (newDiff >= poolEndpoint.Difficulty)
                    {
                        context.EnqueueNewDifficulty(newDiff);
                        context.ApplyPendingDifficulty();

                        await client.NotifyAsync(ZCashStratumMethods.SetTarget, new object[] { EncodeTarget(context.Difficulty) });
                    }

                    else
                        await client.RespondErrorAsync(StratumError.Other, "suggested difficulty too low", request.Id);
                }

                else
                    await client.RespondErrorAsync(StratumError.Other, "invalid target", request.Id);
            }

            else
                await client.RespondErrorAsync(StratumError.Other, "invalid target", request.Id);
        }

        protected override async Task OnRequestAsync(StratumClient client,
            Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
        {
            var request = tsRequest.Value;

            try
            {
                switch(request.Method)
                {
                    case BitcoinStratumMethods.Subscribe:
                        await OnSubscribeAsync(client, tsRequest);
                        break;

                    case BitcoinStratumMethods.Authorize:
                        await OnAuthorizeAsync(client, tsRequest, ct);
                        break;

                    case BitcoinStratumMethods.SubmitShare:
                        await OnSubmitAsync(client, tsRequest, ct);
                        break;

                    case ZCashStratumMethods.SuggestTarget:
                        await OnSuggestTargetAsync(client, tsRequest);
                        break;

                    case BitcoinStratumMethods.ExtraNonceSubscribe:
                        // ignored
                        break;

                    default:
                        logger.Debug(() => $"[{client.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                        await client.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                        break;
                }
            }

            catch(StratumException ex)
            {
                await client.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
            }
        }

        protected override Task OnNewJobAsync(object jobParams)
        {
            currentJobParams = jobParams;

            logger.Info(() => $"Broadcasting job");

            var tasks = ForEachClient(async client =>
            {
                if (!client.IsAlive)
                    return;

                var context = client.ContextAs<BitcoinWorkerContext>();

                if (context.IsSubscribed && context.IsAuthorized)
                {
                    // check alive
                    var lastActivityAgo = clock.Now - context.LastActivity;

                    if (poolConfig.ClientConnectionTimeout > 0 &&
                        lastActivityAgo.TotalSeconds > poolConfig.ClientConnectionTimeout)
                    {
                        logger.Info(() => $"[{client.ConnectionId}] Booting zombie-worker (idle-timeout exceeded)");
                        DisconnectClient(client);
                        return;
                    }

                    // varDiff: if the client has a pending difficulty change, apply it now
                    if (context.ApplyPendingDifficulty())
                        await client.NotifyAsync(ZCashStratumMethods.SetTarget, new object[] { EncodeTarget(context.Difficulty) });

                    // send job
                    await client.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
                }
            });

            return Task.WhenAll(tasks);
        }

        public override double HashrateFromShares(double shares, double interval)
        {
            var multiplier = BitcoinConstants.Pow2x32 / manager.ShareMultiplier;
            var result = shares * multiplier / interval / 1000000 * 2;

            result /= hashrateDivisor;
            return result;
        }

        protected override async Task OnVarDiffUpdateAsync(StratumClient client, double newDiff)
        {
            var context = client.ContextAs<BitcoinWorkerContext>();

            context.EnqueueNewDifficulty(newDiff);

            // apply immediately and notify client
            if (context.HasPendingDifficulty)
            {
                context.ApplyPendingDifficulty();

                await client.NotifyAsync(ZCashStratumMethods.SetTarget, new object[] { EncodeTarget(context.Difficulty) });
                await client.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
            }
        }

        private string EncodeTarget(double difficulty)
        {
            return ZCashUtils.EncodeTarget(difficulty, chainConfig);
        }
    }
}
