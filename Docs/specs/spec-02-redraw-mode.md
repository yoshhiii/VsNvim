# Spec 02 — Redraw Decoding & Mode Mapping (`VsNvim.Core.Redraw`)

> Inherits all **Global Constraints** from [`../implementation-plan.md`](../implementation-plan.md).
> Depends on Spec 01 (`object[]`/`Dictionary` value shapes). Independent of Spec 03.

**Goal:** Turn nvim `redraw` notification payloads into typed events the renderer can consume, and map nvim mode codes to a `VimMode` the input router can gate on.

**Files:**
- Create: `src/VsNvim.Core/Redraw/VimMode.cs`
- Create: `src/VsNvim.Core/Redraw/RedrawEvent.cs`
- Create: `src/VsNvim.Core/Redraw/RedrawDispatcher.cs`
- Test: `tests/VsNvim.Core.Tests/Redraw/VimModeMapTests.cs`
- Test: `tests/VsNvim.Core.Tests/Redraw/RedrawDispatcherTests.cs`

**Interfaces — Produces (consumed by Specs 04/06/07):**
- `enum VimMode { Unknown, Normal, OperatorPending, Insert, Replace, Visual, VisualLine, VisualBlock, CommandLine, Terminal }`
- `VimMode VimModeMap.FromModeCode(string modeCode)` — `modeCode` is the authoritative string from `nvim_get_mode().mode`.
- `RedrawDispatcher` with `void Process(object[] redrawArgs)` and events `ModeChanged(ModeChangeEvent)`, `CursorGoto(CursorGotoEvent)`, `FlushReceived()`.

---

## Task 1: Mode mapping

**Files:** Create `src/VsNvim.Core/Redraw/VimMode.cs`; Test `tests/VsNvim.Core.Tests/Redraw/VimModeMapTests.cs`.

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~VimModeMapTests"`
Expected: FAIL — `VimMode`/`VimModeMap` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/VsNvim.Core/Redraw/VimMode.cs`:
```csharp
namespace VsNvim.Core.Redraw
{
    public enum VimMode
    {
        Unknown,
        Normal,
        OperatorPending,
        Insert,
        Replace,
        Visual,
        VisualLine,
        VisualBlock,
        CommandLine,
        Terminal
    }

