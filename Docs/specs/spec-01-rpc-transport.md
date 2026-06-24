# Spec 01 — RPC Transport (`VsNvim.Core.Rpc`)

> Inherits all **Global Constraints** from [`../implementation-plan.md`](../implementation-plan.md).

**Goal:** A msgpack-RPC layer that encodes/decodes the nvim-safe value set, frames msgpack-RPC messages, and correlates requests with responses over a `Stream` — with zero VS dependency, fully unit-tested.

**Files:**
- Create: `src/VsNvim.Core/Rpc/MsgPackValueWriter.cs`
- Create: `src/VsNvim.Core/Rpc/MsgPackValueReader.cs`
- Create: `src/VsNvim.Core/Rpc/RpcMessage.cs`
- Create: `src/VsNvim.Core/Rpc/RpcCodec.cs`
- Create: `src/VsNvim.Core/Rpc/NeovimRpcClient.cs`
- Modify: `src/VsNvim.Core/VsNvim.Core.csproj` (package ref + InternalsVisibleTo)
- Test: `tests/VsNvim.Core.Tests/Rpc/MsgPackValueRoundTripTests.cs`
- Test: `tests/VsNvim.Core.Tests/Rpc/RpcCodecTests.cs`
- Test: `tests/VsNvim.Core.Tests/Rpc/NeovimRpcClientTests.cs`

**Interfaces:**
- Produces, consumed by Specs 02 / 05 / 04:
  - `byte[] MsgPackValueWriter.Encode(object value)` / `void WriteValue(ref MessagePackWriter, object)`
  - `object MsgPackValueReader.ReadValue(ref MessagePackReader)` (returns `null|bool|long|double|string|byte[]|object[]|Dictionary<string,object>`)
  - `RpcMessage` (`RpcRequest{long MsgId,string Method,object[] Arguments}`, `RpcResponse{long MsgId,object Error,object Result}`, `RpcNotification{string Method,object[] Arguments}`)
  - `RpcCodec.EncodeRequest/EncodeNotification`, `bool RpcCodec.TryDecode(ReadOnlySequence<byte>, out RpcMessage, out long consumed)`
  - `NeovimRpcClient(Stream)` with `Task<object> RequestAsync(string, object[], CancellationToken)`, `Task NotifyAsync(string, object[], CancellationToken)`, `Task RunReadLoopAsync(CancellationToken)`, `event Action<RpcNotification> NotificationReceived`

---

## Task 1: Project setup + msgpack value writer

**Files:** Modify `src/VsNvim.Core/VsNvim.Core.csproj`; Create `src/VsNvim.Core/Rpc/MsgPackValueWriter.cs`; Test `tests/VsNvim.Core.Tests/Rpc/MsgPackValueRoundTripTests.cs`.

- [ ] **Step 1: Add the MessagePack package and test visibility**

Run:
```bash
dotnet add src/VsNvim.Core/VsNvim.Core.csproj package MessagePack --version 2.5.198
```
Then edit `src/VsNvim.Core/VsNvim.Core.csproj` so the `<PropertyGroup>` and an item group contain:
```xml
  <ItemGroup>
    <InternalsVisibleTo Include="VsNvim.Core.Tests" />
  </ItemGroup>
```
Expected: `dotnet restore` succeeds; MessagePack 2.5.x restored (must remain `< 3.0`, which dropped `netstandard2.0`).

- [ ] **Step 2: Write the failing test**

Create `tests/VsNvim.Core.Tests/Rpc/MsgPackValueRoundTripTests.cs`:
```csharp
using VsNvim.Core.Rpc;
using Xunit;

namespace VsNvim.Core.Tests.Rpc;

public class MsgPackValueRoundTripTests
{
    [Fact]
    public void Encode_PositiveFixint_ProducesSingleByte()
    {
        byte[] bytes = MsgPackValueWriter.Encode(5L);
        Assert.Equal(new byte[] { 0x05 }, bytes);
    }

    [Fact]
    public void Encode_ShortString_ProducesFixstr()
    {
        byte[] bytes = MsgPackValueWriter.Encode("hi");
        Assert.Equal(new byte[] { 0xa2, 0x68, 0x69 }, bytes); // fixstr len 2 + "hi"
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~MsgPackValueRoundTripTests"`
Expected: FAIL — `MsgPackValueWriter` does not exist (compile error).

- [ ] **Step 4: Write minimal implementation**

