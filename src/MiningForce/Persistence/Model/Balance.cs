﻿using System;

namespace MiningForce.Persistence.Model
{
	public class Balance
	{
		public string Coin { get; set; }
		public string Wallet { get; set; }
		public double Amount { get; set; }
		public DateTime Created { get; set; }
		public DateTime Updated { get; set; }
	}
}
