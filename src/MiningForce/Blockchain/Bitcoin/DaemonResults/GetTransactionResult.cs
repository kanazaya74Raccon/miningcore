﻿using System;
using Newtonsoft.Json;

namespace MiningForce.Blockchain.Bitcoin.DaemonResults
{
	public class TransactionDetails
	{
		public string Address { get; set; }
		public string Category { get; set; }
		public double Amount { get; set; }
		public string Label { get; set; }
		public int Vout { get; set; }
	}

	public class GetTransactionResult
	{
		public double Amount { get; set; }
		public uint Confirmations { get; set; }
		public bool Generated { get; set; }
		public string BlockHash { get; set; }
		public long BlockIndex { get; set; }
		public ulong BlockTime { get; set; }
		public string TxId { get; set; }
		public string[] WalletConflicts { get; set; }
		public ulong Time { get; set; }
		public ulong TimeReceived { get; set; }

		[JsonProperty("bip125-replaceable")]
		public string Bip125Replaceable { get; set; }

		public TransactionDetails[] Details { get; set; }
	}
}
