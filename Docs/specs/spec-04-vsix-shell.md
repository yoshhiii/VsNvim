# Spec 04 — VSIX Shell, Process Launch & Key Routing (`VsNvim.Vsix`)

> Inherits all **Global Constraints** from [`../implementation-plan.md`](../implementation-plan.md).
> Depends on Specs 01, 02, 03. This is an **integration** spec: it crosses into the VS SDK, so xUnit cannot host it. Each task ends with a manual verification step run in the VS experimental instance ("Exp hive").

**Goal:** A `net472` VSIX that, for the active document, spawns `nvim --embed`, attaches the UI for redraw/mode data, renders a block cursor in normal mode, and routes normal-mode keystrokes into nvim so real Vim motions move the VS caret.

**Files:**
- Create: `src/VsNvim.Vsix/VsNvim.Vsix.csproj`
- Create: `src/VsNvim.Vsix/source.extension.vsixmanifest`
- Create: `src/VsNvim.Vsix/VsNvimPackage.cs`
- Create: `src/VsNvim.Core/Rpc/INeovimProcess.cs` *(interface lives in Core; impl in Vsix)*
- Create: `src/VsNvim.Vsix/Process/NeovimProcess.cs`
- Create: `src/VsNvim.Vsix/Editor/TextViewController.cs`
- Create: `src/VsNvim.Vsix/Editor/VsNvimCommandFilter.cs`
- Create: `src/VsNvim.Vsix/Editor/BlockCursorAdornment.cs`

---

## Task 0: Verify VS 2026 SDK editor surface (spike — Risk #1)

This de-risks the whole project before any VSIX code. **Output:** a short note appended to this spec recording what the installed VS 2026 SDK exposes. Do not proceed to Task 1 until confirmed.

- [ ] **Step 1: Confirm the classic in-proc editor extension model is supported**

In the VS 2026 installer, confirm the **Visual Studio extension development** workload is installed. Confirm these assemblies resolve as NuGet/SDK references for a `net472` project:
- `Microsoft.VisualStudio.SDK` (meta-package) or `Microsoft.VSSDK.BuildTools`
- `Microsoft.VisualStudio.Text.UI.Wpf` (for `IWpfTextView`, `IKeyProcessor`)
- `Microsoft.VisualStudio.Shell.15.0` / `Microsoft.VisualStudio.OLE.Interop` (for `IOleCommandTarget`, `IVsTextView`)

Expected: all resolve. If only the out-of-process **VisualStudio.Extensibility** SDK is available and the classic editor types are absent, **stop** — the embedding approach as designed is not viable on this SDK and the plan needs revision.

- [ ] **Step 2: Confirm the key-interception path**

Confirm both are available (VsVim relies on both):
- `IVsTextViewCreationListener` + `IVsTextView.AddCommandFilter(IOleCommandTarget, out IOleCommandTarget)` — primary path to intercept keys *before* IntelliSense.
- `IKeyProcessorProvider` / `KeyProcessor` (MEF) — for WPF-level keys.

Record the exact namespaces/versions found in a `## SDK Notes` section at the bottom of this file.

- [ ] **Step 3: Confirm `nvim` availability for integration**

Run: `nvim --version`
Expected: Neovim ≥ 0.10. If absent, install and add to `PATH` before integration tasks.

---

## Task 1: Author the VSIX project

**Files:** Create `src/VsNvim.Vsix/VsNvim.Vsix.csproj`, `source.extension.vsixmanifest`, `VsNvimPackage.cs`.

> No `dotnet new` VSIX template exists. Author the project files directly. If the team prefers, create a "VSIX Project (C#)" via the VS 2026 New Project dialog into `src/VsNvim.Vsix`, then reconcile the generated files with the references below.

- [ ] **Step 1: Create the project file**

Create `src/VsNvim.Vsix/VsNvim.Vsix.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <UseWPF>true</UseWPF>
    <GeneratePkgDefFile>true</GeneratePkgDefFile>
    <IncludeAssemblyInVSIXContainer>true</IncludeAssemblyInVSIXContainer>
    <CreateVsixContainer>true</CreateVsixContainer>
    <DeployExtension>true</DeployExtension>
    <StartAction>Program</StartAction>
    <StartProgram>$(DevEnvDir)devenv.exe</StartProgram>
    <StartArguments>/rootsuffix Exp</StartArguments>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.VSSDK.BuildTools" Version="17.*" PrivateAssets="all" />
    <PackageReference Include="Microsoft.VisualStudio.SDK" Version="17.*" ExcludeAssets="runtime" />
    <PackageReference Include="MessagePack" Version="2.5.198" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\VsNvim.Core\VsNvim.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="source.extension.vsixmanifest">
      <SubType>Designer</SubType>
    </None>
  </ItemGroup>
</Project>
```
> Bump the `17.*` SDK package versions to the VS 2026 line confirmed in Task 0's SDK Notes.

