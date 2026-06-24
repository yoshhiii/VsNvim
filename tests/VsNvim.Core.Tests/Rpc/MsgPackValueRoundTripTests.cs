using System.Collections.Generic;
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
}
