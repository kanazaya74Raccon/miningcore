﻿using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MiningForce.Tests.Util
{
    public class Globals
    {
	    public static readonly JsonSerializerSettings JsonSerializerSettings = new JsonSerializerSettings
	    {
		    ContractResolver = new CamelCasePropertyNamesContractResolver()
	    };
    }
}
