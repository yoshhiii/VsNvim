using System.Buffers;
using VsNvim.Core.Rpc;
using Xunit;

namespace VsNvim.Core.Tests.Rpc;

public class RpcCodecTests
{
    [Fact]
    public void EncodeRequest_StartsWithType0()
    {
        byte[] frame = RpcCodec.EncodeRequest(7, "nvim_input", new object[] { "iabc" });
        // fixarray of 4 (0x94), then type byte 0x00
        Assert.Equal(0x94, frame[0]);
        Assert.Equal(0x00, frame[1]);
    }

    [Fact]
    public void TryDecode_Response_RoundTrips()
    {
        byte[] frame = RpcCodec.EncodeRequest(7, "m", new object[] { 1L });
        // craft a response [1, 7, nil, "ok"] by hand via the value writer through codec helper
        byte[] respFrame = RpcCodecTestHelper.EncodeResponse(7, error: null, result: "ok");

        bool ok = RpcCodec.TryDecode(new ReadOnlySequence<byte>(respFrame), out RpcMessage msg, out long consumed);

        Assert.True(ok);
        Assert.Equal(respFrame.Length, consumed);
        var resp = Assert.IsType<RpcResponse>(msg);
        Assert.Equal(7, resp.MsgId);
        Assert.Null(resp.Error);
        Assert.Equal("ok", resp.Result);
    }

    [Fact]
    public void TryDecode_Notification_RoundTrips()
    {
        byte[] frame = RpcCodec.EncodeNotification("redraw", new object[] { "x" });
        bool ok = RpcCodec.TryDecode(new ReadOnlySequence<byte>(frame), out RpcMessage msg, out long consumed);

        Assert.True(ok);
        var note = Assert.IsType<RpcNotification>(msg);
        Assert.Equal("redraw", note.Method);
        Assert.Equal(new object[] { "x" }, note.Arguments);
    }

    [Fact]
    public void TryDecode_IncompleteFrame_ReturnsFalse()
    {
        byte[] frame = RpcCodec.EncodeNotification("redraw", new object[] { "x" });
        var partial = new ReadOnlySequence<byte>(frame, 0, frame.Length - 1);
        bool ok = RpcCodec.TryDecode(partial, out _, out _);
        Assert.False(ok);
    }
}