    /// <summary>Maps the authoritative mode code from nvim_get_mode().mode to a VimMode.</summary>
    public static class VimModeMap
    {
        public static VimMode FromModeCode(string modeCode)
        {
            if (string.IsNullOrEmpty(modeCode))
                return VimMode.Unknown;

            // Operator-pending modes are "no", "nov", "noV", "no<C-v>".
            if (modeCode.StartsWith("no"))
                return VimMode.OperatorPending;

            switch (modeCode[0])
            {
                case 'n': return VimMode.Normal;
                case 'i': return VimMode.Insert;
                case 'R': return VimMode.Replace;
                case 'v': return VimMode.Visual;
                case 'V': return VimMode.VisualLine;
                case (char)0x16: return VimMode.VisualBlock; // CTRL-V
                case 'c': return VimMode.CommandLine;
                case 't': return VimMode.Terminal;
                default: return VimMode.Unknown;
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~VimModeMapTests"`
Expected: PASS (all theory cases + the CTRL-V fact).

- [ ] **Step 5: Commit**

```bash
git add src/VsNvim.Core/Redraw/VimMode.cs tests/VsNvim.Core.Tests/Redraw/VimModeMapTests.cs
git commit -m "feat: map nvim mode codes to VimMode"
```

---

## Task 2: Redraw dispatcher

**Files:** Create `src/VsNvim.Core/Redraw/RedrawEvent.cs`, `src/VsNvim.Core/Redraw/RedrawDispatcher.cs`; Test `tests/VsNvim.Core.Tests/Redraw/RedrawDispatcherTests.cs`.

**Interfaces — Consumes:** the `redraw` notification's `Arguments[0]` (an `object[]` of batches, each `object[]` = `[name, tuple, tuple, ...]`).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using VsNvim.Core.Redraw;
using Xunit;

namespace VsNvim.Core.Tests.Redraw;

public class RedrawDispatcherTests
{
    [Fact]
    public void Process_RaisesModeCursorAndFlushInOrder()
    {
        var dispatcher = new RedrawDispatcher();
        var log = new List<string>();
        dispatcher.ModeChanged += e => log.Add($"mode:{e.ModeName}:{e.ModeIndex}");
        dispatcher.CursorGoto += e => log.Add($"cur:{e.Grid}:{e.Row}:{e.Column}");
        dispatcher.FlushReceived += () => log.Add("flush");

        // Mirrors a real redraw payload: array of batches.
        object[] redrawArgs =
        {
            new object[] { "grid_cursor_goto", new object[] { 1L, 3L, 5L } },
            new object[] { "mode_change", new object[] { "insert", 2L } },
            new object[] { "win_viewport", new object[] { 1L, 0L } }, // ignored
            new object[] { "flush" },
        };

        dispatcher.Process(redrawArgs);

        Assert.Equal(new[] { "cur:1:3:5", "mode:insert:2", "flush" }, log);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~RedrawDispatcherTests"`
Expected: FAIL — `RedrawDispatcher` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/VsNvim.Core/Redraw/RedrawEvent.cs`:
```csharp
namespace VsNvim.Core.Redraw
{
    public sealed class ModeChangeEvent
    {
        public ModeChangeEvent(string modeName, long modeIndex)
        {
            ModeName = modeName;
            ModeIndex = modeIndex;
        }
        public string ModeName { get; }
        public long ModeIndex { get; }
    }

    public sealed class CursorGotoEvent
    {
        public CursorGotoEvent(long grid, long row, long column)
        {
            Grid = grid;
            Row = row;
            Column = column;
        }
        public long Grid { get; }
        public long Row { get; }
        public long Column { get; }
    }
}
```

Create `src/VsNvim.Core/Redraw/RedrawDispatcher.cs`:
```csharp
using System;

namespace VsNvim.Core.Redraw
{
    /// <summary>Decodes a "redraw" notification payload into typed events. Unknown events are ignored.</summary>
    public sealed class RedrawDispatcher
    {
        public event Action<ModeChangeEvent> ModeChanged;
        public event Action<CursorGotoEvent> CursorGoto;
        public event Action FlushReceived;

        public void Process(object[] redrawArgs)
        {
            if (redrawArgs == null)
                return;

            foreach (object batchObj in redrawArgs)
            {
                var batch = (object[])batchObj;
                var name = (string)batch[0];
                switch (name)
                {
                    case "flush":
                        FlushReceived?.Invoke();
                        break;
                    case "mode_change":
                        for (int i = 1; i < batch.Length; i++)
                        {
                            var t = (object[])batch[i];
                            ModeChanged?.Invoke(new ModeChangeEvent((string)t[0], Convert.ToInt64(t[1])));
                        }
                        break;
                    case "grid_cursor_goto":
                        for (int i = 1; i < batch.Length; i++)
                        {
                            var t = (object[])batch[i];
                            CursorGoto?.Invoke(new CursorGotoEvent(
                                Convert.ToInt64(t[0]), Convert.ToInt64(t[1]), Convert.ToInt64(t[2])));
                        }
                        break;
                    // All other redraw events are not needed for the MVP.
                }
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~RedrawDispatcherTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/VsNvim.Core/Redraw/ tests/VsNvim.Core.Tests/Redraw/RedrawDispatcherTests.cs
git commit -m "feat: decode nvim redraw batches into typed mode/cursor/flush events"
```

---

## Notes for downstream specs
- `grid_cursor_goto` gives a **screen** cell, not a buffer position. Spec 04 uses it only as a "cursor moved, go re-query" signal and obtains the authoritative buffer cursor via `nvim_win_get_cursor` (then Spec 03 maps it). Mode for gating (Spec 06) comes from `nvim_get_mode().mode` → `VimModeMap.FromModeCode`, **not** from the redraw `mode_change` name (which is a `mode_info` label, not the authoritative code).
