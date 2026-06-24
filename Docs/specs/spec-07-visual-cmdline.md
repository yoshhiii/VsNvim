# Spec 07 — Visual Selection & Command Line (`VsNvim.Core` cmdline decode + VSIX)

> Inherits all **Global Constraints** from [`../implementation-plan.md`](../implementation-plan.md).
> Depends on Specs 02, 04, 05, 06. Cmdline event decoding is **Core/TDD**; visual rendering and the cmdline overlay are integration/manual. This is the last spec to reach MVP DoD #4.

**Goal:** Render nvim visual selections as VS selections, and render the nvim command line (`:`, `/`) in a small overlay so Ex commands work end-to-end.

**Files:**
- Modify: `src/VsNvim.Core/Redraw/RedrawDispatcher.cs`, `src/VsNvim.Core/Redraw/RedrawEvent.cs`
- Create: `src/VsNvim.Vsix/Editor/VisualSelectionPainter.cs`
- Create: `src/VsNvim.Vsix/Editor/CommandLineOverlay.cs`
- Test: `tests/VsNvim.Core.Tests/Redraw/CmdlineDecodeTests.cs`

---

## Task 1: Decode cmdline redraw events (Core, TDD)

**Files:** Modify `RedrawEvent.cs`, `RedrawDispatcher.cs`; Test `tests/VsNvim.Core.Tests/Redraw/CmdlineDecodeTests.cs`.

**Interfaces — Produces (additions):** `CmdlineShowEvent { string FirstChar; string Content; }`, and on `RedrawDispatcher`: `event Action<CmdlineShowEvent> CmdlineShow`, `event Action CmdlineHide`.

- [ ] **Step 1: Write the failing test**

Create `tests/VsNvim.Core.Tests/Redraw/CmdlineDecodeTests.cs`:
```csharp
using VsNvim.Core.Redraw;
using Xunit;

namespace VsNvim.Core.Tests.Redraw;

public class CmdlineDecodeTests
{
    [Fact]
    public void Process_CmdlineShow_ConcatenatesChunksAndCapturesFirstChar()
    {
        var dispatcher = new RedrawDispatcher();
        CmdlineShowEvent shown = null;
        dispatcher.CmdlineShow += e => shown = e;

        // cmdline_show args: [content, pos, firstc, prompt, indent, level]
        // content is an array of [attrId, text] chunks.
        object[] redrawArgs =
        {
            new object[]
            {
                "cmdline_show",
                new object[]
                {
                    new object[] { new object[] { 0L, "%s/foo" }, new object[] { 0L, "/bar/g" } },
                    6L, ":", "", 0L, 0L,
                },
            },
        };

        dispatcher.Process(redrawArgs);

        Assert.NotNull(shown);
        Assert.Equal(":", shown.FirstChar);
        Assert.Equal("%s/foo/bar/g", shown.Content);
    }

    [Fact]
    public void Process_CmdlineHide_RaisesEvent()
    {
        var dispatcher = new RedrawDispatcher();
        bool hidden = false;
        dispatcher.CmdlineHide += () => hidden = true;

        dispatcher.Process(new object[] { new object[] { "cmdline_hide", new object[] { 0L } } });

        Assert.True(hidden);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~CmdlineDecodeTests"`
Expected: FAIL — `CmdlineShowEvent`/events do not exist.

- [ ] **Step 3: Add the event type**

Append to `src/VsNvim.Core/Redraw/RedrawEvent.cs`:
```csharp
namespace VsNvim.Core.Redraw
{
    public sealed class CmdlineShowEvent
    {
        public CmdlineShowEvent(string firstChar, string content)
        {
            FirstChar = firstChar;
            Content = content;
        }
        public string FirstChar { get; }
        public string Content { get; }
    }
}
```
> Keep this in the same file; C# allows multiple `namespace` blocks, or merge it into the existing block.

- [ ] **Step 4: Extend the dispatcher**

