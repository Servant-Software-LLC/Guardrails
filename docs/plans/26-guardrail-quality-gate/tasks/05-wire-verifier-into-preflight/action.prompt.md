## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "05-wire-verifier-into-preflight": { "someKey": "someValue" } }`. The harness
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
run **before any task spends a token**.

**Write exactly one file:**

1. `src/Guardrails.Cli/PlanPreflightPhase.cs`

### The tests already exist, they are RED, and they are NOT yours to edit

Task 04 authored `tests/Guardrails.Integration.Tests/Samples/SampleVerifierWiringTests.cs` and proved,
against the runner's own TRX, that four of its five behaviours **fail** against the phase as it stands
today. Your job is to make them pass by changing `PlanPreflightPhase.cs` — nothing else.

**That test file is outside your write scope and the harness will reject an edit to it.** If a test
looks wrong, do NOT change it: write `{"needsHuman": "<why the test is wrong>"}` to the state-out path
and stop. The five behaviours it pins, and what each demands of your implementation:

| Test method | What your implementation must make true |
|---|---|
| `EvaluateAsync_ReturnsFalse_WhenACommittedSamplePairIsReversed` | A plan with one committed pair whose halves are swapped makes `EvaluateAsync` return **false**. |
| `EvaluateAsync_HaltsOnABadSamplePair_EvenWhenThePlanDeclaresNoPreflightsFolder` | The same holds for a plan with **no `<plan>/preflights/` folder at all**. This is the placement trap below, and it is the one test that fails if you put the step in the wrong place while everything else stays green. |
| `EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound` | A correctly two-sided pair (`.valid` → exit 0, `.invalid` → non-zero) is **not** halted. Without it you could return false unconditionally and everything else would still pass. |
| `EvaluateAsync_JournalsTheFailingPair_SoAPostMortemReaderCanSeeWhichPairHalted` | The run journal records the failure and the recorded text **names** the offending pair. |
| `Run_HaltsBeforeSchedulingAnyTask_WhenAPlansCommittedSamplePairIsReversed` | The real CLI run exits `ExitCodes.TaskFailed` with **zero attempts journaled on every task** — the halt lands before the DAG. |

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/PlanPreflightPhase.cs`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside that one path —
including `SampleVerifierWiringTests.cs`, `SampleVerifier.cs`, `SamplesCommand.cs`, `RunCommand.cs`,
`Revalidate.cs`, the existing `PlanPreflightPhaseTests.cs`, the SSOT document, or any `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

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

### WHERE the step goes is the whole trap

`PlanPreflightPhase.EvaluateAsync` opens with two short-circuits, and putting the new step after
either one silently disables it for most plans. Grep for these markers rather than trusting a line
number, and confirm the shape is still what this describes:

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
  it otherwise, say so in your summary — the SSOT text for the verb and this phase step lands in the
  plan's terminal documentation task (task 06), not here.
- **Say WHY in the operator-facing text.** The harness already lints the guardrail that can never
  PASS (**GR2055**). The dangerous polarity — the guardrail that can never FAIL — has no check, and
  running the `.invalid` half *is* that detector. An operator who understands why the check exists
  will not delete it; one who reads only "sample mismatch" will.
- **It must be CHEAP, and the plan of record states this as a CONDITION, not a preference.** This now
  runs before every run of every plan in the repo — forever — and §7 accepts that cost only on these
  terms: *"a plan that carries no committed sample pairs must cost one directory probe per task and
  zero process launches. The verifier discovers pairs before it runs anything; a plan with nothing to
  verify must pay discovery only."* Discovery first, execution only for guardrails that actually have
  a committed pair. Task 01 pinned the condition as a behaviour
  (`Verify_RunsNoGuardrail_WhenNoTaskCarriesASamplePair`, a fixture whose guardrail writes a marker
  file if it is ever executed), so it is checked rather than merely written down — do not undo it from
  this side by launching anything on the empty path.
- **A plan with no committed pairs must be BYTE-IDENTICAL in behaviour to today.** No new journal
  section, no new console line, no change to the existing `planPreflights` marker or to the resume
  SKIP. `04-preflight-phase-regression.ps1` runs the 12 pre-existing `PlanPreflightPhaseTests` cases
  against your change, and every one of their fixture plans has no `samples/` folder — so a step that
  narrates itself, writes a marker, or launches a process on the empty path will red them. Those tests
  are outside your write scope, so a red there has no in-scope remedy other than making the empty path
  a genuine no-op. Keep the new code additive and silent when there is nothing to verify.
- **Do NOT add a flag to skip it.** `RunCommand.cs` is outside your write scope, and a skip switch is
  not part of the settled design.
- **Do NOT touch `validate`.** Validate is static and offline, runs in editors and mid-authoring, and
  making it execute arbitrary PowerShell is a semantic change this plan deliberately does not make
  (plan of record §3, "NOT in `validate`"; restated in §6 as out of scope).
- **Do NOT change `EvaluateAsync`'s signature.** Its callers — `RunCommand.cs`, `Revalidate.cs`, and
  `PlanPreflightPhaseTests` — are all outside your write scope, so a signature change breaks the build
  in files you cannot fix, and task 04's wiring test was compiled against today's shape.
