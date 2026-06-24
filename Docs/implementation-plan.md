# VsNvim Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. The detailed, bite-sized tasks live in [`specs/`](specs/README.md). Steps use checkbox (`- [ ]`) syntax for tracking.

**Date:** 2026-06-24

**Goal:** A Visual Studio 2026 extension that delivers true Vim fidelity by embedding a real Neovim process as the editing engine, rather than re-implementing Vim semantics in C#.

**Architecture:** Neovim runs out-of-process (`nvim --embed`) and is the source of truth for normal/visual/operator-pending/command-line modes. The VS editor renders text and owns insert mode (so IntelliSense, snippets, and refactors keep working). A bridge extension shuttles three channels over msgpack-RPC: keystrokes (VS → nvim), buffer edits (both directions), and redraw/mode/cursor metadata (nvim → VS). See the architecture diagram in the originating design discussion.

**Tech Stack:**
- **VsNvim.Core** — `netstandard2.0` class library. All logic that does *not* touch the VS SDK: msgpack-RPC codec + client, redraw-event decoding, mode mapping, coordinate conversion, edit-sync state machine. This is the unit-tested heart of the project.
- **VsNvim.Vsix** — `net472` VSIX (classic in-proc VSSDK + MEF). Hosts `IKeyProcessor`, `IWpfTextView` access, the adornment layer, and document apply. Created as a manual step (no `dotnet new` template exists — see Spec 04).
- **VsNvim.Core.Tests** — `net8.0` xUnit project.
- **MessagePack-CSharp** (neuecc) v2.5.x — msgpack primitives (`MessagePackReader`/`Writer`). Supports `netstandard2.0`.
- **Neovim ≥ 0.10** on `PATH`.

---

## Global Constraints

These apply to every task; copied verbatim into each spec's constraint block.

