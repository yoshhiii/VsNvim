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
