# Spec 05 — Edit Sync (`VsNvim.Core.Sync` + VSIX apply)

> Inherits all **Global Constraints** from [`../implementation-plan.md`](../implementation-plan.md).
> Depends on Specs 01–04. The **echo-guard state machine is pure Core and TDD'd**; the document-apply binding is integration with manual verification. This is Risk #2 — the make-or-break engineering problem.

**Goal:** Keep the VS document and the nvim buffer consistent when edits originate on either side, with **no echo loops** (an edit applied to one side must not bounce back as a new edit to the other).

**The two loops to break:**
1. nvim edit → apply to VS → VS fires `Changed` → would push back to nvim. (Broken by the **applying-remote guard**.)
2. VS edit → push to nvim → nvim fires `on_lines` echo → would re-apply to VS. (Broken by the **pending-self-edit counter**.)

**Files:**
- Create: `src/VsNvim.Core/Text/LineDelta.cs`
- Create: `src/VsNvim.Core/Sync/IDocumentSink.cs`, `src/VsNvim.Core/Sync/INvimBufferWriter.cs`
- Create: `src/VsNvim.Core/Sync/EditSyncEngine.cs`
- Create: `src/VsNvim.Vsix/Editor/TextViewDocumentSink.cs` (integration)
- Test: `tests/VsNvim.Core.Tests/Sync/EditSyncEngineTests.cs`

**Interfaces — Produces:**
- `LineDelta { int FirstLine; int OldLastLine; string[] NewLines; }`
- `IDocumentSink { void ReplaceLines(LineDelta delta); }`
- `INvimBufferWriter { void PushReplaceLines(LineDelta delta); }`
- `EditSyncEngine(IDocumentSink, INvimBufferWriter)` with `void ApplyFromNvim(LineDelta)`, `void OnVsTextChanged(LineDelta)`, `bool IsApplyingRemoteEdit { get; }`

---

## Task 1: Line delta + sync interfaces

**Files:** Create `src/VsNvim.Core/Text/LineDelta.cs`, `src/VsNvim.Core/Sync/IDocumentSink.cs`, `src/VsNvim.Core/Sync/INvimBufferWriter.cs`.

- [ ] **Step 1: Create the types**

Create `src/VsNvim.Core/Text/LineDelta.cs`:
```csharp
namespace VsNvim.Core.Text
{
    /// <summary>A line-range replacement. Mirrors nvim_buf_attach on_lines: replace [FirstLine, OldLastLine) with NewLines.</summary>
    public sealed class LineDelta
    {
        public LineDelta(int firstLine, int oldLastLine, string[] newLines)
        {
            FirstLine = firstLine;
            OldLastLine = oldLastLine;
            NewLines = newLines;
        }
        public int FirstLine { get; }
        public int OldLastLine { get; }
        public string[] NewLines { get; }
    }
}
```

Create `src/VsNvim.Core/Sync/IDocumentSink.cs`:
```csharp
using VsNvim.Core.Text;

namespace VsNvim.Core.Sync
{
    /// <summary>Implemented by the VS side to apply an nvim-originated edit to the document.</summary>
    public interface IDocumentSink
    {
        void ReplaceLines(LineDelta delta);
    }
}
```

Create `src/VsNvim.Core/Sync/INvimBufferWriter.cs`:
```csharp
using VsNvim.Core.Text;

namespace VsNvim.Core.Sync
{
    /// <summary>Implemented by the RPC side to push a VS-originated edit into the nvim buffer.</summary>
    public interface INvimBufferWriter
    {
        void PushReplaceLines(LineDelta delta);
    }
}
```

- [ ] **Step 2: Build & commit**

Run: `dotnet build src/VsNvim.Core/VsNvim.Core.csproj`
Expected: Build succeeded.
```bash
git add src/VsNvim.Core/Text/LineDelta.cs src/VsNvim.Core/Sync/IDocumentSink.cs src/VsNvim.Core/Sync/INvimBufferWriter.cs
git commit -m "feat: add line delta and edit-sync interfaces"
```

---

## Task 2: Edit-sync engine (echo guards)

**Files:** Create `src/VsNvim.Core/Sync/EditSyncEngine.cs`; Test `tests/VsNvim.Core.Tests/Sync/EditSyncEngineTests.cs`.

