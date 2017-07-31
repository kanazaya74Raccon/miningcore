﻿using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Autofac;
using MiningForce.Authorization;
using MiningForce.Blockchain;
using MiningForce.Blockchain.Bitcoin;
using MiningForce.Blockchain.DaemonInterface;
using MiningForce.Blockchain.Monero;
using MiningForce.Configuration;
using MiningForce.JsonRpc;
using MiningForce.MininigPool;
using MiningForce.Networking.Banning;
using MiningForce.Payments;
using MiningForce.Payments.PayoutSchemes;
using MiningForce.Persistence;
using MiningForce.Stratum;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Module = Autofac.Module;

namespace MiningForce
{
    public class AutofacModule : Module
    {
        /// <summary>
        /// Override to add registrations to the container.
        /// </summary>
        /// <remarks>
        /// Note that the ContainerBuilder parameter is unique to this module.
        /// </remarks>
        /// <param name="builder">The builder through which components can be registered.</param>
        protected override void Load(ContainerBuilder builder)
        {
            var thisAssembly = typeof(AutofacModule).GetTypeInfo().Assembly;

            builder.Register(c =>
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip
                };

                return new HttpClient(handler);
            })
            .AsSelf();

            builder.RegisterInstance(new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });

            builder.RegisterType<JsonRpcConnection>()
                .AsSelf();

            builder.RegisterType<StratumClient>()
                .AsSelf();

            builder.RegisterType<Pool>()
                .AsSelf();

            builder.RegisterType<DaemonClient>()
                .AsSelf();

	        builder.RegisterType<PaymentProcessor>()
		        .AsSelf()
				.SingleInstance();

			builder.RegisterType<AddressBasedWorkerAuthorizer>()
                .Named<IWorkerAuthorizer>(StratumAuthorizerKind.AddressBased.ToString())
                .SingleInstance();

            builder.RegisterType<ExtraNonceProvider>()
                .AsSelf();

	        builder.RegisterType<IntegratedBanManager>()
		        .Keyed<IBanManager>(BanManagerKind.Integrated)
				.SingleInstance();

	        builder.RegisterType<ShareRecorder>()
		        .SingleInstance();

			//////////////////////
			// Payment Schemes

	        builder.RegisterType<PayPerLastNShares>()
		        .Keyed<IPayoutScheme>(PayoutScheme.PPLNS)
		        .SingleInstance();

			//////////////////////
			// Bitcoin and family

			builder.RegisterType<BitcoinJobManager>()
		        .Keyed<IBlockchainJobManager>(CoinType.BTC)
		        .Keyed<IBlockchainJobManager>(CoinType.LTC);

			builder.RegisterType<BitcoinPayoutHandler>()
				.Keyed<IPayoutHandler>(CoinType.BTC)
				.Keyed<IPayoutHandler>(CoinType.LTC);

	        //////////////////////
	        // Monero

	        builder.RegisterType<MoneroJobManager>()
		        .Keyed<IBlockchainJobManager>(CoinType.XMR);

	        builder.RegisterType<MoneroPayoutHandler>()
		        .Keyed<IPayoutHandler>(CoinType.XMR);

			base.Load(builder);
        }
    }
}
