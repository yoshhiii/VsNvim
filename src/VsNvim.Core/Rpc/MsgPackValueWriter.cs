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
            var buffer = new MsgPackBufferWriter();
            var writer = new MessagePackWriter(buffer);
            WriteValue(ref writer, value);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }
    }
}
