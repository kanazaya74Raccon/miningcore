﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MiningForce.JsonRpc
{
    [JsonObject(MemberSerialization.OptIn)]
    public class JsonRpcResponse : JsonRpcResponse<object>
    {
        public JsonRpcResponse()
        {
        }

        public JsonRpcResponse(object result, string id = null) : base(result, id)
        {
        }

        public JsonRpcResponse(JsonRpcException ex, string id = null, object result = null) : base(ex, id)
        {
        }
    }

    /// <summary>
    /// Represents a Json Rpc Response
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class JsonRpcResponse<T>
    {
        public JsonRpcResponse()
        {
        }

        public JsonRpcResponse(T result, string id = null)
        {
            Result = JToken.FromObject(result);
            Id = id;
        }

        public JsonRpcResponse(JsonRpcException ex, string id, object result)
        {
            Error = ex;
            Id = id;
	        Result = JToken.FromObject(result);
        }

        //[JsonProperty(PropertyName = "jsonrpc")]
        //public string JsonRpc => "2.0";

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, PropertyName = "result", Order = 2)]
        public JToken Result { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, PropertyName = "error", Order = 3)]
        public JsonRpcException Error { get; set; }

        [JsonProperty(PropertyName = "id", Order = 1)]
        public string Id { get; set; }
    }
}
