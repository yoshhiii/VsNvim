# Spec 06 — Insert-Mode Handoff (`VsNvim.Core` key notation + VSIX gating)

> Inherits all **Global Constraints** from [`../implementation-plan.md`](../implementation-plan.md).
> Depends on Specs 04, 05. Key-notation mapping is **Core/TDD**; the handoff gating is integration/manual.

**Goal:** When nvim enters insert mode, hand typing to the VS editor (so IntelliSense/snippets work); on `<Esc>`, return control to nvim. Inserted text reaches nvim **through the edit-sync push from Spec 05**, not by forwarding keystrokes — so no separate insert-mode key forwarding is needed for ordinary characters.

**The handoff in one paragraph:** In normal mode the command filter swallows keys and forwards them to nvim (Spec 04). Pressing `i`/`a`/`o` is forwarded; nvim switches to insert; `OnFlush` sees `VimMode.Insert`; the block cursor is removed (Spec 04). Now `CurrentMode == Insert`, so the filter **stops swallowing** `TYPECHAR` and lets VS insert normally — each insertion fires `TextBuffer.Changed`, which Spec 05 pushes into nvim. The filter still intercepts **special keys** (notably `<Esc>`) in all modes and forwards them to nvim, which leaves insert mode.

**Known limitation (documented, not solved):** because nvim never sees the individual insert keystrokes, dot-repeat (`.`) and insert-count constructs (`3iabc<Esc>`) replay only nvim's net-change model, not the exact keystroke sequence. Accepted for the MVP.

**Files:**
- Create: `src/VsNvim.Core/Input/SpecialKey.cs`
- Create: `src/VsNvim.Core/Input/NvimKeyNotation.cs`
- Modify: `src/VsNvim.Vsix/Editor/VsNvimCommandFilter.cs`
- Test: `tests/VsNvim.Core.Tests/Input/NvimKeyNotationTests.cs`

---

## Task 1: nvim key notation (Core, TDD)

**Files:** Create `src/VsNvim.Core/Input/SpecialKey.cs`, `src/VsNvim.Core/Input/NvimKeyNotation.cs`; Test `tests/VsNvim.Core.Tests/Input/NvimKeyNotationTests.cs`.

**Interfaces — Produces:** `enum SpecialKey`, `[Flags] enum KeyModifiers`, `string NvimKeyNotation.ForSpecialKey(SpecialKey, KeyModifiers)`, `string NvimKeyNotation.ForChar(char, KeyModifiers)`.

- [ ] **Step 1: Write the failing test**

Create `tests/VsNvim.Core.Tests/Input/NvimKeyNotationTests.cs`:
```csharp
using VsNvim.Core.Input;
using Xunit;

namespace VsNvim.Core.Tests.Input;

public class NvimKeyNotationTests
{
    [Theory]
    [InlineData(SpecialKey.Escape, "<Esc>")]
    [InlineData(SpecialKey.Enter, "<CR>")]
    [InlineData(SpecialKey.Backspace, "<BS>")]
    [InlineData(SpecialKey.Tab, "<Tab>")]
    [InlineData(SpecialKey.Delete, "<Del>")]
    [InlineData(SpecialKey.Up, "<Up>")]
    [InlineData(SpecialKey.Left, "<Left>")]
    public void ForSpecialKey_NoModifiers(SpecialKey key, string expected)
        => Assert.Equal(expected, NvimKeyNotation.ForSpecialKey(key, KeyModifiers.None));

    [Fact]
    public void ForSpecialKey_WithControl()
        => Assert.Equal("<C-Up>", NvimKeyNotation.ForSpecialKey(SpecialKey.Up, KeyModifiers.Control));

    [Theory]
    [InlineData('a', KeyModifiers.None, "a")]
    [InlineData('w', KeyModifiers.Control, "<C-w>")]
    [InlineData('<', KeyModifiers.None, "<lt>")]
    [InlineData('\\', KeyModifiers.None, "<Bslash>")]
    public void ForChar_Cases(char c, KeyModifiers mods, string expected)
        => Assert.Equal(expected, NvimKeyNotation.ForChar(c, mods));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~NvimKeyNotationTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/VsNvim.Core/Input/SpecialKey.cs`:
