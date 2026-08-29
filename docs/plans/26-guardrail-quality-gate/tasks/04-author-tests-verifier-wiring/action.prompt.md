## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "04-author-tests-verifier-wiring": { "someKey": "someValue" } }`. The harness
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

Author the **composition-root wiring tests** for the sample-pair step of the pre-DAG plan-preflight
phase — and nothing else. The wiring itself is task 05; you write the tests it has to satisfy, and
they must be **RED against the phase as it stands today**.

**Write exactly one file:**

1. `tests/Guardrails.Integration.Tests/Samples/SampleVerifierWiringTests.cs`

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/Samples/SampleVerifierWiringTests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside that one path — including
`src/Guardrails.Cli/PlanPreflightPhase.cs`, `SampleVerifier.cs`, `SamplesCommand.cs`, the existing
`PlanPreflightPhaseTests.cs`, the SSOT document, or any `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

**Do NOT implement the wiring.** `PlanPreflightPhase.cs` belongs to task 05 and is outside your write
scope. Your job is to leave behind a test file that **compiles** and **fails for the right reason**.

> **The test lives in `Guardrails.Integration.Tests`, and that is load-bearing.**
> `PlanPreflightPhase` is in the **`Guardrails.Cli`** assembly. `tests/Guardrails.Core.Tests`
> references `Guardrails.Core` **only** and cannot see it, so a wiring test there could not compile a
> reference to the phase — it could only construct `SampleVerifier` by hand, which is the
> unwired-factory failure with extra steps (#120). `tests/Guardrails.Integration.Tests` references
> both projects and is already the home of `PlanPreflightPhaseTests`. **Read that file first**: it is
> the house idiom for driving this phase and the real CLI over a temp git repo. Copy the shape of its
> `TempGitRepo`, `CreatePlan`, `WriteScript`, `RunCliAsync` and `ReadJournal` helpers rather than
> inventing new ones — they are `private` to that class, so you will be writing your own copies; that
> is expected and stays inside your one allowed path.

### What the tests are about

`tasks/<id>/samples/` two-sided pairs are the strongest anti-tautology device the plan-breakdown
skill has, and today they are **a claim recorded in a folder that is never executed**. The contract a
pair asserts is exactly two facts:

```
tasks/<id>/samples/NN-check.valid.<ext>     ->  tasks/<id>/guardrails/NN-check.ps1 must exit 0
tasks/<id>/samples/NN-check.invalid.<ext>   ->  the same guardrail must exit NON-ZERO
```

Running the `.invalid` half **is** the detector for the guardrail that can never FAIL. The harness
already lints the guardrail that can never PASS (GR2055); the dangerous polarity has no check at all.
Task 02 built the verifier, task 03 exposed it as `guardrails samples verify`, and task 05 will wire
it into the pre-DAG phase so a bad pair halts a run **before any task spends a token**. These tests
are what makes that wiring provable.

### Where the trap is — treat this as authoring-time state, not settled fact

`PlanPreflightPhase.EvaluateAsync` opens with two short-circuits. This reflects the file at
plan-authoring time; **grep for these markers and confirm they are still there before relying on the
shape** — do not trust a line number, and do not assume task 05 has not already reshaped the method.

- **`if (plan.PlanPreflights.Count == 0)`** — a plan with **no `<plan>/preflights/` folder** returns
  `true` here. That is most plans in the repo.
- the **resume SKIP**, keyed on `journal.Document.PlanPreflights` carrying a passed marker whose
  `PlanHash` matches the current hash.

A sample-verification step placed *after* the first short-circuit silently protects only the plans
that already opted into Full Flight Checks. **One of your five tests exists solely to pin that**, and
it is the one test that would still fail if task 05 got the placement wrong while everything else went
green.

### The five test methods — named VERBATIM

Class name **`SampleVerifierWiringTests`**, namespace `Guardrails.Integration.Tests.Samples`, and
**every test carries `[Trait("Category", "BacklogSlate")]`**. Both are load-bearing: this task pair's
guardrails filter on `Category=BacklogSlate&FullyQualifiedName~SampleVerifierWiringTests`, and the
plan's baseline preflights exclude that trait so your deliberately-red tests can never be mistaken
for pre-existing breakage.

| Test method name | What it proves | Against TODAY's unwired phase |
|---|---|---|
| `EvaluateAsync_ReturnsFalse_WhenACommittedSamplePairIsReversed` | Call **`PlanPreflightPhase.EvaluateAsync(...)`** on a temp plan whose one committed pair has its `.valid` and `.invalid` halves swapped, and assert it returns **false**. | **FAILS** — the phase evaluates the (green) preflights and returns true. |
| `EvaluateAsync_HaltsOnABadSamplePair_EvenWhenThePlanDeclaresNoPreflightsFolder` | The same, on a plan with **no `<plan>/preflights/` folder at all**. This is the placement trap, pinned. | **FAILS** — the first short-circuit returns true. |
| `EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound` | A plan whose pair is correctly two-sided (`.valid` → exit 0, `.invalid` → non-zero) is **not** halted. Without it, task 05 could return false unconditionally and every other test would still pass. | **PASSES** — see the note below. This is expected and correct. |
| `EvaluateAsync_JournalsTheFailingPair_SoAPostMortemReaderCanSeeWhichPairHalted` | After the failing call, the run journal records the failure and the recorded text NAMES the offending pair. A halt whose only trace is the operator's scrollback is the #432 failure repeating. | **FAILS** — nothing is journaled about samples. |
| `Run_HaltsBeforeSchedulingAnyTask_WhenAPlansCommittedSamplePairIsReversed` | Drive the **real CLI run entry** over a temp git repo — the `RunCliAsync` / `CreatePlan` idiom in `PlanPreflightPhaseTests` — and assert the process exits `ExitCodes.TaskFailed` with **zero attempts journaled on every task**. This is the plan's actual Done-when: the run halts *before the DAG*, so no task spends a token. | **FAILS** — the DAG runs to completion. |

### FOUR of the five must FAIL. The fifth must PASS. This is the guardrail.

`02-tests-fail-on-unwired-phase.ps1` reads the runner's own TRX and requires each of the **four**
rows marked FAILS above to be observed **`Failed`**. A test named for a behaviour whose body is a
tautology — `Assert.True(true)`, an `Assert.NotNull` on a value the test itself constructed, any
assertion that never invokes the phase — **passes** against the unwired phase and is therefore
rejected by name. That is the whole point of this task existing separately from task 05.

`EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound` is the **declared exception**, and the reason
is structural rather than a concession: on a plan with no `preflights/` folder the unwired phase
returns `true` at its first line, so this test *legitimately* passes today and will *still* pass once
task 05 lands. Demanding it be red would demand that a correct implementation fail. The census
therefore requires only that it **exists and executes** (a `[Fact(Skip=…)]` does not count). Write it
honestly anyway: it is the only thing standing between task 05 and a phase that returns `false`
unconditionally, and task 05's own forward census requires it to be **`Passed`** after the wiring
lands.

**So: do not try to make all five red, and do not weaken the sound-pair test to make it red.**

### Prohibitions — structurally checked; a guardrail fails the task if you break them

- **Every test that names the phase must CALL `PlanPreflightPhase.EvaluateAsync`.** A mention is not
  a call: `nameof(PlanPreflightPhase.EvaluateAsync)`, a comment, or the method's name inside a test
  name does not satisfy it. (Measured 2026-08-28, issue #521: a composition-root guardrail that
  required only the dotted NAME was satisfied by a hollow test carrying two dead `nameof` references
  and **zero invocations** — exit 0.)
- **The test must NOT construct or call `SampleVerifier` itself.** Writing the plan fixture — the
  `guardrails.json`, the task folder, the guardrail script, the two sample halves — is expected and
  fine. Running the verifier yourself and asserting on its findings is not: that test is green
  whether or not `PlanPreflightPhase` was ever changed, which is the unwired-factory failure with
  extra steps (#120). Let the phase run it; assert on what `EvaluateAsync` returned and journaled.
  It would also break the red census: `SampleVerifier` is already implemented by task 02, so a test
  asserting on *its* findings goes **green** today and the census reports it as not-Failed.

### Practical notes

- Build every fixture in a temp directory and clean up in a `finally` or `IDisposable`. Never write
  into the repository tree, and never point a fixture at a real plan folder.
- Write the fixture guardrail in the shell the host supports — `.ps1` on Windows, `.sh` elsewhere —
  mirroring the `OperatingSystem.IsWindows()` switch already used in `PlanPreflightPhaseTests`.
- Make the fixture guardrail's exit code a function of the subject it is handed, so a pair's polarity
  is a property of the SAMPLES rather than a hard-coded exit line — otherwise the "sound pair" test
  and the "reversed pair" test are the same test twice.
- A guardrail script is pointed at a sample by **two** conventions, both live in the committed corpus:
  the sample path as the run's **first positional argument**, and as the **`GR_SUBJECT`** environment
  variable. Task 02's verifier supplies both on every run. A fixture guardrail that reads either one
  is therefore driven correctly; one that reads neither ignores the sample entirely and both halves
  observe the same exit code.
- `EvaluateAsync` takes a `RunJournal` that `RunJournal.LoadOrCreate` produced, and a
  `ProcessRunner`; follow how `PlanPreflightPhaseTests` and `src/Guardrails.Cli/Revalidate.cs` obtain
  and pass them rather than hand-rolling a journal. Read the real signature — it has an optional
  trailing parameter, and the exact shape is whatever is in the file today.
- **`xUnit1051` is an ERROR in this repo, and it will bite you in the CLI test.** MEASURED
  2026-08-29: calling `.InvokeAsync()` (or any method with a `CancellationToken` parameter left at its
  default) **inside a `[Fact]` body** fails the build with
  *"Calls to methods which accept CancellationToken should use TestContext.Current.CancellationToken"*.
  `PlanPreflightPhaseTests` sidesteps it by putting the invocation in a private `RunCliAsync` helper —
  the analyzer only inspects test-method bodies. Do the same, or pass an explicit token
  (`CancellationToken.None` on `EvaluateAsync` compiles fine, and that is what the existing callers do).
- **The tests MUST COMPILE.** A test file that does not compile exits `dotnet test` non-zero in
  exactly the same way a failing one does, and task 05 cannot fix it — the file is outside its write
  scope. `01-build-passes.ps1` is what separates the two, and it runs first.
- Use the BCL and the project's existing test packages only; add no package reference (the `.csproj`
  is out of scope). Build policy is centralised in `Directory.Build.props`.