Create `src/VsNvim.Core/Rpc/MsgPackValueWriter.cs`:
```csharp
using System;
using System.Buffers;
using System.Collections.Generic;
using MessagePack;

namespace VsNvim.Core.Rpc
{
    /// <summary>Serializes the nvim-safe value set to msgpack. No ext/typeless encoding.</summary>
    public static class MsgPackValueWriter
    {
        public static void WriteValue(ref MessagePackWriter writer, object value)
        {
            switch (value)
            {
                case null:
                    writer.WriteNil();
                    break;
                case bool b:
                    writer.Write(b);
                    break;
                case int i:
                    writer.Write((long)i);
                    break;
                case long l:
                    writer.Write(l);
                    break;
                case double d:
                    writer.Write(d);
                    break;
                case string s:
                    writer.Write(s);
                    break;
                case byte[] bytes:
                    writer.Write(bytes);
                    break;
                case object[] array:
                    writer.WriteArrayHeader(array.Length);
                    foreach (var item in array)
                        WriteValue(ref writer, item);
                    break;
                case IDictionary<string, object> map:
                    writer.WriteMapHeader(map.Count);
                    foreach (var kv in map)
                    {
                        writer.Write(kv.Key);
                        WriteValue(ref writer, kv.Value);
                    }
                    break;
                default:
                    throw new NotSupportedException(
                        $"RPC value type not allowed by nvim-safe constraint: {value.GetType()}");
            }
        }

        public static byte[] Encode(object value)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            WriteValue(ref writer, value);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~MsgPackValueRoundTripTests"`
Expected: PASS (2 passed).

- [ ] **Step 6: Commit**

```bash
git add src/VsNvim.Core/ tests/VsNvim.Core.Tests/Rpc/MsgPackValueRoundTripTests.cs
git commit -m "feat: add nvim-safe msgpack value writer"
```

---

## Task 2: msgpack value reader (round-trip)

**Files:** Create `src/VsNvim.Core/Rpc/MsgPackValueReader.cs`; Modify the round-trip test.

- [ ] **Step 1: Write the failing test** (append to `MsgPackValueRoundTripTests.cs`)

```csharp
    [Theory]
    [MemberData(nameof(Values))]
    public void RoundTrip_PreservesValue(object value)
    {
        byte[] bytes = MsgPackValueWriter.Encode(value);
        var reader = new MessagePack.MessagePackReader(bytes);
        object result = MsgPackValueReader.ReadValue(ref reader);
        Assert.Equal(value, result);
    }

    public static IEnumerable<object[]> Values() => new[]
    {
        new object[] { 42L },
        new object[] { -7L },
        new object[] { true },
        new object[] { "hello" },
        new object[] { 3.5d },
        new object[] { new object[] { 1L, "two", false } },
        new object[] { new Dictionary<string, object> { ["k"] = 1L, ["s"] = "v" } },
    };
```
Add `using System.Collections.Generic;` to the test file.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~RoundTrip_PreservesValue"`
Expected: FAIL — `MsgPackValueReader` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/VsNvim.Core/Rpc/MsgPackValueReader.cs`:
```csharp
using System;
using System.Buffers;
using System.Collections.Generic;
using MessagePack;

