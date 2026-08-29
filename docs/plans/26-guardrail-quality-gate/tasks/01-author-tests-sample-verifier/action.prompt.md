## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "01-author-tests-sample-verifier": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
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

Author **failing xUnit tests** for a new `SampleVerifier`, plus the **minimal stubs** those tests
compile against.

**Write exactly two files:**

1. `tests/Guardrails.Core.Tests/Samples/SampleVerifierTests.cs` — the test file. The test class
   MUST be named **`SampleVerifierTests`** and every test MUST carry
   `[Trait("Category", "BacklogSlate")]`. Both are load-bearing: this task pair's guardrails filter
   on the class name, and the plan's baseline preflight excludes that trait.
2. `src/Guardrails.Core/Samples/SampleVerifier.cs` — minimal skeleton stubs ONLY, whose members
   throw `NotImplementedException`, so the test project COMPILES.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Samples/SampleVerifierTests.cs` and
`src/Guardrails.Core/Samples/SampleVerifier.cs` (the stub file). After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths — including changes to other
production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

**The tests MUST COMPILE and FAIL against the stubs.** Failing is intentional; NOT compiling is a
mistake to fix. Do NOT implement the behaviour — write the tests and only the minimal throwing stubs.

### What `SampleVerifier` is for — read this before you design the API

`grep -rn "samples" --include=*.cs src/` returns **nothing**. The `tasks/<id>/samples/` two-sided pair
is the strongest anti-tautology device the plan-breakdown skill has, and today it is **a claim recorded
in a folder that is never executed**. A pair can ship with reversed polarity, with an `.invalid` half
the guardrail happily passes, or stale after the script was edited — and every one of those is
indistinguishable from a correct pair by inspection, which is the only inspection that happens.

The contract a pair asserts is exactly two facts:

```
tasks/<id>/samples/NN-check.valid.<ext>     ->  tasks/<id>/guardrails/NN-check.ps1 must exit 0
tasks/<id>/samples/NN-check.invalid.<ext>   ->  the same guardrail must exit NON-ZERO
```

Running the `.invalid` half **is** the detector for the guardrail that can never FAIL. The harness
already lints the guardrail that can never PASS (GR2055); the dangerous polarity has no check at all.
Keep that sentence in mind when you word the assertions on the failure messages — an operator who
understands why the check exists will not delete it.

### The binding problem, MEASURED — the sample must reach the guardrail two ways

A guardrail script is written to scan a real repo file by default. To point it at a sample instead, the
committed corpus uses **two different conventions**, and both are in the tree today:

| convention | example | how the sample binds |
|---|---|---|
| `param([string]$SubjectPath = '<real path>')` | `docs/plans/model-tiering-stage-3/wave-01-config-net/tasks/01-allocate-diagnostic-codes/guardrails/02-codes-allocated.ps1` | the **first positional argument** |
| `$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { '<real path>' }` | `docs/plans/24-plan-source-provenance/tasks/05-wire-recorder-into-breakdown/guardrails/01-wiring-test-drives-the-real-seam.ps1` | the **`GR_SUBJECT` environment variable** |

**Measured on this tree, 2026-08-29** — the `param`-style guardrail above, run against its own
committed pair:

| how the sample was supplied | `.valid` | `.invalid` |
|---|---|---|
| first positional argument | exit **0** | exit **1** — the pair proves what it claims |
| `GR_SUBJECT` only | exit **0** | exit **0** — the guardrail never saw the sample and scanned the real tree instead |

So the verifier MUST supply the sample **both ways on every run**: the absolute sample path as the
run's first positional argument **and** as `GR_SUBJECT`. A guardrail that honours neither ignores the
sample entirely — and then **both halves observe the same exit code**, which is the tell. That case is
not a separate finding class (it always surfaces as `.invalid` passed, or as `.valid` failed); it is a
hypothesis the finding's MESSAGE must name, because "the guardrail ignored the sample" and "the
guardrail has the wrong polarity" are repaired differently.

### The API you are designing

You are authoring the contract, so choose the shape you think the implementation wants. A shape that
satisfies every downstream consumer:

```csharp
namespace Guardrails.Core.Samples;

public enum SampleFindingKind { MissingHalf, OrphanSample, ValidHalfFailed, InvalidHalfPassed, ReversedPolarity, Unverifiable }