- [ ] **Step 2: Create the manifest**

Create `src/VsNvim.Vsix/source.extension.vsixmanifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Id="VsNvim.0c0f2e8e-vsnvim" Version="0.1.0" Language="en-US" Publisher="VsNvim" />
    <DisplayName>VsNvim</DisplayName>
    <Description>Vim emulation for Visual Studio backed by an embedded Neovim engine.</Description>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[18.0,)" />
  </Installation>
  <Dependencies>
    <Dependency Id="Microsoft.Framework.NDP" DisplayName=".NET Framework" Version="[4.7.2,)" />
  </Dependencies>
  <Assets>
    <Asset Type="Microsoft.VisualStudio.MefComponent" d:Source="Project" d:ProjectName="%CurrentProject%" Path="|%CurrentProject%|" />
    <Asset Type="Microsoft.VisualStudio.VsPackage" d:Source="Project" d:ProjectName="%CurrentProject%" Path="|%CurrentProject%;PkgdefProjectOutputGroup|" />
  </Assets>
</PackageManifest>
```
> Confirm the VS 2026 `InstallationTarget` version range in Task 0 (shown here as `[18.0,)`; adjust to the actual product version).

- [ ] **Step 3: Create a minimal package**

Create `src/VsNvim.Vsix/VsNvimPackage.cs`:
```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace VsNvim.Vsix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class VsNvimPackage : AsyncPackage
    {
        public const string PackageGuidString = "0c0f2e8e-0000-4000-8000-vsnvim00pkg0";

        protected override Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
            => Task.CompletedTask; // Editor wiring is MEF-driven (TextViewController); nothing to do here yet.
    }
}
```
> Replace `PackageGuidString` with a real GUID (`[guid]::NewGuid()` in PowerShell). The placeholder above is not a valid GUID.

- [ ] **Step 4: Add to solution and build**

Run:
```bash
dotnet sln VsNvim.slnx add src/VsNvim.Vsix/VsNvim.Vsix.csproj
dotnet build src/VsNvim.Vsix/VsNvim.Vsix.csproj
```
Expected: Build succeeded; a `.vsix` is produced under `src/VsNvim.Vsix/bin/Debug`.

- [ ] **Step 5: Manual verification**

1. Set `VsNvim.Vsix` as startup project; press F5 → a VS 2026 **Exp** instance launches with the extension loaded.
2. Help → About (or Extensions → Manage Extensions) shows **VsNvim 0.1.0**.

- [ ] **Step 6: Commit**

```bash
git add src/VsNvim.Vsix/ VsNvim.slnx
git commit -m "feat: scaffold VsNvim VSIX shell that loads in the Exp instance"
```

---

## Task 2: Launch nvim and connect the RPC client

**Files:** Create `src/VsNvim.Core/Rpc/INeovimProcess.cs`, `src/VsNvim.Vsix/Process/NeovimProcess.cs`.

**Interfaces — Produces:** `INeovimProcess { Stream Stdio { get; } void Start(); void Dispose(); }`; `NeovimProcess : INeovimProcess` (spawns `nvim --embed`).

- [ ] **Step 1: Define the Core abstraction**

Create `src/VsNvim.Core/Rpc/INeovimProcess.cs`:
```csharp
using System;
using System.IO;

namespace VsNvim.Core.Rpc
{
    /// <summary>A running nvim child process exposing a bidirectional msgpack-rpc stdio stream.</summary>
    public interface INeovimProcess : IDisposable
    {
        Stream Stdio { get; }
        void Start();
    }
}
```

- [ ] **Step 2: Implement the launcher in the VSIX**