namespace VsNvim.Core.Rpc
{
    /// <summary>Deserializes msgpack into the nvim-safe CLR value set.</summary>
    public static class MsgPackValueReader
    {
        public static object ReadValue(ref MessagePackReader reader)
        {
            switch (reader.NextMessagePackType)
            {
                case MessagePackType.Nil:
                    reader.ReadNil();
                    return null;
                case MessagePackType.Boolean:
                    return reader.ReadBoolean();
                case MessagePackType.Integer:
                    return reader.ReadInt64();
                case MessagePackType.Float:
                    return reader.ReadDouble();
                case MessagePackType.String:
                    return reader.ReadString();
                case MessagePackType.Binary:
                    ReadOnlySequence<byte> bin = reader.ReadBytes() ?? default;
                    return bin.ToArray();
                case MessagePackType.Array:
                {
                    int len = reader.ReadArrayHeader();
                    var array = new object[len];
                    for (int i = 0; i < len; i++)
                        array[i] = ReadValue(ref reader);
                    return array;
                }
                case MessagePackType.Map:
                {
                    int len = reader.ReadMapHeader();
                    var map = new Dictionary<string, object>(len);
                    for (int i = 0; i < len; i++)
                    {
                        string key = reader.ReadString();
                        map[key] = ReadValue(ref reader);
                    }
                    return map;
                }
                default:
                    throw new NotSupportedException(
                        $"Unsupported msgpack type from nvim: {reader.NextMessagePackType}");
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~RoundTrip_PreservesValue"`
Expected: PASS (7 cases).

- [ ] **Step 5: Commit**

```bash
git add src/VsNvim.Core/Rpc/MsgPackValueReader.cs tests/VsNvim.Core.Tests/Rpc/MsgPackValueRoundTripTests.cs
git commit -m "feat: add nvim-safe msgpack value reader with round-trip coverage"
```

---

## Task 3: RPC message types + frame codec

**Files:** Create `src/VsNvim.Core/Rpc/RpcMessage.cs`, `src/VsNvim.Core/Rpc/RpcCodec.cs`; Test `tests/VsNvim.Core.Tests/Rpc/RpcCodecTests.cs`.

- [ ] **Step 1: Write the failing test**

Create `tests/VsNvim.Core.Tests/Rpc/RpcCodecTests.cs`:
```csharp
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
```

- [ ] **Step 2: Add the test helper for crafting responses**

Create `tests/VsNvim.Core.Tests/Rpc/RpcCodecTestHelper.cs`:
```csharp
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
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~RpcCodecTests"`
Expected: FAIL — `RpcMessage`/`RpcCodec` do not exist.

- [ ] **Step 4: Write minimal implementation**

Create `src/VsNvim.Core/Rpc/RpcMessage.cs`:
```csharp
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
```

Create `src/VsNvim.Core/Rpc/RpcCodec.cs`:
```csharp
using System;
using System.Buffers;
using System.IO;
using MessagePack;

namespace VsNvim.Core.Rpc
{
    /// <summary>Encodes/decodes msgpack-RPC frames: [0,id,method,args] / [1,id,err,res] / [2,method,args].</summary>
    public static class RpcCodec
    {
        public static byte[] EncodeRequest(long msgId, string method, object[] args)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(4);
            writer.Write(0L);
            writer.Write(msgId);
            writer.Write(method);
            MsgPackValueWriter.WriteValue(ref writer, args ?? Array.Empty<object>());
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        public static byte[] EncodeNotification(string method, object[] args)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(3);
            writer.Write(2L);
            writer.Write(method);
            MsgPackValueWriter.WriteValue(ref writer, args ?? Array.Empty<object>());
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        /// <summary>Tries to decode one frame. Returns false (consumed=0) when the buffer holds a partial frame.</summary>
        public static bool TryDecode(ReadOnlySequence<byte> buffer, out RpcMessage message, out long consumed)
        {
            message = null;
            consumed = 0;
            if (buffer.IsEmpty)
                return false;

            var reader = new MessagePackReader(buffer);
            try
            {
                int count = reader.ReadArrayHeader();
                long type = reader.ReadInt64();
                switch (type)
                {
                    case 0:
                    {
                        long id = reader.ReadInt64();
                        string method = reader.ReadString();
                        object[] args = (object[])MsgPackValueReader.ReadValue(ref reader);
                        message = new RpcRequest(id, method, args);
                        break;
                    }
                    case 1:
                    {
                        long id = reader.ReadInt64();
                        object err = MsgPackValueReader.ReadValue(ref reader);
                        object res = MsgPackValueReader.ReadValue(ref reader);
                        message = new RpcResponse(id, err, res);
                        break;
                    }
                    case 2:
                    {
                        string method = reader.ReadString();
                        object[] args = (object[])MsgPackValueReader.ReadValue(ref reader);
                        message = new RpcNotification(method, args);
                        break;
                    }
                    default:
                        throw new InvalidDataException($"Unknown msgpack-rpc message type: {type}");
                }
            }
            catch (EndOfStreamException)
            {
                // Not enough bytes yet for a full frame.
                message = null;
                return false;
            }

            consumed = reader.Consumed;
            return true;
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~RpcCodecTests"`
Expected: PASS (4 passed).

- [ ] **Step 6: Commit**

```bash
git add src/VsNvim.Core/Rpc/RpcMessage.cs src/VsNvim.Core/Rpc/RpcCodec.cs tests/VsNvim.Core.Tests/Rpc/
git commit -m "feat: add msgpack-rpc frame codec and message types"
```

---

## Task 4: NeovimRpcClient (request/response correlation + notifications)

**Files:** Create `src/VsNvim.Core/Rpc/NeovimRpcClient.cs`; Test `tests/VsNvim.Core.Tests/Rpc/NeovimRpcClientTests.cs`.

**Interfaces — Produces:** `NeovimRpcClient(Stream)`, `Task<object> RequestAsync(string, object[], CancellationToken)`, `Task NotifyAsync(string, object[], CancellationToken)`, `Task RunReadLoopAsync(CancellationToken)`, `event Action<RpcNotification> NotificationReceived`. Correlation tested via the internal `Dispatch(RpcMessage)` hook (InternalsVisibleTo from Task 1).

- [ ] **Step 1: Write the failing test**

Create `tests/VsNvim.Core.Tests/Rpc/NeovimRpcClientTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~NeovimRpcClientTests"`
Expected: FAIL — `NeovimRpcClient`/`NeovimRpcException` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/VsNvim.Core/Rpc/NeovimRpcClient.cs`:
```csharp
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VsNvim.Core.Rpc
{
    public sealed class NeovimRpcException : Exception
    {
        public NeovimRpcException(object error) : base(Describe(error)) { Error = error; }
        public object Error { get; }
        private static string Describe(object error) =>
            error is object[] a && a.Length == 2 ? $"nvim error {a[0]}: {a[1]}" : $"nvim error: {error}";
    }

    public sealed class NeovimRpcClient : IDisposable
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcResponse>> _pending =
            new ConcurrentDictionary<long, TaskCompletionSource<RpcResponse>>();
        private long _nextMsgId;

        public NeovimRpcClient(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        public event Action<RpcNotification> NotificationReceived;

        public async Task<object> RequestAsync(string method, object[] args, CancellationToken ct)
        {
            long id = Interlocked.Increment(ref _nextMsgId);
            var tcs = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            await WriteFrameAsync(RpcCodec.EncodeRequest(id, method, args), ct).ConfigureAwait(false);

            using (ct.Register(() => { if (_pending.TryRemove(id, out var t)) t.TrySetCanceled(); }))
            {
                RpcResponse response = await tcs.Task.ConfigureAwait(false);
                if (response.Error != null)
                    throw new NeovimRpcException(response.Error);
                return response.Result;
            }
        }

        public Task NotifyAsync(string method, object[] args, CancellationToken ct) =>
            WriteFrameAsync(RpcCodec.EncodeNotification(method, args), ct);

        private async Task WriteFrameAsync(byte[] frame, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>Reads frames off the stream until cancelled. Covered by Spec 04 integration smoke test.</summary>
        public async Task RunReadLoopAsync(CancellationToken ct)
        {
            var accumulated = new byte[0];
            var chunk = new byte[8192];
            while (!ct.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
                if (read == 0)
                    return; // pipe closed

                int oldLen = accumulated.Length;
                Array.Resize(ref accumulated, oldLen + read);
                Array.Copy(chunk, 0, accumulated, oldLen, read);

                while (RpcCodec.TryDecode(new ReadOnlySequence<byte>(accumulated), out RpcMessage msg, out long consumed))
                {
                    Dispatch(msg);
                    int remaining = accumulated.Length - (int)consumed;
                    var rest = new byte[remaining];
                    Array.Copy(accumulated, (int)consumed, rest, 0, remaining);
                    accumulated = rest;
                }
            }
        }

        internal void Dispatch(RpcMessage message)
        {
            switch (message)
            {
                case RpcResponse response:
                    if (_pending.TryRemove(response.MsgId, out var tcs))
                        tcs.TrySetResult(response);
                    break;
                case RpcNotification notification:
                    NotificationReceived?.Invoke(notification);
                    break;
                // RpcRequest (nvim calling us) is unused in the MVP; ignore.
            }
        }

        public void Dispose() => _writeLock.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~NeovimRpcClientTests"`
Expected: PASS (3 passed).

- [ ] **Step 5: Run the full Core suite**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj`
Expected: PASS (all Spec 01 tests green).

- [ ] **Step 6: Commit**

```bash
git add src/VsNvim.Core/Rpc/NeovimRpcClient.cs tests/VsNvim.Core.Tests/Rpc/NeovimRpcClientTests.cs
git commit -m "feat: add NeovimRpcClient with request/response correlation and notifications"
```

---

## Done when
- All four tasks committed; `dotnet test` green.
- `NeovimRpcClient` can issue requests and surface notifications over any `Stream`. The live read loop is exercised against a real `nvim --embed` process in Spec 04.
