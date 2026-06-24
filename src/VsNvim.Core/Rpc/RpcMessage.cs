namespace VsNvim.Core.Rpc
{
    public abstract class RpcMessage { }

    public sealed class RpcRequest : RpcMessage
    {
        public RpcRequest(long msgId, string method, object[] arguments)
        {
            MsgId = msgId;
            Method = method;
            Arguments = arguments;
        }
        public long MsgId { get; }
        public string Method { get; }
        public object[] Arguments { get; }
    }

    public sealed class RpcResponse : RpcMessage
    {
        public RpcResponse(long msgId, object error, object result)
        {
            MsgId = msgId;
            Error = error;
            Result = result;
        }
        public long MsgId { get; }
        public object Error { get; }
        public object Result { get; }
    }

    public sealed class RpcNotification : RpcMessage
    {
        public RpcNotification(string method, object[] arguments)
        {
            Method = method;
            Arguments = arguments;
        }
        public string Method { get; }
        public object[] Arguments { get; }
    }
}