- [ ] **Step 1: Write the failing test**

Create `tests/VsNvim.Core.Tests/Sync/EditSyncEngineTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using VsNvim.Core.Sync;
using VsNvim.Core.Text;
using Xunit;

namespace VsNvim.Core.Tests.Sync;

public class EditSyncEngineTests
{
    private sealed class FakeSink : IDocumentSink
    {
        public List<LineDelta> Applied { get; } = new();
        public Action<LineDelta> OnReplace { get; set; }
        public void ReplaceLines(LineDelta delta) { Applied.Add(delta); OnReplace?.Invoke(delta); }
    }

    private sealed class FakeNvim : INvimBufferWriter
    {
        public List<LineDelta> Pushed { get; } = new();
        public void PushReplaceLines(LineDelta delta) => Pushed.Add(delta);
    }

    [Fact]
    public void ApplyFromNvim_AppliesToSink()
    {
        var sink = new FakeSink();
        var nvim = new FakeNvim();
        var engine = new EditSyncEngine(sink, nvim);

        engine.ApplyFromNvim(new LineDelta(0, 1, new[] { "hello" }));

        Assert.Single(sink.Applied);
        Assert.False(engine.IsApplyingRemoteEdit);
        Assert.Empty(nvim.Pushed); // applying to VS must not push back to nvim
    }

    [Fact]
    public void ApplyFromNvim_SuppressesReentrantVsChange()
    {
        var nvim = new FakeNvim();
        var sink = new FakeSink();
        var engine = new EditSyncEngine(sink, nvim);

        // Simulate VS firing its Changed event synchronously while we apply the nvim edit.
        sink.OnReplace = _ => engine.OnVsTextChanged(new LineDelta(0, 1, new[] { "echo" }));

        engine.ApplyFromNvim(new LineDelta(0, 1, new[] { "hello" }));

        Assert.Empty(nvim.Pushed); // the reentrant VS change was suppressed by the guard
    }

    [Fact]
    public void OnVsTextChanged_PushesToNvim_ThenIgnoresTheEcho()
    {
        var nvim = new FakeNvim();
        var sink = new FakeSink();
        var engine = new EditSyncEngine(sink, nvim);

        // 1. User types in VS.
        engine.OnVsTextChanged(new LineDelta(0, 1, new[] { "typed" }));
        Assert.Single(nvim.Pushed);

        // 2. nvim echoes the same change back via on_lines.
        engine.ApplyFromNvim(new LineDelta(0, 1, new[] { "typed" }));
        Assert.Empty(sink.Applied); // echo must NOT be re-applied to VS
    }

    [Fact]
    public void GenuineNvimEdit_AfterVsEdit_StillApplies()
    {
        var nvim = new FakeNvim();
        var sink = new FakeSink();
        var engine = new EditSyncEngine(sink, nvim);

        engine.OnVsTextChanged(new LineDelta(0, 1, new[] { "typed" }));   // push
        engine.ApplyFromNvim(new LineDelta(0, 1, new[] { "typed" }));     // echo, ignored
        engine.ApplyFromNvim(new LineDelta(1, 2, new[] { "from :s" }));   // genuine, applied

        Assert.Single(sink.Applied);
        Assert.Equal(1, sink.Applied[0].FirstLine);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~EditSyncEngineTests"`
Expected: FAIL — `EditSyncEngine` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/VsNvim.Core/Sync/EditSyncEngine.cs`:
```csharp
using System;
using VsNvim.Core.Text;

namespace VsNvim.Core.Sync
{
    /// <summary>
    /// Coordinates bidirectional edits between the VS document and the nvim buffer,
    /// breaking both echo loops. Must be driven on a single thread (the VS UI thread).
    /// </summary>
    public sealed class EditSyncEngine
    {
        private readonly IDocumentSink _sink;
        private readonly INvimBufferWriter _nvim;
        private int _pendingSelfEdits; // VS-originated pushes awaiting their nvim on_lines echo

        public EditSyncEngine(IDocumentSink sink, INvimBufferWriter nvim)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _nvim = nvim ?? throw new ArgumentNullException(nameof(nvim));
        }

