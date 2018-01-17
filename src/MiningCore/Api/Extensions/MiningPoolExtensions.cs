using System.Linq;
using AutoMapper;
using MiningCore.Api.Responses;
using MiningCore.Blockchain;
using MiningCore.Blockchain.Ethereum.Configuration;
using MiningCore.Configuration;
using MiningCore.Extensions;
using MiningCore.Mining;

namespace MiningCore.Api.Extensions
{
    public static class MiningPoolExtensions
    {
        public static PoolInfo ToPoolInfo(this PoolConfig pool, IMapper mapper, Persistence.Model.PoolStats stats)
        {
            var poolInfo = mapper.Map<PoolInfo>(pool);

            poolInfo.PoolStats = mapper.Map<PoolStats>(stats);
            poolInfo.NetworkStats = mapper.Map<BlockchainStats>(stats);

            // pool wallet link
            CoinMetaData.AddressInfoLinks.TryGetValue(pool.Coin.Type, out var addressInfobaseUrl);
            if (!string.IsNullOrEmpty(addressInfobaseUrl))
                poolInfo.AddressInfoLink = string.Format(addressInfobaseUrl, poolInfo.Address);

            // pool fees
            poolInfo.PoolFeePercent = (float)pool.RewardRecipients.Sum(x => x.Percentage);

            // strip security critical stuff
            if (poolInfo.PaymentProcessing.Extra != null)
            {
                var extra = poolInfo.PaymentProcessing.Extra;

                extra.StripValue(nameof(EthereumPoolPaymentProcessingConfigExtra.CoinbasePassword));
            }

            return poolInfo;
        }
    }
}