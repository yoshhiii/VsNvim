# VsNvim MVP — Spec Index

These specs decompose the MVP defined in [`../implementation-plan.md`](../implementation-plan.md) into bite-sized, independently testable tasks. Read the plan's **Global Constraints** first — they apply to every spec here.

## Execution order

```
        ┌──────────────────────┐
        │ 01 RPC transport     │  (Core / TDD)
        └──────────┬───────────┘
            ┌───────┴────────┐
            ▼                ▼
  ┌──────────────────┐  ┌──────────────────┐
  │ 02 Redraw & mode │  │ 03 Coordinate map│   (Core / TDD, parallel)
  └─────────┬────────┘  └─────────┬────────┘
            └────────┬────────────┘
                     ▼
        ┌──────────────────────────┐
        │ 04 VSIX shell + keys      │  (Integration / manual)
        └────────────┬─────────────┘
                     ▼
        ┌──────────────────────────┐
        │ 05 Edit sync              │  (Core TDD + integration)
        └────────────┬─────────────┘
                     ▼
        ┌──────────────────────────┐
        │ 06 Insert handoff         │  (Integration / manual)
        └────────────┬─────────────┘
                     ▼
        ┌──────────────────────────┐
        │ 07 Visual + cmdline       │  (Integration / manual)
        └──────────────────────────┘
```

Specs **01, 02, 03** have no VS SDK dependency and no dependency on each other (02 and 03 only need 01's types) — they can be built in parallel by separate agents.

## Spec list

| # | File | Test mode |
|---|------|-----------|
| 01 | [spec-01-rpc-transport.md](spec-01-rpc-transport.md) | xUnit (TDD) |
| 02 | [spec-02-redraw-mode.md](spec-02-redraw-mode.md) | xUnit (TDD) |
| 03 | [spec-03-coordinate-mapping.md](spec-03-coordinate-mapping.md) | xUnit (TDD) |
| 04 | [spec-04-vsix-shell.md](spec-04-vsix-shell.md) | manual |
| 05 | [spec-05-edit-sync.md](spec-05-edit-sync.md) | xUnit + manual |
| 06 | [spec-06-insert-handoff.md](spec-06-insert-handoff.md) | manual |
| 07 | [spec-07-visual-cmdline.md](spec-07-visual-cmdline.md) | manual |

## Conventions

- **TDD cycle per unit:** write failing test → run & confirm failure → minimal implementation → run & confirm pass → commit.
- **Run Core tests:** `dotnet test tests/VsNvim.Core.Tests/VsNvim.Core.Tests.csproj`
- **Run one test:** `dotnet test --filter "FullyQualifiedName~<TestName>"`
- **Manual verification (integration specs):** each spec ends with a numbered procedure to run in the VS experimental instance (`dotnet build` of the VSIX deploys it to the Exp hive; launch via the project's debug profile). A spec is done only when every numbered observation is confirmed.
- **`nvim`-dependent integration tests** are decorated so they **skip** (not fail) when `nvim` is not on `PATH`.