- **Target frameworks are fixed:** `VsNvim.Core` = `netstandard2.0`; `VsNvim.Core.Tests` = `net8.0`; `VsNvim.Vsix` = `net472`. Do not change a TFM to make something compile.
  - **Why `net472` and not modern .NET (verified against VS 2026 docs, June 2026):** `devenv.exe` is still a **.NET Framework** process in the VS 2026 line, so **in-process** extensions must target .NET Framework. Microsoft confirms in-process VisualStudio.Extensibility extensions run "against the .NET Framework runtime"; only **out-of-process** extensions run on modern .NET (.NET 8, rolling to .NET 10). This project needs in-proc editor control (`IKeyProcessor`/command filter, `IWpfTextView`, adornments, synchronous keystroke interception), which the out-of-proc remote `IClientContext` editor model cannot provide today. "VS 2026 is built for .NET 10" refers to the SDK/target support, **not** the host process runtime. Refs: [Managing .NET runtime versions](https://devblogs.microsoft.com/visualstudio/visualstudio-extensibility-managing-net-runtime-versions/), [VS 2026 compatibility](https://learn.microsoft.com/en-us/visualstudio/releases/2026/compatibility).
  - **Framework footprint is intentionally minimal:** because `netstandard2.0` is consumable by .NET Framework 4.7.2+, only the thin VSIX shell is Framework-bound. `VsNvim.Core` is Framework-free and would port to an out-of-process .NET 8/10 host **if/when** the out-of-proc editor API matures enough for synchronous key/caret control. Keep the in-proc surface in `VsNvim.Vsix` as thin as possible to preserve that migration path.
- **`VsNvim.Core` must never reference any `Microsoft.VisualStudio.*` package.** If a task needs a VS type, it belongs in `VsNvim.Vsix`, and `Core` exposes a plain interface for it. (This is also what keeps the future out-of-proc migration cheap.)
- **Neovim ≥ 0.10** must be discoverable on `PATH` as `nvim`. Integration tests skip (not fail) when `nvim` is absent.
- **The mode-ownership rule is non-negotiable:** Neovim owns normal/visual/operator-pending/command-line; the VS editor owns insert mode. No task may forward ordinary insert-mode character keys to Neovim.
- **MVP is single-buffer, single-window.** One nvim instance, one attached buffer matching the active VS document. Splits, tabs, and multi-document sync are out of scope.
- **VS owns the undo stack** for the MVP. Do not wire nvim undo persistence.
- **RPC values use only the nvim-safe primitive set:** null, bool, long, double, string, byte[], `object[]` (array), `IDictionary<string, object>` (map). No msgpack ext/typeless encoding (nvim rejects it).
- **All async RPC calls take a `CancellationToken`** and must not block the VS UI thread.
- **TDD is required** for every `VsNvim.Core` unit. Integration tasks that cross into the VS SDK use the documented manual verification procedure in their spec (xUnit cannot host `devenv`).
- **Commit after every green step.** Conventional Commit messages (`feat:`, `test:`, `chore:`).

---

## File Structure

```
VsNvim/
  VsNvim.slnx
  src/
    VsNvim.Core/                 (netstandard2.0)
      Rpc/
        MsgPackValueWriter.cs        value -> msgpack bytes (nvim-safe set)
        MsgPackValueReader.cs        msgpack bytes -> CLR objects
        RpcMessage.cs                Request/Response/Notification records
        RpcCodec.cs                  frame encode/decode
        NeovimRpcClient.cs           Stream-based request/response + notifications
        INeovimProcess.cs            abstraction over the nvim child process
      Redraw/
        RedrawEvent.cs               decoded "redraw" batches
        RedrawDispatcher.cs          raises typed events (mode_change, etc.)
        VimMode.cs                   enum + mapping from nvim mode strings
      Text/
        Position.cs                  (row, col) value type
        CoordinateMapper.cs          nvim byte/(1,0) <-> VS UTF-16/(0,0)
        LineDelta.cs                 on_lines change representation
      Sync/
        EditSyncEngine.cs            origin-tagged edit queue + echo guard
        IDocumentSink.cs             interface VS implements to apply edits
      VsNvim.Core.csproj
    VsNvim.Vsix/                 (net472, manual — Spec 04)
      VsNvimPackage.cs
      Editor/
        VsNvimKeyProcessor.cs        + IKeyProcessorProvider
        BlockCursorAdornment.cs      + adornment layer definition
        TextViewDocumentSink.cs      implements Core.Sync.IDocumentSink
        TextViewController.cs        wires Core to one IWpfTextView
      source.extension.vsixmanifest
      VsNvim.Vsix.csproj
  tests/
    VsNvim.Core.Tests/           (net8.0, xUnit)
  Docs/
    implementation-plan.md       (this file)
    specs/                        (task-by-task MVP plan)
```

---

## MVP Definition of Done

The MVP is the smallest build that proves the architecture is viable. It is **done** when, inside an experimental VS 2026 instance, on a single open document:

1. Opening the document spawns nvim, attaches the UI, and shows a **block cursor** in normal mode and a line cursor in insert mode.
2. Normal-mode motions and operators driven by real Vim work: `h j k l w b e`, `x`, `dd`, `dw`, `cw`, `2dd`, `.` (within nvim's own model), and the cursor stays in sync.
3. Pressing `i`/`a`/`o` hands off to VS; typing uses native VS insert (IntelliSense available); `<Esc>` resyncs the edited text back into nvim and returns to a correct block cursor.
4. Visual mode (`v`, `V`) renders a VS selection; `:` opens a command line rendered by the extension and `:w`, `:%s/a/b/g` execute through nvim.
5. Edits originating in nvim (e.g. `:%s`) and edits originating in VS (insert typing) both land correctly with no echo loops or desync.

## Task Roadmap (→ MVP)

Each row links to a spec. Specs 01–03 and the state machine in 05 are pure-`Core` TDD with complete code. Specs 04, 06, 07 are VS-integration tasks with manual verification procedures.

| # | Spec | Deliverable | Test mode | MVP DoD |
|---|------|-------------|-----------|---------|
| 01 | [RPC transport](specs/spec-01-rpc-transport.md) | msgpack value codec + `NeovimRpcClient` over a `Stream` | xUnit (TDD) | foundation |
| 02 | [Redraw & mode](specs/spec-02-redraw-mode.md) | decode `redraw` batches; map nvim mode → `VimMode` | xUnit (TDD) | #1, #3 |
| 03 | [Coordinate mapping](specs/spec-03-coordinate-mapping.md) | byte/(1,0) ↔ UTF-16/(0,0), tabs, multibyte | xUnit (TDD) | #2, #4 |
| 04 | [VSIX shell + key processor](specs/spec-04-vsix-shell.md) | net472 VSIX, spawn nvim, `ext`-UI attach, block cursor, key routing | manual | #1, #2 |
| 05 | [Edit sync](specs/spec-05-edit-sync.md) | origin-tagged edit queue (TDD) + `IDocumentSink` apply | xUnit + manual | #2, #5 |
| 06 | [Insert handoff](specs/spec-06-insert-handoff.md) | mode-gated key routing; `<Esc>` resync | manual | #3 |
| 07 | [Visual + cmdline](specs/spec-07-visual-cmdline.md) | visual selection rendering; `ext_cmdline` UI | manual | #4 |

## Sequencing & Dependencies

- **01 → 02, 05**: everything needs the RPC client.
- **02, 03 → 04**: the VSIX shell consumes mode/redraw decoding and coordinate mapping.
- **04 → 05 → 06 → 07**: integration tasks are strictly ordered; each builds on a working previous instance.
- Specs **01, 02, 03** are independent of each other and of the VS SDK — they can be built in parallel by separate agents before any VSIX work begins.

## Key Risks (carried from design)

1. **VS 2026 SDK editor API surface** — *partially resolved (June 2026):* `devenv.exe` remains a .NET Framework process, so the classic in-proc `net472` model with `IKeyProcessor`/`IWpfTextView`/adornment APIs is the correct and supported path (see the TFM rationale in Global Constraints). The out-of-process *VisualStudio.Extensibility* model runs on modern .NET but its remote `IClientContext` editor surface is insufficient for synchronous keystroke/caret control today. **Still to confirm in [Spec 04 Task 0](specs/spec-04-vsix-shell.md):** the exact VS 2026 package/assembly versions and that `IVsTextView.AddCommandFilter` + the MEF editor types resolve against the installed SDK.
2. **Edit-sync race conditions** (Spec 05) — async RPC vs. synchronous UI-thread edits is the make-or-break engineering problem. The origin-tagged queue + echo guard is the mitigation; if it does not hold here, the approach needs rethinking.
3. **`.`/dot-repeat fidelity in insert mode** (Spec 06) — accepted limitation: nvim sees the net text change on `<Esc>`, not the keystrokes. Documented, not solved, in the MVP.

---

## Execution Handoff

See [`specs/README.md`](specs/README.md) for execution order and conventions. After review of this plan, choose:

1. **Subagent-Driven (recommended)** — fresh subagent per spec task, review between tasks.
2. **Inline Execution** — execute in-session with checkpoints.
