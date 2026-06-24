# Spec 03 — Coordinate Mapping (`VsNvim.Core.Text`)

> Inherits all **Global Constraints** from [`../implementation-plan.md`](../implementation-plan.md).
> Depends on nothing in this repo. Independent of Specs 01/02 — can be built in parallel.

**Goal:** Convert between nvim cursor coordinates (1-based row, 0-based **byte** column) and VS editor coordinates (0-based line, 0-based **UTF-16 char** column), correctly handling multibyte UTF-8 and surrogate pairs. Wrong conversions desync the cursor on every non-ASCII character.

**Why bytes vs chars:** nvim columns are UTF-8 byte offsets. VS works in UTF-16 char offsets. `é` (U+00E9) is 2 UTF-8 bytes / 1 UTF-16 char; `😀` (U+1F600) is 4 UTF-8 bytes / 2 UTF-16 chars. Tabs are 1 byte / 1 char (Vim cursor columns are byte-based, **not** display columns), so tabs need no special handling here.

**Files:**
- Create: `src/VsNvim.Core/Text/Position.cs`
- Create: `src/VsNvim.Core/Text/CoordinateMapper.cs`
- Test: `tests/VsNvim.Core.Tests/Text/CoordinateMapperTests.cs`

**Interfaces — Produces (consumed by Specs 04/05/06/07):**
- `readonly struct VsPosition { int Line; int Column; }` (0-based, UTF-16)
- `readonly struct NvimPosition { long Row; long Column; }` (1-based row, 0-based byte col)
- `static int CoordinateMapper.ByteToCharColumn(string lineText, int byteColumn)`
- `static int CoordinateMapper.CharToByteColumn(string lineText, int charColumn)`
- `static VsPosition CoordinateMapper.ToVs(NvimPosition pos, string lineText)`
- `static NvimPosition CoordinateMapper.ToNvim(VsPosition pos, string lineText)`

---

## Task 1: Position value types

**Files:** Create `src/VsNvim.Core/Text/Position.cs`. (No standalone test — exercised by Task 2.)

- [ ] **Step 1: Create the implementation**

Create `src/VsNvim.Core/Text/Position.cs`:
```csharp
namespace VsNvim.Core.Text
{
    /// <summary>VS editor position: 0-based line, 0-based UTF-16 char column.</summary>
    public readonly struct VsPosition
    {
        public VsPosition(int line, int column)
        {
            Line = line;
            Column = column;
        }
        public int Line { get; }
        public int Column { get; }
    }

    /// <summary>Neovim position: 1-based row, 0-based UTF-8 byte column.</summary>
    public readonly struct NvimPosition
    {
        public NvimPosition(long row, long column)
        {
            Row = row;
            Column = column;
        }
        public long Row { get; }
        public long Column { get; }
    }
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build src/VsNvim.Core/VsNvim.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/VsNvim.Core/Text/Position.cs
git commit -m "feat: add VS/nvim position value types"
```

---

## Task 2: Column conversion (byte <-> char)

**Files:** Create `src/VsNvim.Core/Text/CoordinateMapper.cs`; Test `tests/VsNvim.Core.Tests/Text/CoordinateMapperTests.cs`.

- [ ] **Step 1: Write the failing test**

Create `tests/VsNvim.Core.Tests/Text/CoordinateMapperTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~CoordinateMapperTests"`
Expected: FAIL — `CoordinateMapper` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/VsNvim.Core/Text/CoordinateMapper.cs`:
```csharp
using System;

namespace VsNvim.Core.Text
{
    /// <summary>Converts between nvim byte columns / 1-based rows and VS UTF-16 columns / 0-based lines.</summary>
    public static class CoordinateMapper
    {
        /// <summary>UTF-8 byte length of a Unicode code point.</summary>
        private static int Utf8Length(int codePoint)
        {
            if (codePoint <= 0x7F) return 1;
            if (codePoint <= 0x7FF) return 2;
            if (codePoint <= 0xFFFF) return 3;
            return 4;
        }

        /// <summary>0-based UTF-16 char column -> 0-based UTF-8 byte column. Clamps to end of line.</summary>
        public static int CharToByteColumn(string lineText, int charColumn)
        {
            if (lineText == null) throw new ArgumentNullException(nameof(lineText));
            int chars = Math.Min(charColumn, lineText.Length);
            int bytes = 0;
            int i = 0;
            while (i < chars)
            {
                char c = lineText[i];
                if (char.IsHighSurrogate(c) && i + 1 < lineText.Length && char.IsLowSurrogate(lineText[i + 1]))
                {
                    bytes += Utf8Length(char.ConvertToUtf32(c, lineText[i + 1]));
                    i += 2;
                }
                else
                {
                    bytes += Utf8Length(c);
                    i += 1;
                }
            }
            return bytes;
        }

        /// <summary>0-based UTF-8 byte column -> 0-based UTF-16 char column. Clamps to end of line.</summary>
        public static int ByteToCharColumn(string lineText, int byteColumn)
        {
            if (lineText == null) throw new ArgumentNullException(nameof(lineText));
            int bytes = 0;
            int i = 0;
            while (i < lineText.Length && bytes < byteColumn)
            {
                char c = lineText[i];
                if (char.IsHighSurrogate(c) && i + 1 < lineText.Length && char.IsLowSurrogate(lineText[i + 1]))
                {
                    bytes += Utf8Length(char.ConvertToUtf32(c, lineText[i + 1]));
                    i += 2;
                }
                else
                {
                    bytes += Utf8Length(c);
                    i += 1;
                }
            }
            return i;
        }

        public static NvimPosition ToNvim(VsPosition pos, string lineText) =>
            new NvimPosition(pos.Line + 1, CharToByteColumn(lineText, pos.Column));

        public static VsPosition ToVs(NvimPosition pos, string lineText) =>
            new VsPosition((int)(pos.Row - 1), ByteToCharColumn(lineText, (int)pos.Column));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~CoordinateMapperTests"`
Expected: PASS (all cases).

- [ ] **Step 5: Run the full Core suite**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj`
Expected: PASS (Specs 01–03 all green).

- [ ] **Step 6: Commit**

```bash
git add src/VsNvim.Core/Text/CoordinateMapper.cs tests/VsNvim.Core.Tests/Text/CoordinateMapperTests.cs
git commit -m "feat: add byte/char column coordinate mapping with multibyte coverage"
```

---

## Done when
- Conversions round-trip across ASCII, 2-byte (`é`), and 4-byte surrogate-pair (`😀`) content.
- `ToVs`/`ToNvim` correctly offset the row by 1 and map the column through the byte/char conversion.
