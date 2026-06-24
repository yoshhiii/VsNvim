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
