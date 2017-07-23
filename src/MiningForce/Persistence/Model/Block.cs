﻿using System;
using MiningForce.Configuration;

namespace MiningForce.Persistence.Model
{
	public class Block
	{
		public string PoolId { get; set; }
		public long Blockheight { get; set; }
		public string Status { get; set; }
		public string TransactionConfirmationData { get; set; }
		public DateTime Created { get; set; }

		public const string StatusPending = "pending";
	}
}
