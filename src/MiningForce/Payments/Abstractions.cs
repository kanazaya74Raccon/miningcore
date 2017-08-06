﻿using System.Data;
using System.Threading.Tasks;
using MiningForce.Configuration;
using MiningForce.Persistence.Model;

namespace MiningForce.Payments
{
	public interface IPayoutHandler
	{
		void Configure(ClusterConfig clusterConfig, PoolConfig poolConfig);

		Task<Block[]> ClassifyBlocksAsync(Block[] blocks);
		Task UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, Block block, PoolConfig pool);
		Task PayoutAsync(Balance[] balances);

		string FormatAmount(decimal amount);
	}

	public interface IPayoutScheme
	{
		Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, PoolConfig poolConfig, IPayoutHandler payoutHandler, Block block);
	}
}