public sealed record SampleFinding
{
    public required SampleFindingKind Kind { get; init; }
    public string? GuardrailPath { get; init; }   // null only for OrphanSample
    public required string SamplePath { get; init; }
    public int? ObservedExitCode { get; init; }
    public required string Message { get; init; }
}

public sealed record SampleVerifyResult
{
    public required IReadOnlyList<SampleFinding> Findings { get; init; }
    public required int PairsVerified { get; init; }
    public bool Passed => Findings.Count == 0;
}

public static class SampleVerifier
{
    public static Task<SampleVerifyResult> VerifyAsync(
        PlanDefinition plan, ProcessRunner processRunner, TimeSpan perSampleTimeout, CancellationToken cancellationToken);
}
```

**Take a loaded `PlanDefinition`, not a folder path.** It already carries everything discovery needs —
`TaskNode.Directory` (the task folder), `TaskNode.Guardrails` with each `GuardrailDefinition.Name`
(the file's basename without extension), `.Path` (absolute) and `.Kind` — so the verifier never
re-implements guardrail discovery, and both downstream callers (task 03's `guardrails samples verify`
verb and task 05's preflight step) already hold one. Run each guardrail through `ProcessRunner` with
the interpreter resolved by `InterpreterMap` (that is what `ScriptUnitRunner` already does), with the
**working directory set to `plan.Workspace`** so a guardrail's own default subject path still resolves
— which is precisely what makes the "ignored the sample" case observable rather than a crash.

Discovery rule: every directory named `samples` that is a SIBLING of a `guardrails` directory — i.e.
`<task.Directory>/samples/` for each task. The settled home is `tasks/<id>/samples/`; that is what a
`TaskNode` walk finds in both flat and waved plans.

### The behaviours to encode, each bound to a PINNED test method name

Author exactly these test methods, named verbatim — the red census greps for these names:

| Test method name | Behaviour |
|---|---|
| `Verify_ReportsNothing_WhenTheValidHalfExitsZeroAndTheInvalidHalfExitsNonZero` | The happy pair. A guardrail that exits 0 on `.valid` and non-zero on `.invalid` yields **zero findings**, and `PairsVerified` counts it. |
| `Verify_ReportsInvalidHalfPassed_WhenTheInvalidSampleExitsZero` | The can-never-fail detector: the guardrail exits **0** against the half that carries the defect. One finding, `Kind = InvalidHalfPassed`. |
| `Verify_ReportsValidHalfFailed_WhenTheValidSampleExitsNonZero` | The false-red: the guardrail rejects a representative CORRECT artifact. One finding, `Kind = ValidHalfFailed`. |
| `Verify_ReportsReversedPolarity_AsASingleFinding_WhenBothHalvesAreInverted` | `.valid` → non-zero AND `.invalid` → 0 is **ONE** finding (`Kind = ReversedPolarity`), not two. Assert the count is 1 — two findings for one cause is noise an operator learns to skim. |
| `Verify_ReportsMissingHalf_WhenOnlyOneSideOfThePairIsCommitted` | A `NN-check.valid.cs` with no `NN-check.invalid.cs` (and the mirror) yields `Kind = MissingHalf`. A one-sided pair certifies nothing. |
| `Verify_ReportsOrphanSample_WhenNoGuardrailMatchesTheSampleBaseName` | `07-renamed.valid.cs` in a task whose `guardrails/` holds no `07-renamed.*` yields `Kind = OrphanSample` — the STALE pair, left behind when the script was renamed or deleted. |
| `Verify_BindsTheSample_AsTheGuardrailsFirstPositionalArgument` | A fixture guardrail that reads **only** its first positional argument (`param([string]$SubjectPath = '<a path that does not exist>')`) is correctly driven by both halves. Prove it discriminates: this pair must produce **no** finding. |
| `Verify_BindsTheSample_AsTheGrSubjectEnvironmentVariable` | A fixture guardrail that reads **only** `$env:GR_SUBJECT` is likewise correctly driven, and produces no finding. Two conventions, one verifier — a version that supplies only one of them fails exactly one of these two tests. |
| `Verify_EveryFinding_NamesTheGuardrailPath_TheSamplePath_AndTheObservedExitCode` | Every finding an operator can act on: assert the `Message` (or the equivalent fields) contains the guardrail path, the sample path, and the observed exit code. A finding that says "a pair is wrong" and not WHICH is an unactionable report. |
| `Verify_IgnoresSamplesFolderFilesThatAreNotAValidOrInvalidHalf` | A `samples/` folder holding `README.md` and `01-thing.probe.ps1` (both real, in the committed corpus) yields no finding from those files. Only `*.valid.<ext>` / `*.invalid.<ext>` participate. |
| `Verify_ReportsUnverifiablePair_WhenTheMatchedGuardrailIsAPromptJudge` | A pair whose matched guardrail is `ActionKind.Prompt` cannot be executed deterministically. Report it (`Kind = Unverifiable`) — never skip it silently. A pair we cannot execute is the same "recorded but never run" failure this whole feature exists to end, wearing a different hat. |
| `Verify_RunsNoGuardrail_WhenNoTaskCarriesASamplePair` | **The permanent-tax condition — read the paragraph below before writing this one.** A fixture plan carrying **no** `samples/` folder anywhere must cost discovery only: the verifier launches **no** process at all. Build the fixture's guardrail script so it **writes a marker file if it is ever executed**, run the verifier, and assert (a) the marker is **ABSENT** and (b) `PairsVerified` is **0**. |

**Why that last one exists, and why the marker file rather than the count.** This is the condition
§7 of the plan of record attaches to the whole feature. Once the preflight step lands, this code runs
before **every run of every plan in this repo, forever** — so a verifier that launches the interpreter
once per guardrail regardless of whether a pair exists would pass every other guardrail in this plan,
slow every future run, and be attributed to nobody. The cost would land on plans that never opted in,
long after this plan is forgotten. §7 states the condition in one line: *a plan that carries no
committed sample pairs must cost one directory probe per task and zero process launches* — the
verifier discovers pairs **before** it runs anything.

The **absence of a side effect** is the assertion, and the count is not a substitute: a verifier that
launches the guardrail and then discards the result still reports `PairsVerified = 0`, so a count-only
test greens the exact defect. Only the marker file distinguishes "never ran it" from "ran it and
ignored the answer". And the fixture must genuinely carry **no** `samples/` directory anywhere — a
fixture that accidentally has one makes the whole behaviour vacuous, so **assert that by construction**
(enumerate the fixture tree and assert no directory named `samples` exists) rather than assuming the
builder left it out.

### How to build the fixtures — real folders, real scripts, real processes

This type's entire subject is **process exit codes**, so a fixture that fakes the process proves
nothing. Build each fixture as a real plan folder in a temp directory and load it with the real
`PlanLoader`, exactly as `tests/Guardrails.Core.Tests/FourFolderLoaderTests.cs` does (read it first —
it is the house idiom, including the `.git` marker directory the workspace check wants). Then hand the
loaded `PlanDefinition` to `SampleVerifier`.

- Write the fixture guardrail in the shell the host supports — `.ps1` on Windows, `.sh` elsewhere —
  mirroring the `OperatingSystem.IsWindows()` switch in
  `tests/Guardrails.Integration.Tests/PlanPreflightPhaseTests.cs`. `InterpreterMap` resolves both.
- Make each fixture guardrail's exit code a function of the sample it is handed (e.g. exit non-zero
  when the subject's text contains a marker word), so a pair's polarity is a property of the SAMPLES,
  not of a hard-coded exit line. A guardrail that ignores its argument and always exits 0 is the
  fixture for `Verify_ReportsInvalidHalfPassed_…`, and that is the only place it belongs.
- Build every fixture in a temp directory and clean up in a `finally` or `IDisposable`. Never write
  into the repository tree.

### The stub file

`SampleVerifier.cs` needs only enough shape for the tests to compile — the enum, the finding/result
records, and the entry point whose body is `throw new NotImplementedException();`. Keep it minimal and
do NOT implement it. The implementation is task 02; the CLI verb is task 03; the wiring tests are
task 04 and the preflight wiring itself is task 05. None of those files are yours.

Use the BCL only; add no package reference (the `.csproj` is out of scope). Match the surrounding
house style — build policy is centralised in `Directory.Build.props`, so nullable and implicit-usings
settings are already decided for you.
