﻿using System;
using System.Collections.Generic;
using System.Text;
using MiningForce.Configuration;

namespace MiningForce.Persistence.Model
{
	public class Share
	{
		public long Id { get; set; }
		public string PoolId { get; set; }
		public long Blockheight { get; set; }
		public string Worker { get; set; }
		public double Difficulty { get; set; }
		public double NetworkDifficulty { get; set; }
		public string IpAddress { get; set; }
		public DateTime Created { get; set; }
	}
}