Create `src/VsNvim.Vsix/Process/NeovimProcess.cs`:
```csharp
using System;
using System.Diagnostics;
using System.IO;
using VsNvim.Core.Rpc;

namespace VsNvim.Vsix.Process
{
    public sealed class NeovimProcess : INeovimProcess
    {
        private System.Diagnostics.Process _process;
        private DuplexProcessStream _stdio;

        public Stream Stdio => _stdio ?? throw new InvalidOperationException("Call Start() first.");

        public void Start()
        {
            var psi = new ProcessStartInfo("nvim", "--embed")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _process = System.Diagnostics.Process.Start(psi);
            _stdio = new DuplexProcessStream(
                _process.StandardOutput.BaseStream,
                _process.StandardInput.BaseStream);
        }

        public void Dispose()
        {
            try { if (_process != null && !_process.HasExited) _process.Kill(); }
            catch { /* best effort */ }
            _stdio?.Dispose();
            _process?.Dispose();
        }
    }

    /// <summary>Joins a process's stdout (read) and stdin (write) into one full-duplex Stream.</summary>
    internal sealed class DuplexProcessStream : Stream
    {
        private readonly Stream _read;
        private readonly Stream _write;
        public DuplexProcessStream(Stream read, Stream write) { _read = read; _write = write; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override int Read(byte[] b, int o, int c) => _read.Read(b, o, c);
        public override System.Threading.Tasks.Task<int> ReadAsync(byte[] b, int o, int c, System.Threading.CancellationToken t) => _read.ReadAsync(b, o, c, t);
        public override void Write(byte[] b, int o, int c) => _write.Write(b, o, c);
        public override System.Threading.Tasks.Task WriteAsync(byte[] b, int o, int c, System.Threading.CancellationToken t) => _write.WriteAsync(b, o, c, t);
        public override void Flush() => _write.Flush();
        public override System.Threading.Tasks.Task FlushAsync(System.Threading.CancellationToken t) => _write.FlushAsync(t);
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _read.Dispose(); _write.Dispose(); } }
    }
}
```

- [ ] **Step 3: Manual verification (smoke test via a scratch command)**

