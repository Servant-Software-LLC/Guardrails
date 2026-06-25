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
  matrix gates, then the publish job packs with the version derived FROM the tag
  (`-p:Version=${GITHUB_REF_NAME#v}`) and pushes via **Trusted Publishing** (OIDC): the job
  has `id-token: write`, `NuGet/login@v1` (input `user: ${{ secrets.NUGET_USER }}`) exchanges
  the OIDC token for a short-lived key, then `dotnet nuget push`. No `NUGET_API_KEY` secret —
  only a non-sensitive `NUGET_USER` (nuget.org profile name) plus a Trusted Publishing policy
  on nuget.org (Repository Owner `Servant-Software-LLC`, Repository `Guardrails`, Workflow
  File `release.yml`).
- **Bundled skills**: the CLI csproj globs three skill folders
  (`plan-breakdown` incl. `references/`, `guardrails-review`, `guardrails-domain-knowledge`)
  as `Content` with `CopyToOutputDirectory=PreserveNewest` and a
  `<Link>skills\<name>\%(RecursiveDir)…</Link>`. They land in the build output under
  `skills/` AND — because the dotnet-tool packer sweeps copy-to-output content — inside the
  nupkg at `tools/net8.0/any/skills/...`, i.e. next to the entry assembly
  (`AppContext.BaseDirectory`) for the installed global tool. **Do NOT add explicit
  `Pack=true`/`PackagePath` on those items** — the packer already includes them, so doing so
  duplicates the path and trips NU5118 (warning-as-error). `guardrails skills install` reads
  from `AppContext.BaseDirectory/skills`. Repo bootstrap: `install.ps1` (root, tested) and
  `install.sh` (root, untested twin) verify dotnet, install/update the tool, then run
  `guardrails skills install`.
- **Skill-version stamping + `--version` drift (#152, `docs/DEPLOYMENT.md` §Skill versioning
  and drift detection)**: each INSTALLED skill folder is stamped with a
  `.guardrails-skill-version` marker (`SkillVersionReport.MarkerFileName`) whose only content is
  the harness version — the normalised `InformationalVersion` from `GuardrailsVersion.Current`
  (= what `guardrails --version` prints). Written by `SkillsInstaller.StampVersion` on every
  INSTALLED skill; a SKIPPED skill keeps its old/absent marker (that staleness is the drift
  signal). The CLI tool version and an installed skill's version are **independent** — updating
  the tool does NOT refresh installed skills, because `skills install` SKIPS an already-present
  folder unless `--force` (the silent-skip trap: a stale `/plan-breakdown` keeps emitting
  legacy output for an older harness with no error — the incident a preview.25 harness produced
  a `captureHashes`/no-`writeScope` folder). `guardrails --version` now surfaces this:
  `VersionWithDriftAction` keeps stdout as the bare version line (scripts parse it, unchanged)
  and writes a drift-warning block to **stderr** — exit code stays 0 — for any known skill
  (set = bundled `AppContext.BaseDirectory/skills`) found under `~/.claude/skills` or
  `./.claude/skills` whose marker is missing (`unversioned`) or `≠` the harness (compared via
  `SkillVersionReport.Build`, `GuardrailsVersion.Normalize` strips `+build`). Remedy it warns:
  `guardrails skills install --force`.

## Commands

```powershell
dotnet build "Guardrails.sln" -c Release          # 0 warnings tolerated
dotnet test  "Guardrails.sln" -c Release          # full suite; integration spawns real processes
dotnet run --project src/Guardrails.Cli -- validate <plan-folder>
dotnet run --project src/Guardrails.Cli -- run <plan-folder> --no-ui [--fresh]
dotnet run --project src/Guardrails.Cli -- plan|status|reset <plan-folder>
dotnet run --project src/Guardrails.Cli -- graph <plan-folder> [--check] [--stdout] [--format mermaid]
#   renders the task/guardrail DAG to <folder>/diagram.md (Mermaid); --check reports staleness
#   via a source-sha256 in the file's provenance comment (SSOT §10); --stdout writes nothing
dotnet run --project src/Guardrails.Cli -- skills install [--project] [--target <dir>] [--force]
#   `--project` → ./.claude/skills (else ~/.claude/skills); `install skills` is a hidden alias
dotnet pack src/Guardrails.Cli -c Release -o nupkg    # local tool package (bundles skills/)
```

Smoke test of record: `run examples/hello-guardrails/hello-guardrails --fresh --no-ui`
(spends ~$1 of Claude tokens — prompt tasks; needs `claude` on PATH).

## Conventions & gotchas (hard-won)

- **JSON manifests**: System.Text.Json with `ReadCommentHandling.Skip` +
  `AllowTrailingCommas` — committed examples use `//` comments; don't break this.
