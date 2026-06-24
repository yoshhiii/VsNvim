using VsNvim.Core.Text;
using Xunit;

namespace VsNvim.Core.Tests.Text;

public class CoordinateMapperTests
{
    [Theory]
    // ASCII: byte col == char col
    [InlineData("hello", 3, 3)]
    [InlineData("hello", 5, 5)]
    // "café": c,a,f are 1 byte each; é (U+00E9) is 2 bytes. Total 5 bytes, 4 chars.
    [InlineData("café", 3, 3)] // char 3 -> byte 3 (start of é)
    [InlineData("café", 4, 5)] // char 4 (end) -> byte 5
    // "a😀b": a=1 byte/1 char; 😀=4 bytes/2 chars (surrogate pair); b=1/1. 6 bytes, 4 chars.
    [InlineData("a😀b", 1, 1)] // after 'a'
    [InlineData("a😀b", 3, 5)] // after 'a' + surrogate pair
    [InlineData("a😀b", 4, 6)] // end
    public void CharToByteColumn_Converts(string line, int charCol, int expectedByteCol)
        => Assert.Equal(expectedByteCol, CoordinateMapper.CharToByteColumn(line, charCol));

    [Theory]
    [InlineData("hello", 3, 3)]
    [InlineData("café", 5, 4)] // byte 5 -> char 4 (end)
    [InlineData("café", 3, 3)] // byte 3 -> char 3 (start of é)
    [InlineData("a😀b", 1, 1)]
    [InlineData("a😀b", 5, 3)] // byte 5 -> char 3 (after surrogate pair)
    [InlineData("a😀b", 6, 4)] // end
    public void ByteToCharColumn_Converts(string line, int byteCol, int expectedCharCol)
        => Assert.Equal(expectedCharCol, CoordinateMapper.ByteToCharColumn(line, byteCol));

    [Fact]
    public void RoundTrip_Holds_ForMultibyteLine()
    {
        const string line = "x café 😀 y";
        for (int charCol = 0; charCol <= line.Length; charCol++)
        {
            // A column that lands on a low surrogate splits a surrogate pair (e.g. inside 😀).
            // That is not a valid caret position and has no byte offset, so skip it.
            if (charCol < line.Length && char.IsLowSurrogate(line[charCol]))
                continue;

            int byteCol = CoordinateMapper.CharToByteColumn(line, charCol);
            Assert.Equal(charCol, CoordinateMapper.ByteToCharColumn(line, byteCol));
        }
    }

    [Fact]
    public void ToVs_And_ToNvim_RoundTrip()
    {
        const string line = "café 😀";
        var vs = new VsPosition(2, 4);
        NvimPosition nvim = CoordinateMapper.ToNvim(vs, line);
        Assert.Equal(3, nvim.Row);   // 0-based line 2 -> 1-based row 3
        VsPosition back = CoordinateMapper.ToVs(nvim, line);
        Assert.Equal(vs.Line, back.Line);
        Assert.Equal(vs.Column, back.Column);
    }
}