        /// <summary>True while applying an nvim edit to VS — used to suppress the resulting VS Changed event.</summary>
        public bool IsApplyingRemoteEdit { get; private set; }

        /// <summary>An edit reported by nvim (on_lines). Apply to VS unless it is the echo of our own push.</summary>
        public void ApplyFromNvim(LineDelta delta)
        {
            if (_pendingSelfEdits > 0)
            {
                _pendingSelfEdits--; // this on_lines is the echo of a VS edit we already applied locally
                return;
            }

            IsApplyingRemoteEdit = true;
            try
            {
                _sink.ReplaceLines(delta);
            }
            finally
            {
                IsApplyingRemoteEdit = false;
            }
        }

        /// <summary>An edit reported by the VS document. Push to nvim unless we caused it by applying an nvim edit.</summary>
        public void OnVsTextChanged(LineDelta delta)
        {
            if (IsApplyingRemoteEdit)
                return; // we are mid-apply of an nvim edit; do not bounce it back

            _pendingSelfEdits++;
            _nvim.PushReplaceLines(delta);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj --filter "FullyQualifiedName~EditSyncEngineTests"`
Expected: PASS (4 passed).

- [ ] **Step 5: Commit**

```bash
git add src/VsNvim.Core/Sync/EditSyncEngine.cs tests/VsNvim.Core.Tests/Sync/EditSyncEngineTests.cs
git commit -m "feat: add edit-sync engine that breaks both echo loops"
```

---

## Task 3: Wire the engine into the VS text view (integration)

**Files:** Create `src/VsNvim.Vsix/Editor/TextViewDocumentSink.cs`; modify `TextViewController` (Spec 04) to construct the engine and subscribe to both edit sources.

**Interfaces — Consumes:** `EditSyncEngine`, `IDocumentSink`, `INvimBufferWriter`, `LineDelta`, `NeovimRpcClient`.

- [ ] **Step 1: Implement the document sink + nvim writer**

Create `src/VsNvim.Vsix/Editor/TextViewDocumentSink.cs`:
```csharp
using System;
using System.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using VsNvim.Core.Rpc;
using VsNvim.Core.Sync;
using VsNvim.Core.Text;

namespace VsNvim.Vsix.Editor
{
    /// <summary>Applies nvim-originated line replacements to the VS buffer.</summary>
    internal sealed class TextViewDocumentSink : IDocumentSink
    {
        private readonly IWpfTextView _view;
        public TextViewDocumentSink(IWpfTextView view) => _view = view;

        public void ReplaceLines(LineDelta delta)
        {
            ITextSnapshot snapshot = _view.TextSnapshot;
            int firstLine = Math.Min(delta.FirstLine, snapshot.LineCount - 1);
            int oldLast = Math.Min(delta.OldLastLine, snapshot.LineCount);

            int startOffset = snapshot.GetLineFromLineNumber(firstLine).Start.Position;
            int endOffset = oldLast >= snapshot.LineCount
                ? snapshot.Length
                : snapshot.GetLineFromLineNumber(oldLast).Start.Position;

            string replacement = string.Join(Environment.NewLine, delta.NewLines);
            if (oldLast < snapshot.LineCount) replacement += Environment.NewLine;

            using (ITextEdit edit = _view.TextBuffer.CreateEdit())
            {
                edit.Replace(Span.FromBounds(startOffset, endOffset), replacement);
                edit.Apply();
            }
        }
    }

    /// <summary>Pushes VS-originated line replacements into the nvim buffer.</summary>
    internal sealed class NvimBufferWriter : INvimBufferWriter
    {
        private readonly NeovimRpcClient _client;
        public NvimBufferWriter(NeovimRpcClient client) => _client = client;

        public void PushReplaceLines(LineDelta delta)
        {
            object[] lines = Array.ConvertAll(delta.NewLines, l => (object)l);
            _ = _client.RequestAsync("nvim_buf_set_lines",
                new object[] { 0L, (long)delta.FirstLine, (long)delta.OldLastLine, false, lines },
                CancellationToken.None);
        }
    }
}
```

- [ ] **Step 2: Wire both edit sources in `TextViewController`**

In `TextViewController.StartAsync` (Spec 04), after attaching the UI, add:
```csharp
            _sink = new TextViewDocumentSink(_view);
            _engine = new EditSyncEngine(_sink, new NvimBufferWriter(_client));

            // nvim -> VS: subscribe to on_lines via nvim_buf_attach.
            await _client.RequestAsync("nvim_buf_attach", new object[] { 0L, true,
                new System.Collections.Generic.Dictionary<string, object>() }, _cts.Token);
            // on_lines arrives as a notification handled in OnNotification (Step 3).

            // VS -> nvim: subscribe to buffer changes.
            _view.TextBuffer.Changed += OnVsBufferChanged;
```
Add fields `private TextViewDocumentSink _sink; private EditSyncEngine _engine;` and the handler:
```csharp
        private void OnVsBufferChanged(object sender, Microsoft.VisualStudio.Text.TextContentChangedEventArgs e)
        {
            foreach (var change in e.Changes)
            {
                // Old line range that was replaced (half-open [firstLine, oldLastLine)).
                int firstLine = e.Before.GetLineNumberFromPosition(change.OldPosition);
                int oldLastLine = e.Before.GetLineNumberFromPosition(change.OldEnd) + 1;
                // New text occupying that range now, read from the post-change snapshot.
                int newLastLine = e.After.GetLineNumberFromPosition(change.NewEnd) + 1;
                string[] newLines = SnapshotLineRange(e.After, firstLine, newLastLine);
                _engine.OnVsTextChanged(new VsNvim.Core.Text.LineDelta(firstLine, oldLastLine, newLines));
            }
        }

        private static string[] SnapshotLineRange(Microsoft.VisualStudio.Text.ITextSnapshot snap, int firstLine, int lastLineExclusive)
        {
            int last = System.Math.Min(lastLineExclusive, snap.LineCount);
            var result = new System.Collections.Generic.List<string>();
            for (int i = firstLine; i < last; i++)
                result.Add(snap.GetLineFromLineNumber(i).GetText());
            return result.ToArray();
        }
```
And in `OnNotification`, handle the `on_lines` notification (method name `"nvim_buf_lines_event"` when using `nvim_buf_attach`'s default, or the `on_lines` Lua callback if you route through the bridge helper):
```csharp
            if (n.Method == "nvim_buf_lines_event")
            {
                // args: [buf, changedtick, firstline, lastline, linedata(array), more(bool)]
                int firstLine = Convert.ToInt32(n.Arguments[2]);
                int lastLine = Convert.ToInt32(n.Arguments[3]);
                var data = (object[])n.Arguments[4];
                string[] newLines = Array.ConvertAll(data, o => (string)o);
                RunOnUi(() => _engine.ApplyFromNvim(new VsNvim.Core.Text.LineDelta(firstLine, lastLine, newLines)));
            }
```
> **Verify the exact event name and arg layout** of `nvim_buf_attach` against the running nvim (`:help api-buffer-updates`). The `OnVsBufferChanged` line-range computation above is a first cut — confirm boundaries on multi-line deletes during manual verification and adjust `oldLastLine`/`newLines` accordingly.

- [ ] **Step 3: Manual verification (MVP DoD #2 & #5)**

In the Exp instance, on a multi-line file:
1. Normal-mode `dd` deletes the line **in the VS document** (text actually changes, not just the caret).
2. `x`, `dw`, `cw`, `2dd` all change VS text correctly.
3. Type in insert mode, `<Esc>`: the typed text is present and identical in both VS and (via `:%p` or inspecting) nvim — no duplication, no missing chars.
4. Run `:%s/foo/bar/g` in nvim → VS text updates to match. **No flicker loop, no runaway edits** (confirms both guards hold).
5. Stress: hold `j` then `dd` rapidly; confirm no desync and no exceptions in the Output pane.

- [ ] **Step 4: Commit**

```bash
git add src/VsNvim.Vsix/Editor/TextViewDocumentSink.cs src/VsNvim.Vsix/Editor/TextViewController.cs
git commit -m "feat: bidirectional edit sync between VS document and nvim buffer"
```

---

## Done when
- Edits from both sides land correctly with no echo loops (DoD #5).
- The `EditSyncEngine` unit tests are green and the manual stress test shows no desync.
- **If the guards do not hold under the stress test, stop and revisit the architecture** (this is the project's primary risk).