In `src/VsNvim.Core/Redraw/RedrawDispatcher.cs`, add the events and `case` arms:
```csharp
        public event Action<CmdlineShowEvent> CmdlineShow;
        public event Action CmdlineHide;
```
Add inside the `switch (name)` in `Process`:
```csharp
                    case "cmdline_show":
                        for (int i = 1; i < batch.Length; i++)
                        {
                            var t = (object[])batch[i];
                            var chunks = (object[])t[0];
                            var sb = new System.Text.StringBuilder();
                            foreach (object chunkObj in chunks)
                            {
                                var chunk = (object[])chunkObj;
                                sb.Append((string)chunk[1]);
                            }
                            CmdlineShow?.Invoke(new CmdlineShowEvent((string)t[2], sb.ToString()));
                        }
                        break;
                    case "cmdline_hide":
                        CmdlineHide?.Invoke();
                        break;
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~CmdlineDecodeTests"`
Expected: PASS (2 passed).

- [ ] **Step 6: Run the full Core suite**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj`
Expected: PASS (all Core specs green).

- [ ] **Step 7: Commit**

```bash
git add src/VsNvim.Core/Redraw/ tests/VsNvim.Core.Tests/Redraw/CmdlineDecodeTests.cs
git commit -m "feat: decode cmdline_show/cmdline_hide redraw events"
```

---

## Task 2: Render visual selections (integration)

**Files:** Create `src/VsNvim.Vsix/Editor/VisualSelectionPainter.cs`; subscribe in `TextViewController`.

- [ ] **Step 1: Implement selection painting on mode change/flush**

When `CurrentMode` is `Visual`/`VisualLine`/`VisualBlock`, query nvim for the selection anchor and cursor and set the VS selection. Create `src/VsNvim.Vsix/Editor/VisualSelectionPainter.cs`:
```csharp
using System;
using System.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using VsNvim.Core.Redraw;
using VsNvim.Core.Rpc;
using VsNvim.Core.Text;

namespace VsNvim.Vsix.Editor
{
    internal sealed class VisualSelectionPainter
    {
        private readonly IWpfTextView _view;
        private readonly NeovimRpcClient _client;
        private readonly Func<VimMode> _mode;
        private readonly CancellationToken _ct;

        public VisualSelectionPainter(IWpfTextView view, NeovimRpcClient client, Func<VimMode> mode, CancellationToken ct)
        {
            _view = view; _client = client; _mode = mode; _ct = ct;
        }

        /// <summary>Call after each redraw flush (on the UI thread is not required for the query).</summary>
        public async void Refresh()
        {
            VimMode mode = _mode();
            if (mode != VimMode.Visual && mode != VimMode.VisualLine && mode != VimMode.VisualBlock)
            {
                Run(() => _view.Selection.Clear());
                return;
            }

            // getpos returns [bufnum, lnum(1-based), col(1-based byte), off]
            var anchor = (object[])await _client.RequestAsync("nvim_call_function",
                new object[] { "getpos", new object[] { "v" } }, _ct);
            var cursor = (object[])await _client.RequestAsync("nvim_call_function",
                new object[] { "getpos", new object[] { "." } }, _ct);

            Run(() =>
            {
                var snapshot = _view.TextSnapshot;
                SnapshotPoint a = ToPoint(snapshot, anchor);
                SnapshotPoint c = ToPoint(snapshot, cursor);
                SnapshotPoint start = a <= c ? a : c;
                SnapshotPoint end = a <= c ? c : a;
                if (mode == VimMode.VisualLine)
                {
                    start = snapshot.GetLineFromPosition(start).Start;
                    end = snapshot.GetLineFromPosition(end).End;
                }
                // VisualBlock is approximated as a linear selection in the MVP.
                end = new SnapshotPoint(snapshot, Math.Min(end.Position + 1, snapshot.Length)); // inclusive cursor cell
                _view.Selection.Select(new SnapshotSpan(start, end), isReversed: a > c);
                _view.Caret.MoveTo(c);
            });
        }

        private static SnapshotPoint ToPoint(ITextSnapshot snapshot, object[] getpos)
        {
            int lnum = Convert.ToInt32(getpos[1]);
            int byteCol = Convert.ToInt32(getpos[2]) - 1; // getpos col is 1-based
            int lineIndex = Math.Min(lnum - 1, snapshot.LineCount - 1);
            var line = snapshot.GetLineFromLineNumber(lineIndex);
            int charCol = CoordinateMapper.ByteToCharColumn(line.GetText(), byteCol);
            return new SnapshotPoint(snapshot, line.Start.Position + Math.Min(charCol, line.Length));
        }

