## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `14-author-tests-boundary-wiring`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "14-author-tests-boundary-wiring": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
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

Author failing tests for the two drain boundaries — design 40 §1's table. There is no stub
file: the production types already exist; what is missing is the CALL, which is exactly what
these tests must prove.

**Test file:** `tests/Guardrails.Integration.Tests/Supply/SuppliedBoundaryWiringTests.cs`
**Test class:** `SuppliedBoundaryWiringTests`

Every test carries `[Trait("Category", "Supply")]`.

**Drive the REAL seam, never an injected fake of it.** Construct the production `Scheduler`
through its real factory and drive `guardrails run` through
`CommandFactory.BuildRootCommand`. A test that injects its own drain and asserts the drain was
called proves the fake was called — that is how a component ships wired to nothing and green.
Assert an effect ONLY the production path emits: the supplied file is PRESENT on the base at the
next task's worktree, and the commit carries the §4 trailer.

**Pin these behaviours to these EXACT method names:**

- `TaskBoundary_DrainsFilesStagedWhileTheRunIsExecuting` — row 1 of §1's table.
- `RunStart_DrainsFilesStagedAfterTheRunHalted` — row 2, **the measured incident**: the task
  settled needs-human and the run EXITED, so there was no live task boundary. This drains before
  the first task is scheduled, which is the cleanest boundary of the two — nothing is branched
  from the base yet.
- `SettledTasksAreNotReRunBySupplying` — §2's closing rule. Supplying does not invalidate
  completed work; a design that re-ran the DAG on every supply would be unusable on a long plan.
- `ARunThatSuppliesNothing_IsUnchanged` — the never-weaker requirement, asserted rather than
  assumed: no commit, no journal section, no observer event.

The tests MUST COMPILE and FAIL against the unwired code. Do NOT wire anything here.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

