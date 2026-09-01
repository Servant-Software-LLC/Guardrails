## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "04-stamp-the-pin-serial-and-revalidate": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements stage 4 of `docs/plans/32-executed-definition-hash.md`. **Read sections 4.2, 4.3,
5.2 and 5.8** (P1, P2, P4, P14). Where this prompt and the plan disagree, the plan is authoritative and
you should say so in your summary.

## Task

Two write sites, two expression-level substitutions. **Zero plumbing** - both members already hold the
`TaskNode` whose hash they stamp (section 5.2), so no parameter is threaded, no field is added to a
handle, and no new object is passed anywhere.

| Site | Member | Change |
|---|---|---|
| **W1** | `AttemptJournaler.CompleteSucceededOrInvalidFragment` (around `AttemptJournaler.cs:91`) | `TaskDefinitionHash.Compute(task)` becomes `task.DefinitionHashAtLoad`, in place |
| **W4** | `TaskExecutor.RevalidateAsync` (around `TaskExecutor.cs:590`) | the same substitution, at the named-argument call site |

Find both by **member name**; the line numbers are an authoring-time snapshot and stage 3 has already
landed in this working tree.

**Stage 3 put `DefinitionHashAtLoad` on `TaskNode` as a nullable, init-only auto-property populated
eagerly by `PlanLoader.LoadTask`.** This describes the state at plan-authoring time, before stage 3 had
actually run - verify it before assuming this shape.

### W4 is a CONSISTENCY pin, not a defect pin - and it is fixed anyway

Section 5.8's P4 says this plainly. `guardrails run --revalidate-task` loads the plan, re-runs the
guardrails and journals a synthetic success in one shot, with **no window in which a human could edit
between load and settle** - so pin and disk agree there today and W4 is a no-op in practice. It is fixed
regardless because *"every write site, one rule"* is the property section 9's guardrails enforce, and an
exception carved out for the site that "cannot" hit the window is how the fifth site gets written the old
way later.

### The one thing that must not happen

**No fallback.** Not `task.DefinitionHashAtLoad ?? TaskDefinitionHash.Compute(task)`, not a null-guard
that recomputes, not a helper that does it one frame away. Section 5.2:

> A null pin records a null hash. There is no fallback to disk, at any write site, ever.

That is the state SSOT section 7.2 already defines and already handles (*"recorded hash absent ⇒ treated
as 'unknown - assume unchanged' → match"*), the same path a pre-#274 journal entry takes; and in
production it is unreachable, because the loader is the only constructor. A `??` here is what section 5.2
calls the **cheapest wrong implementation of this entire plan** - it passes every behavioural pin and
reads like defensive coding. Guardrail 03 exists solely to catch it.

**When you are done, neither file may contain a `TaskDefinitionHash.Compute(` call at all.** Both are
measured at exactly 1 today; both must be 0 afterwards. That is guardrail 03's first clause and section
9's *"and zero in `AttemptJournaler.cs`, `TaskExecutor.cs`"*.

### What turns green

`tests/Guardrails.Core.Tests/Journal/ExecutedDefinitionHashTests.cs` (stage 1) - P1 in serial mode and
P14's between-attempts discriminator go from red to green. P5 and P8 were green before and must stay
green. Do **not** edit that file: it is outside your `writeScope`, and if one of its assertions looks
wrong to you, say so with `needsHuman` rather than changing it.

`tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs` may still be red - that is expected and
correct until stages 5 and 13 land (section 15's filtered-guardrail note). Your guardrail does not run it.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/AttemptJournaler.cs`
and `src/Guardrails.Core/Execution/TaskExecutor.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths - including `TaskNode.cs`, `Scheduler.cs`, any
test file, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you
hit a compile error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