```csharp
using System;

namespace VsNvim.Core.Input
{
    public enum SpecialKey
    {
        Escape, Enter, Backspace, Tab, Delete,
        Up, Down, Left, Right, Home, End, PageUp, PageDown
    }

    [Flags]
    public enum KeyModifiers
    {
        None = 0,
        Control = 1,
        Shift = 2,
        Alt = 4
    }
}
```

Create `src/VsNvim.Core/Input/NvimKeyNotation.cs`:
```csharp
using System.Collections.Generic;

namespace VsNvim.Core.Input
{
    /// <summary>Builds nvim_input key notation strings (e.g. "&lt;Esc&gt;", "&lt;C-w&gt;").</summary>
    public static class NvimKeyNotation
    {
        private static readonly Dictionary<SpecialKey, string> Names = new Dictionary<SpecialKey, string>
        {
            [SpecialKey.Escape] = "Esc",
            [SpecialKey.Enter] = "CR",
            [SpecialKey.Backspace] = "BS",
            [SpecialKey.Tab] = "Tab",
            [SpecialKey.Delete] = "Del",
            [SpecialKey.Up] = "Up",
            [SpecialKey.Down] = "Down",
            [SpecialKey.Left] = "Left",
            [SpecialKey.Right] = "Right",
            [SpecialKey.Home] = "Home",
            [SpecialKey.End] = "End",
            [SpecialKey.PageUp] = "PageUp",
            [SpecialKey.PageDown] = "PageDown",
        };

        public static string ForSpecialKey(SpecialKey key, KeyModifiers mods)
            => "<" + Prefix(mods) + Names[key] + ">";

        public static string ForChar(char c, KeyModifiers mods)
        {
            if (mods == KeyModifiers.None)
            {
                if (c == '<') return "<lt>";
                if (c == '\\') return "<Bslash>";
                return c.ToString();
            }
            return "<" + Prefix(mods) + c + ">";
        }

        private static string Prefix(KeyModifiers mods)
        {
            string p = "";
            if ((mods & KeyModifiers.Control) != 0) p += "C-";
            if ((mods & KeyModifiers.Alt) != 0) p += "M-";
            if ((mods & KeyModifiers.Shift) != 0) p += "S-";
            return p;
        }
    }
}
```
> The `&lt;`/`&gt;` in the doc-comment are just escaped angle brackets for XML; type literal `<` `>` in code.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~NvimKeyNotationTests"`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add src/VsNvim.Core/Input/ tests/VsNvim.Core.Tests/Input/NvimKeyNotationTests.cs
git commit -m "feat: nvim key notation builder for special keys and control chars"
```

---

## Task 2: Mode-gated routing + special keys (integration)

**Files:** Modify `src/VsNvim.Vsix/Editor/VsNvimCommandFilter.cs`.

- [ ] **Step 1: Map VS command IDs to SpecialKey and gate by mode**

Extend `Exec` in `VsNvimCommandFilter` so that:
- A `VSStd2KCmdID` for a special key always maps via a switch to a `SpecialKey`, is converted with `NvimKeyNotation.ForSpecialKey`, forwarded with `nvim_input`, and swallowed **in all modes** for `<Esc>** (so insert can be exited); for the other special keys, swallow only when `CurrentMode != Insert`.
- `TYPECHAR` is forwarded+swallowed only when `CurrentMode != Insert` (unchanged from Spec 04); in insert mode it falls through to `Next.Exec` so VS inserts the character.

