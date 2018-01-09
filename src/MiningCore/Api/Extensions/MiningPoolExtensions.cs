using System.Linq;
using AutoMapper;
using MiningCore.Api.Responses;
using MiningCore.Blockchain;
using MiningCore.Configuration;
using MiningCore.Mining;

namespace MiningCore.Api.Extensions
{
    public static class MiningPoolExtensions
    {
        public static PoolInfo ToPoolInfo(this PoolConfig pool, IMapper mapper)
        {
            var poolInfo = mapper.Map<PoolInfo>(pool);

            // pool wallet link
            CoinMetaData.AddressInfoLinks.TryGetValue(pool.Coin.Type, out var addressInfobaseUrl);
            if (!string.IsNullOrEmpty(addressInfobaseUrl))
                poolInfo.AddressInfoLink = string.Format(addressInfobaseUrl, poolInfo.Address);

            // pool fees
            poolInfo.PoolFeePercent = (float)pool.RewardRecipients.Sum(x => x.Percentage);

            return poolInfo;
        }
    }
}