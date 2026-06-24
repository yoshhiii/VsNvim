using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VsNvim.Core.Rpc;
using Xunit;

namespace VsNvim.Core.Tests.Rpc;

public class NeovimRpcClientTests
{
    [Fact]
    public async Task RequestAsync_CompletesWhenMatchingResponseDispatched()
    {
        using var client = new NeovimRpcClient(new MemoryStream());

        Task<object> pending = client.RequestAsync("nvim_eval", new object[] { "1+1" }, CancellationToken.None);
        Assert.False(pending.IsCompleted);

        // First request gets msgid 1.
        client.Dispatch(new RpcResponse(1, error: null, result: 2L));

        object result = await pending;
        Assert.Equal(2L, result);
    }

    [Fact]
    public async Task RequestAsync_ThrowsWhenResponseHasError()
    {
        using var client = new NeovimRpcClient(new MemoryStream());
        Task<object> pending = client.RequestAsync("bad", Array.Empty<object>(), CancellationToken.None);

        client.Dispatch(new RpcResponse(1, error: new object[] { 0L, "boom" }, result: null));

        await Assert.ThrowsAsync<NeovimRpcException>(() => pending);
    }

    [Fact]
    public void Dispatch_Notification_RaisesEvent()
    {
        using var client = new NeovimRpcClient(new MemoryStream());
        RpcNotification received = null;
        client.NotificationReceived += n => received = n;

        client.Dispatch(new RpcNotification("redraw", new object[] { "x" }));

        Assert.NotNull(received);
        Assert.Equal("redraw", received.Method);
    }
}