        private void Run(Action a)
        {
            if (_view.VisualElement.Dispatcher.CheckAccess()) a();
            else _view.VisualElement.Dispatcher.BeginInvoke(a);
        }
    }
}
```
In `TextViewController`, construct a `VisualSelectionPainter` and call `Refresh()` from `OnFlush` (after mode/cursor resync).

- [ ] **Step 2: Manual verification**

1. `v` then motions extend a character-wise selection that matches nvim.
2. `V` selects whole lines.
3. Operators on a visual selection (e.g. `vjjd`, `Vd`) delete the selected region in VS (via Spec 05).
4. `<C-v>` block selection at least selects *something* sensible (linear approximation acceptable; note the limitation).

- [ ] **Step 3: Commit**

```bash
git add src/VsNvim.Vsix/Editor/VisualSelectionPainter.cs src/VsNvim.Vsix/Editor/TextViewController.cs
git commit -m "feat: render nvim visual selections as VS selections"
```

---

## Task 3: Command-line overlay (integration)

**Files:** Create `src/VsNvim.Vsix/Editor/CommandLineOverlay.cs`; enable `ext_cmdline` and subscribe in `TextViewController`.

- [ ] **Step 1: Enable ext_cmdline**

In `TextViewController.StartAsync`, change the `nvim_ui_attach` options to:
```csharp
            var uiOpts = new System.Collections.Generic.Dictionary<string, object>
            {
                ["ext_linegrid"] = true,
                ["ext_cmdline"] = true,
            };
```

- [ ] **Step 2: Implement the overlay**

Create `src/VsNvim.Vsix/Editor/CommandLineOverlay.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;
using VsNvim.Core.Redraw;

namespace VsNvim.Vsix.Editor
{
    /// <summary>A bottom-anchored text box that mirrors nvim's command line.</summary>
    internal sealed class CommandLineOverlay
    {
        private readonly IWpfTextView _view;
        private readonly TextBox _box;
        private bool _added;

        public CommandLineOverlay(IWpfTextView view)
        {
            _view = view;
            _box = new TextBox
            {
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed,
            };
        }

        public void Show(CmdlineShowEvent e)
        {
            EnsureAdded();
            _box.Text = e.FirstChar + e.Content;
            _box.Visibility = Visibility.Visible;
        }

        public void Hide() => _box.Visibility = Visibility.Collapsed;

        private void EnsureAdded()
        {
            if (_added) return;
            // Host the overlay in the editor's WPF visual tree. The exact host depends on the
            // template; a Grid/Canvas overlay added to _view.VisualElement's parent works in practice.
            if (_view.VisualElement.Parent is Panel panel)
            {
                panel.Children.Add(_box);
                _added = true;
            }
        }
    }
}
```
In `TextViewController`, wire: `_redraw.CmdlineShow += e => RunOnUi(() => _overlay.Show(e)); _redraw.CmdlineHide += () => RunOnUi(_overlay.Hide);`

> Hosting the overlay precisely in the editor chrome may need adjustment against the VS 2026 view template — confirm during manual verification and fall back to a `IWpfTextViewMargin` if the parent panel is not a `Panel`.

- [ ] **Step 3: Manual verification (MVP DoD #4)**

1. Press `:` — the overlay appears showing `:`.
2. Type `w` and Enter — file saves (confirm via title/asterisk or disk).
3. `:%s/foo/bar/g` + Enter — replaces text in VS (via Spec 05), overlay hides on completion.
4. `/pattern` + Enter — search moves the caret to the match.
5. `<Esc>` while the command line is open dismisses it (overlay hides, back to normal mode).

- [ ] **Step 4: Commit**

```bash
git add src/VsNvim.Vsix/Editor/CommandLineOverlay.cs src/VsNvim.Vsix/Editor/TextViewController.cs
git commit -m "feat: render nvim command line in an editor overlay"
```

---

## Done when
- Visual selections mirror nvim; `:` and `/` commands work end-to-end with a visible command line. **All five MVP Definition-of-Done items are met.**
