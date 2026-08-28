## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "04-implement-declared-count-gate": { "someKey": "someValue" } }`. The harness
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

Implement `DeclaredCountGate` so the tests task 03 authored **pass**.

**Write exactly one file:** `src/Guardrails.Core/Breakdown/DeclaredCountGate.cs` — replacing the
`NotImplementedException` stubs with real logic.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Breakdown/DeclaredCountGate.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including
`tests/Guardrails.Core.Tests/PlanSource/DeclaredCountGateTests.cs`, any other production file, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. **The test file is
NOT yours to edit.** If a test looks wrong, it is still the contract: implement to it. If you hit a
compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Read the tests FIRST — they are the specification

Task 03 authored `tests/Guardrails.Core.Tests/PlanSource/DeclaredCountGateTests.cs` and the stub file
you are about to fill. **This paragraph describes the authoring-time state, before task 03 had
actually run — verify it is still accurate before assuming the same shape applies.** Task 03 was told
to choose the member shape it thought the implementation wanted (something like a static
`Evaluate(int declaredDelegatedDecisions, string planFolder)` returning a small result exposing
pass/fail, the declared count, the recorded count and a failure message), so the exact signatures are
whatever landed on disk. Read both files before writing a line; `git show` will show you what task 03
committed.

You MAY reshape the stub's members (that file is in your write scope) — but only in ways the
**existing tests still compile and pass against**. Changing the stub to dodge a test is the one thing
this task cannot do.

### The rule

> The harness read a plan declaring **N** delegated decisions. The folder records **M**. If **N >= 1**
> and **M != N**, fail the breakdown.

- **N** arrives as an input (`int`). This gate never re-derives it from the plan markdown — that is
  the plan-source record's job, and two readers that could disagree about what they read is exactly
  what the design set out to avoid (`docs/plans/24-plan-source-provenance.md` section 1).
- **M** is the number of `## DECISION` sections in `<planFolder>/decisions.md`, and **0** when the
  file is absent. Do not treat an absent file as "unknown" or as a pass: a breakdown that never ran
  the delegated-decision scan produces no `decisions.md`, and catching that is the entire reason this
  gate exists.
- `N == 0` passes unconditionally. Charter emits the count line whenever the count is >= 1 and never
  when it is 0, so a zero is "no claim made", not "a claim of zero".

### The failure message must carry both limits — it is a deliverable, not decoration

The message must name **N**, name **M**, and state the two limits rather than leaving them to be
discovered by whoever hits the failure at 2am:

1. It proves the **count**. It says nothing about whether a decision was made **well**.
2. It depends on Charter's count-line guarantee. Markers present with **no** count line is a **Charter
   bug to file there**, not a defect in this plan — so the message must not send the reader off to
   edit their plan.

Task 03's tests assert on substrings of this message. Read them and satisfy what they actually pin.

### Two things this task must NOT do

- **Do NOT wire anything.** This task implements the type only. `BreakdownCommand` calling the gate is
  task 05's work and is outside your write scope.
- **Do NOT weaken a test to make it pass**, and do not add `[Fact(Skip=…)]` anywhere — the test file
  is out of scope, so any such edit fails the write-scope check immediately.

Use only the BCL; add no package reference (the `.csproj` is out of scope).
