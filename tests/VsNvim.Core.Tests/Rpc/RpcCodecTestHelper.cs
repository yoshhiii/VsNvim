using System.Buffers;
using MessagePack;
using VsNvim.Core.Rpc;

namespace VsNvim.Core.Tests.Rpc;

internal static class RpcCodecTestHelper
{
    public static byte[] EncodeResponse(long msgId, object error, object result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(4);
        writer.Write(1L);            // type = response
        writer.Write(msgId);
        MsgPackValueWriter.WriteValue(ref writer, error);
        MsgPackValueWriter.WriteValue(ref writer, result);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