- **Diagnostic codes**: GR10xx loading, GR20xx validation (`DiagnosticCodes.cs`);
  tests assert codes; never renumber. GR2009 = prompt-runner `command` not on PATH
  (WARNING, not error). GR2022 = a guardrail/script-action reading a non-ancestor task's
  state key (#121); GR2023 = a prompt-runner `maxOutputTokens` ≤ 0 (#114). GR2025
  (WARNING) = plan missing/stale a `/guardrails-review` marker (#79, SSOT §13) — surfaced
  at the CLI command layer (`PlanValidator.ReviewMarkerDiagnostic`), NOT inside
  `PlanValidator.Validate`. Next free: GR1010 / GR2024 / GR2026.
- **Sorts are ordinal** everywhere (guardrail order, task folders) — locale bugs.
- **Atomic writes** (`AtomicFile`) for anything resume reads (state.json, run.json).
- **Process spawning**: `ArgumentList` only; `Kill(entireProcessTree: true)`;
  interpreter resolution via `InterpreterMap` with injectable `IExecutableProbe`
  (tests use `FakeExecutableProbe`, never the real PATH). Child streams are pinned
  UTF-8 (no BOM) in `ProcessRunner` (stdout/stderr decode + stdin encode) — never
  rely on `Console.OutputEncoding`, which is the Windows OEM code page and corrupts
  non-ASCII in the logs (#55, SSOT §5.1).
- **Merge-sequence protocol**: `journal.ReserveMergeSequence()` BEFORE
  `stateManager.MergeFragment(...)`; pass the reserved value to `RecordAttempt`.
- **Recorded action outcome → guardrails** (`TaskExecutor`): a guardrail gets the action's
  captured result via `GUARDRAILS_ACTION_RESULT` (`action-result.json` = `{kind, exitCode,
  summary}`), `GUARDRAILS_ACTION_STDOUT`, `GUARDRAILS_ACTION_STDERR` (SSOT §5.1). Doctrine
  is **verify-don't-replay** (#62): a guardrail may verify a postcondition from this recorded
  output instead of re-running the action's command — but it's a speed/flake trade-off, sound
  only against output the action couldn't fabricate (a produced artifact, a runner-written TRX).
  The recorded `exitCode` is ALWAYS 0 at guardrail time (a non-zero action fails the attempt
  first), so never expose a `GUARDRAILS_ACTION_EXIT_CODE` env var — it would be tautological.
- **Claude specifics live ONLY in `Prompts/`** — `ClaudePromptRunner` (flags, invocation),
  `ClaudeStreamParser` (terminal result), and `ClaudeTranscriptRenderer` (the deterministic
  `transcript.md` projection of the raw stream, #27). Verdicts come from files, never exit
  codes. `PromptComposer` injects dependency-context + prior-attempt pointers that reference
  `transcript.md` (#26); it stays PURE (no IO) — `TaskExecutor` resolves paths/existence, and
  the renderer must stay deterministic (golden-file tested).
- **CLI output seam (`IConsoleIo`)**: the CLI writes ALL user-facing output through an
  injected `IConsoleIo` (`Out`/`Error` `TextWriter`s), never the process-global
  `Console.*`. Production wires `SystemConsoleIo.Instance` (the ONLY place that touches
  `Console.Out`/`Console.Error`); `Program.cs` builds the tree via
  `CommandFactory.BuildRootCommand(io)`. Every command factory takes `io`
  (`ValidateCommand.Create(io)`, … `SkillsCommand.Create(io)`) and the helpers take the
  writer (`ConsoleRunObserver(TextWriter)`, `PlanProbe.PrintDiagnostics(diags, TextWriter)`,
  `DryRun.Execute(folder, io)`, `FolderArgument.ResolveAndAnnounce(value, TextWriter)`).
  LEFT on `Console` by design: the Spectre `LiveRunObserver`, the UI-capability probes
  (`Console.IsOutputRedirected`, `AnsiConsole...Interactive`), and `ResetCommand`'s
  confirmation INPUT (`Console.IsInputRedirected`/`ReadLine`). Tests inject a
  `StringWriter`-backed `StringConsoleIo` and capture from its `OutText`/`ErrorText` —
  **no `Console.SetOut`, no global console state, so the CLI-driving test classes are
  parallel-safe** (there is no `ConsoleCaptureCollection`; do not reintroduce one).
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

## Design-of-record → draft-PR review workflow (#106)

A **design-of-record** — a substantial `docs/plans/NN-*.md` architecture doc — goes into a
**draft GitHub PR for inline human review before its implementation milestones begin**. The
loop: author the doc on a branch → `gh pr create --draft` → human comments inline → architect
revises and pushes until addressed → only then does coding (breakdown / harness work) start.
This is the product's own "everything is a reviewable draft a human approves before it runs"
gate applied to the *design* (`docs/plans/01-overview.md` pitches the artifacts as "reviewable
in a PR"; `plan-breakdown` already presents its task folder as a draft).

- **Applies to:** substantial designs-of-record (new-capability architecture, contract changes,
  multi-milestone plans — e.g. the parallel-execution / disjoint-scope plans this loop was
  forged on).
- **Does NOT apply to:** trivial/mechanical changes (typos, one-line clarifications,
  renumbering) — those go straight in.
- **Distinct from** the v2 "CI mode / PR-per-task" roadmap bet #2 (the *harness* emitting a
  check-run/PR per task during a run): that is automation, this is *human design review*.

The `guardrails-architect` operating contract owns the full statement (`.claude/agents/guardrails-architect.md`).

## Status pointers

Milestone status lives in `guardrails-domain-knowledge` → Status section. Roadmap +
Reality Gate: `docs/plans/03-roadmap.md`. Schema truth: `docs/plans/02-schemas-and-contracts.md`.
