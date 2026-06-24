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
