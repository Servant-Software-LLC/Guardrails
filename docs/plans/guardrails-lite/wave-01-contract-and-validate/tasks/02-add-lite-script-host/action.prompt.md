## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. In this WAVED plan that is the wave-qualified id
  `wave-01-contract-and-validate/02-add-lite-script-host`, NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "wave-01-contract-and-validate/02-add-lite-script-host": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

**Context.** Guardrails Lite (issue #823, `docs/plans/guardrails-lite.charter.md`) is a set of PowerShell 7
kernel scripts under `scripts/lite/` (none exist yet — later tasks write them). Every Lite test class in
this plan drives those scripts through ONE shared helper that you write now. Five later tasks, written by
other agents, compile against the API below **exactly as spelled** — do not rename, reorder parameters or
change return types. Read `docs/plans/guardrails-lite-wave01-design.md`, section "Cross-task APIs", for
the contract; it is restated here.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/Lite/LiteScriptHost.cs` and
`tests/Guardrails.Integration.Tests/Lite/LiteScriptHostTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside these paths — including changes to other test
files, production code, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### 1. `tests/Guardrails.Integration.Tests/Lite/LiteScriptHost.cs`

Namespace `Guardrails.Integration.Tests.Lite`. Public API (exact):

```csharp
public static class LiteScriptHost
{
    public static string RepoRoot { get; }          // see below
    public static bool PwshAvailable { get; }        // true iff a 'pwsh' executable is on PATH
    public static Task<LiteResult> RunAsync(string scriptName, params string[] args);
    public static Task<LiteResult> RunScriptAsync(string scriptPath, params string[] args);
    public static TempPlan CopyFixture(string group, string name);
    public static TempPlan CopyPlan(string repoRelativePlanDir);
}

public sealed class TempPlan : IDisposable
{
    public string Dir { get; }
    public void Dispose();
}

public sealed record LiteResult(int ExitCode, System.Text.Json.JsonElement? Json, string Stdout, string Stderr)
{
    public IReadOnlyList<string> DiagnosticCodes();
}
```

Behaviour:
- **`RepoRoot`** — walk up from `AppContext.BaseDirectory` to the first directory containing
  **`Guardrails.sln`** (the repo root has `Guardrails.sln`; there is NO `.slnx`). Throw
  `InvalidOperationException` naming `Guardrails.sln` if none is found.
- **`PwshAvailable`** — `pwsh` (on Windows `pwsh.exe`) found in any `PATH` directory. Lite requires
  PowerShell 7, so do **NOT** fall back to Windows PowerShell 5.1 the way `TestShell.WindowsShell`
  (`tests/Guardrails.Integration.Tests/TestShell.cs`) does — reuse its PATH-scan idea only.
- **`RunScriptAsync(scriptPath, args)`** — if the file does not exist, throw `FileNotFoundException`
  whose message contains the full path. Otherwise start `pwsh` with `ProcessStartInfo.ArgumentList`
  (never a hand-quoted argument string): `-NoProfile`, `-NonInteractive`, `-ExecutionPolicy`, `Bypass`,
  `-File`, `<scriptPath>`, then each arg. Working directory = `RepoRoot`. Redirect stdout and stderr,
  read BOTH asynchronously (avoid the pipe-buffer deadlock), decode as UTF-8. Timeout 120 s: on timeout
  kill the whole process tree and throw `TimeoutException` naming the script. `Json` = the **last
  non-empty stdout line** parsed with `JsonDocument` and `.RootElement.Clone()`d; if that line is not
  valid JSON, `Json` is `null` (do not throw).
- **`RunAsync(scriptName, args)`** — `scriptName` has no extension; resolves to
  `Path.Combine(RepoRoot, "scripts", "lite", scriptName + ".ps1")` and delegates to `RunScriptAsync`
  (so a missing script throws `FileNotFoundException` naming that `scripts/lite/<name>.ps1` path).
- **`CopyFixture(group, name)`** — source is
  `Path.Combine(RepoRoot, "tests", "Guardrails.Integration.Tests", "Lite", "Fixtures", group, name)`
  (the SOURCE tree, not the build output). Missing → `DirectoryNotFoundException` whose message contains
  that source path. Otherwise copy it recursively into a fresh unique directory under
  `Path.GetTempPath()` and return a `TempPlan` over the copy. Never write into the source.
- **`CopyPlan(repoRelativePlanDir)`** — the same, for any repo-relative plan folder (e.g.
  `examples/hello-guardrails/hello-guardrails`).
- **`TempPlan.Dispose`** — delete the copy recursively after clearing read-only attributes on every
  file (git and some tools mark files read-only on Windows); best-effort, never throws.
- **`LiteResult.DiagnosticCodes()`** — the `code` string of every element of `Json.diagnostics`, in
  order; empty when `Json` is null or has no `diagnostics` array.

### 2. `tests/Guardrails.Integration.Tests/Lite/LiteScriptHostTests.cs`

Class **`LiteScriptHostTests`**, namespace `Guardrails.Integration.Tests.Lite`, `[Trait("Category", "Lite")]`
at class level. Tests that spawn `pwsh` start with
`Assert.SkipUnless(LiteScriptHost.PwshAvailable, "pwsh (PowerShell 7) not found on PATH");`.
Scripts for the `RunScriptAsync` tests are written to a temp dir by the test itself. Write exactly these
`[Fact]` methods — the names are pinned; the guardrail looks each one up in the test results and
requires it **Passed**:

- **`RepoRoot_IsTheDirectoryHoldingGuardrailsSln`** — `Guardrails.sln` and the `src` directory exist
  under `RepoRoot`.
- **`RunScriptAsync_ParsesTheLastNonEmptyStdoutLineAsJson`** — a script writing a noise line, then
  `{"script":"probe","ok":true,"n":3}`, then a blank line; assert `ExitCode == 0` and
  `Json.GetProperty("n").GetInt32() == 3`.
- **`RunScriptAsync_LeavesJsonNullWhenTheLastLineIsNotJson`** — last line `not json`; `Json` is null,
  `Stdout` contains `not json`.
- **`RunScriptAsync_ReportsNonZeroExitCodeAndStderr`** — a script doing
  `[Console]::Error.WriteLine('boom'); exit 7`; assert `ExitCode == 7` and `Stderr` contains `boom`.
- **`RunAsync_MissingScript_ThrowsFileNotFoundNamingTheScriptsLitePath`** —
  `RunAsync("definitely-not-a-lite-script")` throws `FileNotFoundException` whose message contains
  `Path.Combine("scripts", "lite", "definitely-not-a-lite-script.ps1")`.
- **`CopyPlan_CopiesTheExampleIntoAFreshTempDir_AndDisposeDeletesIt`** — copy
  `examples/hello-guardrails/hello-guardrails`; the copy holds `guardrails.json`, is not under
  `RepoRoot`, and after `Dispose()` the directory no longer exists.
- **`CopyFixture_MissingFixture_ThrowsDirectoryNotFoundNamingTheFixturePath`** —
  `CopyFixture("no-such-group", "no-such-fixture")` throws `DirectoryNotFoundException` whose message
  contains `no-such-fixture`.
- **`DiagnosticCodes_ReadsEveryCodeFromTheDiagnosticsArray`** — a `LiteResult` built directly from
  `{"diagnostics":[{"code":"GR1001"},{"code":"GR2090"}]}` returns exactly `["GR1001","GR2090"]`.
- **`DiagnosticCodes_IsEmptyWhenTheJsonHasNoDiagnostics`** — for `{"ok":true}` and for a null `Json`.

**Named exception (GR2075) — say it in your summary.** This task writes test infrastructure and grades
itself with its own self-tests; there is no upstream test-author task, because the helper is the thing
every later test-author task depends on. The compensating controls: the self-tests include failure-path
contrast cases (non-zero exit, non-JSON output, missing script, missing fixture), and the guardrail's
per-test census requires every pinned method to be observed **Passed** — a skipped or renamed test fails.
The real proof comes later: five downstream test classes compile and run through this API.

### Completion criteria (these match the guardrails)
1. `dotnet build tests/Guardrails.Integration.Tests` succeeds.
2. All nine pinned `LiteScriptHostTests` methods run and pass.
