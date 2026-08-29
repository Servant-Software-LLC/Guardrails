## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "04-wire-verifier-into-preflight": { "someKey": "someValue" } }`. The harness
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

Wire `SampleVerifier` into the **real** pre-DAG plan-preflight phase, so a bad sample pair halts the
run **before any task spends a token** — and prove the wiring with a test that drives the production
entry point rather than injecting the seam itself.

**Write exactly two files:**

1. `src/Guardrails.Cli/PlanPreflightPhase.cs` — the phase verifies every committed sample pair.
2. `tests/Guardrails.Integration.Tests/Samples/SampleVerifierWiringTests.cs` — the composition-root
   test.

**Scope boundary (harness-enforced):** Write only to those two paths. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — including `SampleVerifier.cs`,
`SamplesCommand.cs`, `RunCommand.cs`, `Revalidate.cs`, the SSOT document, or any `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

> **The test lives in `Guardrails.Integration.Tests`, and that is load-bearing.**
> `PlanPreflightPhase` is in the **`Guardrails.Cli`** assembly. `tests/Guardrails.Core.Tests`
> references `Guardrails.Core` **only** and cannot see it, so a wiring test there could not compile a
> reference to the phase — it could only construct `SampleVerifier` by hand, which is the unwired-factory
> failure with extra steps (#120). `tests/Guardrails.Integration.Tests` references both projects and is
> already the home of `PlanPreflightPhaseTests`. Read that file first: it is the house idiom for driving
> this phase and the real `RunCommand` over a temp git repo, and you should copy its fixture helpers'
> shape rather than invent new ones.

### Read the two landed halves FIRST — and treat this section as authoring-time state

`SampleVerifier` (task 02) and the `guardrails samples verify` verb (task 03) are **siblings that ran
before you**. Everything below reflects the state at plan-authoring time, **before either had actually
run** — verify it is still accurate before assuming the same shape applies. `git log --oneline`,
`git show` and a read of `src/Guardrails.Core/Samples/SampleVerifier.cs` and
`src/Guardrails.Cli/Commands/SamplesCommand.cs` are the fastest way to see what actually landed.

- `SampleVerifier` was specified to expose an async entry point over a loaded `PlanDefinition`, a
  `ProcessRunner` and a per-sample timeout, returning a result carrying a list of findings — each with
  a kind, the guardrail path, the sample path and the observed exit code. Its exact signature is
  whatever task 02 shipped.
- `SamplesCommand` shows how the verb assembles that call and renders the findings. **Read it and
  reuse the same call shape** — the point of this task is that the verb and the phase run the SAME
  verifier, not two implementations of one policy.

If a landed shape makes an instruction below impossible as written, implement the **intent** and say
so in your summary — do not reshape a sibling's file to match this prompt (it is out of scope).

### Half A — the phase verifies sample pairs, and WHERE it does so is the whole trap

`PlanPreflightPhase.EvaluateAsync` opens with two short-circuits, and putting the new step after
either one silently disables it for most plans:

```csharp
if (plan.PlanPreflights.Count == 0)          // ← a plan with NO <plan>/preflights/ folder returns TRUE here
{
    return true;
}
...
if (journal.Document.PlanPreflights is { } marker && marker.Status == Passed && marker.PlanHash == currentHash)
{
    return true;                              // ← the B1 resume SKIP
}
```

**Verify the sample pairs BEFORE both of them.** Two reasons, each load-bearing:

- **Most plans declare no `preflights/` folder at all.** Placed after the first early return, sample
  verification would run only for plans that already opted into Full Flight Checks — a gate that
  protects the plans least likely to need it. A pair with reversed polarity would remain
  indistinguishable from a correct one for every other plan in the repo, which is the exact state
  #510 exists to end.
- **The resume SKIP exists for a different kind of check and its reasoning does not transfer.** That
  marker exists because a *negative-baseline* check is true only at the very start of a plan's
  lifecycle, so re-running it against partially-merged bytes would false-halt a healthy run (SSOT §7,
  the B1 fix). Sample verification has no such property: samples are plan INPUTS, not run outputs, so
  re-verifying them mid-run can never false-halt. Skipping them on resume would reintroduce
  "recorded but never executed" through the resume door.

Requirements:

- Run the verification on **every** call, before the two short-circuits above, and return **false**
  when any finding is reported — that boolean is what `RunCommand` already turns into a halt at exit
  code 2, before the Scheduler builds any wave.
- **Keep the existing failure posture and the existing shapes.** Journal the failure using the
  `PlanPreflightsSection` / `PlanPreflightCheck` / `RunHalt` machinery already in this file, adding a
  check entry that NAMES each bad pair, so `state/run.json` explains a halt to a post-mortem reader
  who never saw the console (#432). This is an additive check entry, not a schema change; if you judge
  it otherwise, say so in your summary — the SSOT text for the verb and this phase step lands in this
  plan's terminal documentation task, not here.
- **Say WHY in the operator-facing text.** The harness already lints the guardrail that can never
  PASS (**GR2055**). The dangerous polarity — the guardrail that can never FAIL — has no check, and
  running the `.invalid` half *is* that detector. An operator who understands why the check exists
  will not delete it; one who reads only "sample mismatch" will.
- **It must be CHEAP.** This now runs before every run of every plan (plan of record §7 names that as
  an accepted risk with a condition attached). A plan with no committed pairs must cost a directory
  probe per task and nothing more — no process launches. Only guardrails that actually have a
  committed pair are ever executed, and by doctrine those are source-shape greps.
- **Do NOT add a flag to skip it.** `RunCommand.cs` is outside your write scope, and a skip switch is
  not part of the settled design.
- **Do NOT touch `validate`.** Validate is static and offline, runs in editors and mid-authoring, and
  making it execute arbitrary PowerShell is a semantic change this plan deliberately does not make
  (plan of record §1).

### Half B — the composition-root test: drive the REAL phase, never inject the seam

Write `tests/Guardrails.Integration.Tests/Samples/SampleVerifierWiringTests.cs`, namespace
`Guardrails.Integration.Tests.Samples`, every test carrying `[Trait("Category", "BacklogSlate")]`.
Author exactly these methods, named verbatim:

| Test method name | What it proves |
|---|---|
| `EvaluateAsync_ReturnsFalse_WhenACommittedSamplePairIsReversed` | Call **`PlanPreflightPhase.EvaluateAsync(...)`** on a temp plan whose one committed pair has its `.valid` and `.invalid` halves swapped, and assert it returns **false**. |
| `EvaluateAsync_HaltsOnABadSamplePair_EvenWhenThePlanDeclaresNoPreflightsFolder` | The same, on a plan with **no `<plan>/preflights/` folder at all**. This is the placement trap above, pinned. It is the one test that fails if the new step lands after the first early return — and everything else would still be green. |
| `EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound` | A plan whose pair is correctly two-sided (`.valid` → exit 0, `.invalid` → non-zero) is **not** halted. Without this the phase could return false unconditionally and every other test would still pass. |
| `EvaluateAsync_JournalsTheFailingPair_SoAPostMortemReaderCanSeeWhichPairHalted` | After the failing call, `state/run.json` records the failure and the recorded text NAMES the offending pair. A halt whose only trace is the operator's scrollback is the #432 failure repeating. |
| `Run_HaltsBeforeSchedulingAnyTask_WhenAPlansCommittedSamplePairIsReversed` | Drive the **real CLI run entry** over a temp git repo — the `RunCliAsync` / `CreatePlan` idiom in `PlanPreflightPhaseTests` — and assert the process exits `ExitCodes.TaskFailed` with **zero attempts journaled on every task**. This is the plan's actual Done-when: the run halts *before the DAG*, so no task spends a token. |

**Prohibitions, and they are structurally checked (a guardrail fails the task if you break them):**

- **The test must CALL `PlanPreflightPhase.EvaluateAsync`.** A mention is not a call:
  `nameof(PlanPreflightPhase.EvaluateAsync)`, a comment, or the method's name inside a test name does
  not satisfy it. (Measured on 2026-08-28, issue #521: a composition-root guardrail that required only
  the dotted NAME was satisfied by a hollow test carrying two dead `nameof` references and **zero
  invocations** — exit 0.)
- **The test must NOT construct or call `SampleVerifier` itself.** Writing the plan fixture — the
  `guardrails.json`, the task folder, the guardrail script, the two sample halves — is expected and
  fine. Running the verifier yourself and asserting on its findings is not: that test is green whether
  or not `PlanPreflightPhase` was ever changed, which is the unwired-factory failure with extra steps
  (#120). Let the phase run it; assert on what the phase returned and journaled.

Practical notes:

- Build every fixture in a temp directory and clean up in a `finally` or `IDisposable`. Never write
  into the repository tree, and never point a fixture at a real plan folder.
- Write the fixture guardrail in the shell the host supports — `.ps1` on Windows, `.sh` elsewhere —
  mirroring the `OperatingSystem.IsWindows()` switch already used in `PlanPreflightPhaseTests`.
- Make the fixture guardrail's exit code a function of the subject it is handed, so a pair's polarity
  is a property of the SAMPLES rather than a hard-coded exit line — otherwise the "sound pair" test and
  the "reversed pair" test are the same test twice.
- `EvaluateAsync` takes a `RunJournal` that `RunJournal.LoadOrCreate` produced; follow how
  `PlanPreflightPhaseTests` and `Revalidate.cs` obtain one rather than hand-rolling a journal.
