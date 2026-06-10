---
name: guardrails-dev-knowledge
description: |
  Guardrails repo development knowledge: solution layout, build/test/run commands,
  dotnet-tool packaging, testing conventions and gotchas, dogfooding safety rules.
  Use when implementing, testing, running, or packaging the harness, or onboarding
  an agent to the codebase.

  SELF-UPDATING: When your work changes the solution layout, conventions, packaging,
  or any fact below, you MUST update the affected section(s) before completing your
  task.
---

# Guardrails Dev Knowledge

## Solution layout

```
Guardrails.sln                  # classic .sln (NOT .slnx — CI's .NET 8 SDK can't read it)
global.json                     # pins SDK 8.0.100, rollForward latestFeature — local == CI
src/Guardrails.Core/            # Model, Loading, Graph, Execution, State, Journal, Prompts — NO UI deps
src/Guardrails.Cli/             # the dotnet tool: PackAsTool, ToolCommandName=guardrails
tests/Guardrails.Core.Tests/    # unit — fake runners/probes, TestData fixture folders
tests/Guardrails.Integration.Tests/  # real processes; plan builders in temp dirs
examples/hello-guardrails/      # golden example: runnable demo + acceptance fixture
.github/workflows/ci.yml        # 3-OS matrix: windows/ubuntu/macos-latest
```

TFM `net8.0` everywhere; CLI has `<RollForward>LatestMajor</RollForward>`.
`TreatWarningsAsErrors` everywhere. NuGet: System.CommandLine 2.0.9 (GA API:
`SetAction`/`GetRequiredValue`), Spectre.Console (CLI only, behind `IRunObserver`),
YamlDotNet (Core, frontmatter), xunit.v3.

## Packaging (dotnet tool)

- **PackageId `ServantSoftware.Guardrails`** (org convention — NOT bare `guardrails`);
  `ToolCommandName` stays `guardrails` (that is the invoked command). Package metadata
  (Authors/Company `Servant Software LLC`, Description, `PackageLicenseExpression` MIT with
  a root `LICENSE`, RepositoryUrl, PackageTags, README packed) lives in
  `src/Guardrails.Cli/Guardrails.Cli.csproj`. Version: `1.0.0-preview.1`.
- Local pack + acceptance (leaves the machine clean):
  ```powershell
  dotnet pack src/Guardrails.Cli -c Release -o nupkg          # nupkg/ is gitignored
  dotnet tool install --global --add-source ./nupkg ServantSoftware.Guardrails --version 1.0.0-preview.1
  guardrails validate examples/hello-guardrails/hello-guardrails   # via the INSTALLED tool
  guardrails plan     examples/hello-guardrails/hello-guardrails
  dotnet tool uninstall -g ServantSoftware.Guardrails
  ```
  (Prerelease → pass `--version 1.0.0-preview.1` or `--prerelease`; use `dotnet tool
  update` if a version is already installed.) Do NOT run the example's prompt tasks via the
  installed tool — no token spend.
- Publish pipeline: `.github/workflows/release.yml` — on a pushed tag `v*`, the 3-OS test
  matrix gates, then `dotnet pack` + `dotnet nuget push` using repo secret `NUGET_API_KEY`
  (must be configured in GitHub repo settings).

## Commands

```powershell
dotnet build "Guardrails.sln" -c Release          # 0 warnings tolerated
dotnet test  "Guardrails.sln" -c Release          # full suite; integration spawns real processes
dotnet run --project src/Guardrails.Cli -- validate <plan-folder>
dotnet run --project src/Guardrails.Cli -- run <plan-folder> --no-ui [--fresh]
dotnet run --project src/Guardrails.Cli -- plan|status|reset <plan-folder>
dotnet pack src/Guardrails.Cli -c Release -o nupkg    # local tool package
```

Smoke test of record: `run examples/hello-guardrails/hello-guardrails --fresh --no-ui`
(spends ~$1 of Claude tokens — prompt tasks; needs `claude` on PATH).

## Conventions & gotchas (hard-won)

- **JSON manifests**: System.Text.Json with `ReadCommentHandling.Skip` +
  `AllowTrailingCommas` — committed examples use `//` comments; don't break this.
- **Diagnostic codes**: GR10xx loading, GR20xx validation (`DiagnosticCodes.cs`);
  tests assert codes; never renumber. GR2009 = prompt-runner `command` not on PATH
  (WARNING, not error). Next free: GR1009 / GR2010.
- **Sorts are ordinal** everywhere (guardrail order, task folders) — locale bugs.
- **Atomic writes** (`AtomicFile`) for anything resume reads (state.json, run.json).
- **Process spawning**: `ArgumentList` only; `Kill(entireProcessTree: true)`;
  interpreter resolution via `InterpreterMap` with injectable `IExecutableProbe`
  (tests use `FakeExecutableProbe`, never the real PATH).
- **Merge-sequence protocol**: `journal.ReserveMergeSequence()` BEFORE
  `stateManager.MergeFragment(...)`; pass the reserved value to `RecordAttempt`.
- **Claude specifics live ONLY in `ClaudePromptRunner`** (flags, stream-json
  parsing). Verdicts come from files, never exit codes.
- **Testing doctrine**: TCS-gated fakes for concurrency (no sleeps); `.ps1` + `.sh`
  fixture flavors OS-picked; plan builders pin `defaultRetries: 0`; prompt-pipeline
  tests use `FakeClaudePlanBuilder` (tokenless); real-claude tests gated behind
  `GUARDRAILS_REAL_CLAUDE=1`; xunit.v3 wants `TestContext.Current.CancellationToken`
  (xUnit1051 is an error).
- **Windows .sh hazard**: bare `bash` can resolve to WSL's `System32\bash.exe` and
  fail on Windows paths (GitHub issue #1). Tests/examples use OS-appropriate
  scripts; `guardrails.json interpreters` is the user escape hatch.

## Dogfooding safety

When the harness executes a plan that builds THIS repo: guardrail scripts must build
from source (`dotnet build`/`dotnet run --project src/Guardrails.Cli`) — **never
invoke the globally installed `guardrails` tool that is executing the plan** (file
locks on its own binaries, and you'd be testing the old build).

## Status pointers

Milestone status lives in `guardrails-domain-knowledge` → Status section. Roadmap +
Reality Gate: `docs/plans/03-roadmap.md`. Schema truth: `docs/plans/02-schemas-and-contracts.md`.
