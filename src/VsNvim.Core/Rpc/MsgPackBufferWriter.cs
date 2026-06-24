using System;
using System.Buffers;

namespace VsNvim.Core.Rpc
{
    /// <summary>
    /// Minimal growable <see cref="IBufferWriter{T}"/> for msgpack output.
    /// netstandard2.0 has no built-in <c>ArrayBufferWriter&lt;T&gt;</c> (added in netstandard2.1), so we provide one.
    /// </summary>
    internal sealed class MsgPackBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer;
        private int _written;

        public MsgPackBufferWriter(int initialCapacity = 256)
        {
            if (initialCapacity < 1)
                initialCapacity = 1;
            _buffer = new byte[initialCapacity];
        }

        public ReadOnlySpan<byte> WrittenSpan => new ReadOnlySpan<byte>(_buffer, 0, _written);

        public void Advance(int count)
        {
            if (count < 0 || _written + count > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return new Memory<byte>(_buffer, _written, _buffer.Length - _written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return new Span<byte>(_buffer, _written, _buffer.Length - _written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 1)
                sizeHint = 1;
            int needed = _written + sizeHint;
            if (needed > _buffer.Length)
            {
                int newSize = Math.Max(_buffer.Length * 2, needed);
                Array.Resize(ref _buffer, newSize);
            }
        }
    }
}