Replace the body of `Exec` with:
```csharp
        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            var controller = Controller;
            if (controller != null && pguidCmdGroup == VSConstants.VSStd2K)
            {
                var cmd = (VSConstants.VSStd2KCmdID)nCmdID;
                bool insert = controller.CurrentMode == VimMode.Insert;

                // Special keys.
                if (TryMapSpecialKey(cmd, out VsNvim.Core.Input.SpecialKey special))
                {
                    bool isEscape = special == VsNvim.Core.Input.SpecialKey.Escape;
                    if (!insert || isEscape)
                    {
                        string keys = VsNvim.Core.Input.NvimKeyNotation.ForSpecialKey(
                            special, VsNvim.Core.Input.KeyModifiers.None);
                        _ = controller.Client.NotifyAsync("nvim_input", new object[] { keys }, default);
                        return VSConstants.S_OK;
                    }
                }

                // Plain typed character.
                if (cmd == VSConstants.VSStd2KCmdID.TYPECHAR && !insert)
                {
                    var typed = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
                    string keys = VsNvim.Core.Input.NvimKeyNotation.ForChar(typed, VsNvim.Core.Input.KeyModifiers.None);
                    _ = controller.Client.NotifyAsync("nvim_input", new object[] { keys }, default);
                    return VSConstants.S_OK;
                }
            }
            return Next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        private static bool TryMapSpecialKey(VSConstants.VSStd2KCmdID cmd, out VsNvim.Core.Input.SpecialKey key)
        {
            switch (cmd)
            {
                case VSConstants.VSStd2KCmdID.CANCEL: key = VsNvim.Core.Input.SpecialKey.Escape; return true;
                case VSConstants.VSStd2KCmdID.RETURN: key = VsNvim.Core.Input.SpecialKey.Enter; return true;
                case VSConstants.VSStd2KCmdID.BACKSPACE: key = VsNvim.Core.Input.SpecialKey.Backspace; return true;
                case VSConstants.VSStd2KCmdID.TAB: key = VsNvim.Core.Input.SpecialKey.Tab; return true;
                case VSConstants.VSStd2KCmdID.DELETE: key = VsNvim.Core.Input.SpecialKey.Delete; return true;
                case VSConstants.VSStd2KCmdID.UP: key = VsNvim.Core.Input.SpecialKey.Up; return true;
                case VSConstants.VSStd2KCmdID.DOWN: key = VsNvim.Core.Input.SpecialKey.Down; return true;
                case VSConstants.VSStd2KCmdID.LEFT: key = VsNvim.Core.Input.SpecialKey.Left; return true;
                case VSConstants.VSStd2KCmdID.RIGHT: key = VsNvim.Core.Input.SpecialKey.Right; return true;
                case VSConstants.VSStd2KCmdID.HOME: key = VsNvim.Core.Input.SpecialKey.Home; return true;
                case VSConstants.VSStd2KCmdID.END: key = VsNvim.Core.Input.SpecialKey.End; return true;
                default: key = default; return false;
            }
        }
```
> Confirm the exact `VSStd2KCmdID` member names against the VS 2026 SDK (Task 0 SDK Notes) — historically `CANCEL` is Escape and `RETURN` is Enter, but verify.

- [ ] **Step 2: Manual verification (MVP DoD #3)**

In the Exp instance:
1. From normal mode press `i` — block cursor disappears, VS line caret appears, status reflects insert.
2. Type several characters and trigger IntelliSense (`Ctrl+Space`) — completion works; text appears in VS.
3. Press `<Esc>` — returns to normal mode, block cursor returns, caret steps back one column (Vim behavior).
4. Confirm the inserted text is present in nvim (e.g. run `:w` to a temp file and inspect, or `nvim_buf_get_lines` via the immediate window) — matches VS exactly.
5. `o` opens a new line below in insert mode; `<Esc>` returns to normal. `A` appends at line end. All behave like Vim.
6. Known-limitation check: `.` after an insert replays the net text (acceptable); note any surprising behavior in the spec for future work.

- [ ] **Step 3: Commit**

```bash
git add src/VsNvim.Vsix/Editor/VsNvimCommandFilter.cs
git commit -m "feat: mode-gated key routing with insert handoff and special keys"
```

---

## Done when
- Insert mode uses native VS editing (IntelliSense available); `<Esc>` reliably returns to normal mode; text stays identical across VS and nvim. Dot-repeat limitation documented.
