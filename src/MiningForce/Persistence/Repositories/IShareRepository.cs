﻿using System;
using System.Data;

namespace MiningForce.Persistence.Repositories
{
    public interface IShareRepository
	{
		void Insert(IDbConnection con, IDbTransaction tx, Model.Share share);
		Model.Share[] PageSharesBefore(IDbConnection con, DateTime before, int page, int pageSize);
	}
}