Temporarily, in `VsNvimPackage.InitializeAsync`, start the process and issue one request:
```csharp
var proc = new VsNvim.Vsix.Process.NeovimProcess();
proc.Start();
var client = new VsNvim.Core.Rpc.NeovimRpcClient(proc.Stdio);
_ = client.RunReadLoopAsync(CancellationToken.None);
object apiInfo = await client.RequestAsync("nvim_get_api_info", System.Array.Empty<object>(), cancellationToken);
System.Diagnostics.Debug.WriteLine($"[VsNvim] api info arity: {((object[])apiInfo).Length}");
```
1. F5 the Exp instance. In the **Output → Debug** pane, confirm `[VsNvim] api info arity: 2`.
2. Confirm no `nvim` window pops up (headless).
3. Remove the scratch code after confirming. (The real wiring lands in Task 3's `TextViewController`.)

- [ ] **Step 4: Commit**

```bash
git add src/VsNvim.Core/Rpc/INeovimProcess.cs src/VsNvim.Vsix/Process/NeovimProcess.cs
git commit -m "feat: launch embedded nvim and confirm round-trip RPC over stdio"
```

---

## Task 3: Attach UI, track mode & cursor per text view

**Files:** Create `src/VsNvim.Vsix/Editor/TextViewController.cs` (MEF `IWpfTextViewCreationListener`).

**Interfaces — Consumes:** `NeovimRpcClient` (Spec 01), `RedrawDispatcher`/`VimModeMap` (Spec 02), `CoordinateMapper` (Spec 03). **Produces:** a per-view controller exposing `CurrentMode` and raising `ModeChanged`, used by Tasks 4 & later specs.

- [ ] **Step 1: Implement the controller**

Create `src/VsNvim.Vsix/Editor/TextViewController.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VsNvim.Core.Redraw;
using VsNvim.Core.Rpc;
using VsNvim.Core.Text;

namespace VsNvim.Vsix.Editor
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    public sealed class TextViewControllerProvider : IWpfTextViewCreationListener
    {
        public void TextViewCreated(IWpfTextView textView)
        {
            var controller = new TextViewController(textView);
            textView.Properties[typeof(TextViewController)] = controller;
            controller.StartAsync(CancellationToken.None);
            textView.Closed += (_, __) => controller.Dispose();
        }
    }

    public sealed class TextViewController : IDisposable
    {
        private readonly IWpfTextView _view;
        private readonly Process.NeovimProcess _proc = new Process.NeovimProcess();
        private NeovimRpcClient _client;
        private readonly RedrawDispatcher _redraw = new RedrawDispatcher();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public TextViewController(IWpfTextView view) => _view = view;

        public VimMode CurrentMode { get; private set; } = VimMode.Normal;
        public event Action<VimMode> ModeChanged;

        public async void StartAsync(CancellationToken ct)
        {
            _proc.Start();
            _client = new NeovimRpcClient(_proc.Stdio);
            _client.NotificationReceived += OnNotification;
            _ = _client.RunReadLoopAsync(_cts.Token);

            // Seed nvim with the document's lines.
            string[] lines = SnapshotLines();
            await _client.RequestAsync("nvim_buf_set_lines",
                new object[] { 0L, 0L, -1L, false, lines }, _cts.Token);

            // Attach the UI grid for redraw/mode events (size is nominal; we do not render the grid).
            var uiOpts = new Dictionary<string, object> { ["ext_linegrid"] = true };
            await _client.RequestAsync("nvim_ui_attach",
                new object[] { 120L, 40L, uiOpts }, _cts.Token);

            _redraw.FlushReceived += OnFlush;
        }

        private void OnNotification(RpcNotification n)
        {
            if (n.Method == "redraw")
                _redraw.Process((object[])n.Arguments[0]);
        }

        private async void OnFlush()
        {
            // Authoritative mode + cursor after each redraw cycle.
            var modeResult = (Dictionary<string, object>)await _client.RequestAsync(
                "nvim_get_mode", Array.Empty<object>(), _cts.Token);
            VimMode mode = VimModeMap.FromModeCode((string)modeResult["mode"]);
            if (mode != CurrentMode)
            {
                CurrentMode = mode;
                RunOnUi(() => ModeChanged?.Invoke(mode));
            }

            var cursor = (object[])await _client.RequestAsync(
                "nvim_win_get_cursor", new object[] { 0L }, _cts.Token);
            long row = Convert.ToInt64(cursor[0]);   // 1-based
            long byteCol = Convert.ToInt64(cursor[1]);
            RunOnUi(() => MoveCaret(new NvimPosition(row, byteCol)));
        }

        private void MoveCaret(NvimPosition pos)
        {
            var snapshot = _view.TextSnapshot;
            int lineIndex = Math.Min((int)(pos.Row - 1), snapshot.LineCount - 1);
            var line = snapshot.GetLineFromLineNumber(lineIndex);
            int charCol = CoordinateMapper.ByteToCharColumn(line.GetText(), (int)pos.Column);
            int offset = line.Start.Position + Math.Min(charCol, line.Length);
            _view.Caret.MoveTo(new Microsoft.VisualStudio.Text.SnapshotPoint(snapshot, offset));
        }

        private string[] SnapshotLines()
        {
            var snapshot = _view.TextSnapshot;
            var lines = new List<string>(snapshot.LineCount);
            foreach (var l in snapshot.Lines) lines.Add(l.GetText());
            return lines.ToArray();
        }

        private void RunOnUi(Action action)
        {
            if (_view.VisualElement.Dispatcher.CheckAccess()) action();
            else _view.VisualElement.Dispatcher.BeginInvoke(action);
        }

        internal NeovimRpcClient Client => _client;

        public void Dispose()
        {
            _cts.Cancel();
            _proc.Dispose();
            _client?.Dispose();
            _cts.Dispose();
        }
    }
}
```

- [ ] **Step 2: Manual verification**

1. F5 the Exp instance, open a `.txt`/`.cs` file.
2. Set a breakpoint in `OnFlush`; confirm it hits and `mode` resolves to `VimMode.Normal`.
3. Confirm no exceptions in the Output pane and the document content seeded into nvim matches (inspect via `nvim_buf_get_lines` in the immediate window if needed).

- [ ] **Step 3: Commit**

```bash
git add src/VsNvim.Vsix/Editor/TextViewController.cs
git commit -m "feat: per-view nvim attach, mode tracking, and cursor sync from nvim"
```

---

## Task 4: Route normal-mode keys + render the block cursor

**Files:** Create `src/VsNvim.Vsix/Editor/VsNvimCommandFilter.cs`, `src/VsNvim.Vsix/Editor/BlockCursorAdornment.cs`.

> Use the `IOleCommandTarget` command-filter path confirmed in Task 0 — it is the reliable way to receive keys before IntelliSense. The filter forwards keys to nvim **only when `CurrentMode != Insert`** (Global Constraint: nvim owns non-insert modes; Spec 06 implements the full insert handoff). For this task, treat insert as "let VS handle it".

- [ ] **Step 1: Implement the command filter (key → nvim_input)**

Create `src/VsNvim.Vsix/Editor/VsNvimCommandFilter.cs`:
```csharp
using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using VsNvim.Core.Redraw;

namespace VsNvim.Vsix.Editor
{
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    public sealed class CommandFilterProvider : IVsTextViewCreationListener
    {
        [Import] internal IVsEditorAdaptersFactoryService AdapterService = null;

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            IWpfTextView view = AdapterService.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new VsNvimCommandFilter(view);
            textViewAdapter.AddCommandFilter(filter, out IOleCommandTarget next);
            filter.Next = next;
        }
    }

    internal sealed class VsNvimCommandFilter : IOleCommandTarget
    {
        private readonly IWpfTextView _view;
        public IOleCommandTarget Next { get; set; }
        public VsNvimCommandFilter(IWpfTextView view) => _view = view;

        private TextViewController Controller =>
            _view.Properties.TryGetProperty(typeof(TextViewController), out TextViewController c) ? c : null;

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            var controller = Controller;
            if (controller != null && controller.CurrentMode != VimMode.Insert &&
                pguidCmdGroup == VSConstants.VSStd2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR)
            {
                var typed = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
                _ = controller.Client.NotifyAsync("nvim_input", new object[] { typed.ToString() }, default);
                return VSConstants.S_OK; // swallow; nvim drives the buffer
            }
            return Next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
            => Next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
    }
}
```
> Special keys (`<Esc>`, `<BS>`, `<CR>`, arrows) arrive as distinct `VSStd2KCmdID` values, not `TYPECHAR`. Map them to nvim notation (`"<Esc>"`, `"<BS>"`, ...) in `Exec`. Implement at minimum `<Esc>` here so you can leave insert mode; the full table lands in Spec 06.

- [ ] **Step 2: Implement the block-cursor adornment**

Create `src/VsNvim.Vsix/Editor/BlockCursorAdornment.cs`:
```csharp
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VsNvim.Core.Redraw;

namespace VsNvim.Vsix.Editor
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    public sealed class BlockCursorProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VsNvimBlockCursor")]
        [Order(After = PredefinedAdornmentLayers.Caret)]
        public AdornmentLayerDefinition BlockCursorLayer = null;

        public void TextViewCreated(IWpfTextView textView) => new BlockCursorAdornment(textView);
    }

    internal sealed class BlockCursorAdornment
    {
        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;

        public BlockCursorAdornment(IWpfTextView view)
        {
            _view = view;
            _layer = view.GetAdornmentLayer("VsNvimBlockCursor");
            view.Caret.PositionChanged += (_, __) => Redraw();
            if (view.Properties.TryGetProperty(typeof(TextViewController), out TextViewController c))
                c.ModeChanged += _ => Redraw();
        }

        private void Redraw()
        {
            _layer.RemoveAllAdornments();
            if (!_view.Properties.TryGetProperty(typeof(TextViewController), out TextViewController controller))
                return;
            // Block cursor in every non-insert mode; VS draws the normal line caret in insert.
            if (controller.CurrentMode == VimMode.Insert) return;

            var caretLine = _view.Caret.ContainingTextViewLine;
            var caretPoint = _view.Caret.Position.BufferPosition;
            var bounds = caretLine.GetCharacterBounds(caretPoint);
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = bounds.Width > 0 ? bounds.Width : 8,
                Height = bounds.Height,
                Fill = new SolidColorBrush(Color.FromArgb(120, 120, 200, 120)),
            };
            Canvas.SetLeft(rect, bounds.Left);
            Canvas.SetTop(rect, bounds.Top);
            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, caretLine.Extent, null, rect, null);
        }
    }
}
```

- [ ] **Step 3: Manual verification (MVP DoD #1 & partial #2)**

In the Exp instance, open a multi-line file and confirm:
1. A **block cursor** is drawn over the caret in normal mode.
2. `j`/`k`/`h`/`l` move the caret (driven by nvim, not VS).
3. `w`/`b`/`e` jump by word; `0`/`$` go to line ends.
4. `x` deletes the char under the cursor; `dd` deletes the line; `2dd` deletes two lines. *(Buffer edits land via Spec 05 — if the caret moves but text does not yet change, that is expected until Spec 05; confirm at minimum that the keys reach nvim by watching the cursor respond to motions.)*
5. Pressing `i` removes the block cursor and restores the VS line caret (insert handed to VS).

- [ ] **Step 4: Commit**

```bash
git add src/VsNvim.Vsix/Editor/VsNvimCommandFilter.cs src/VsNvim.Vsix/Editor/BlockCursorAdornment.cs
git commit -m "feat: route normal-mode keys to nvim and render a block cursor"
```

---

## Done when
- Normal-mode motions move the VS caret via real nvim, and the block cursor reflects mode.
- Note: visible **text** edits depend on Spec 05 (edit sync). This spec proves input + mode + cursor; Spec 05 closes the loop on buffer content.

## SDK Notes
*(Fill in during Task 0: confirmed assembly names, package versions, VS 2026 product version range, and any API deltas from the VS 17.x signatures shown above.)*
