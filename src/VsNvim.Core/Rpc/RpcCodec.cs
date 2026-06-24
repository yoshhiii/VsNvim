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
            var buffer = new MsgPackBufferWriter();
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
            var buffer = new MsgPackBufferWriter();
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
