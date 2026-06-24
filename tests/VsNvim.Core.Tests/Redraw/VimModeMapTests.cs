using VsNvim.Core.Redraw;
using Xunit;

namespace VsNvim.Core.Tests.Redraw;

public class VimModeMapTests
{
    [Theory]
    [InlineData("n", VimMode.Normal)]
    [InlineData("niI", VimMode.Normal)]
    [InlineData("no", VimMode.OperatorPending)]
    [InlineData("nov", VimMode.OperatorPending)]
    [InlineData("i", VimMode.Insert)]
    [InlineData("ic", VimMode.Insert)]
    [InlineData("R", VimMode.Replace)]
    [InlineData("v", VimMode.Visual)]
    [InlineData("V", VimMode.VisualLine)]
    [InlineData("c", VimMode.CommandLine)]
    [InlineData("t", VimMode.Terminal)]
    [InlineData("", VimMode.Unknown)]
    [InlineData("zz", VimMode.Unknown)]
    public void FromModeCode_MapsKnownModes(string code, VimMode expected)
        => Assert.Equal(expected, VimModeMap.FromModeCode(code));

    // CTRL-V (0x16) can't be written as an InlineData string literal, so test it separately.
    [Fact]
    public void FromModeCode_CtrlV_IsVisualBlock()
        => Assert.Equal(VimMode.VisualBlock, VimModeMap.FromModeCode(((char)0x16).ToString()));
}
