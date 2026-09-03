## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `08-author-tests-worktree-settle-event`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "08-author-tests-worktree-settle-event": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "08-author-tests-worktree-settle-event": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
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

Produce the **behavioural proof of Bug A**, which does not exist yet.

Bug A is structurally confirmed and behaviourally unproven: `Scheduler.cs` raises `AttemptFinished`
**zero times**, and `AttemptJournaler.ValidateFragmentForSettle` - the worktree-mode SUCCESS path -
returns without raising it. So on that path `attempt-finished` fires for failures and halts only. This
test is what turns that reading into evidence, and it must be RED before task 09 lands.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RunEvents/WorktreeSettleEventTests.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Class name is pinned: **`WorktreeSettleEventTests`**, `[Trait("Category", "RunEvents")]`.

### Two traps that make a test pass for the wrong reason - both are load-bearing

**1. The fake-provider branch.** A test using the fake worktree provider takes
`Scheduler.RecordSucceededSettle`'s `PendingAttempt is null` early return, never reaching the deferred
settle at all. It must go through `ValidateFragmentForSettle` and the **real** deferred settle.

**2. Silent demotion to serial - filed as #596.** A test that merely sets `maxParallelism > 1` may run
**serial anyway** and pass for the wrong reason, because it would then exercise
`CompleteSucceededOrInvalidFragment`, which already raises the event. `SchedulerFactory` spells the
worktree predicate twice, `IsGitRepository` shells out to `git rev-parse` and swallows every failure as
false, and nothing records the answer - so the two evaluations can disagree within one run.

**Therefore the test MUST assert it actually took the deferred-settle path** - that the settle
journalled a `mergeSequence`, or that the worktree provider was genuinely used - **not merely that a
row appeared or did not appear.** Without that assertion this test proves nothing, in either
direction. Verify the path assertion fires by checking it fails when you force the serial path.

### The behaviours - these exact method names

**1. `WorktreeSucceededSettle_TakesTheDeferredSettlePath`**
The path assertion, standing alone so its failure is unambiguous. Drive a real run that genuinely uses
the worktree provider and assert the deferred-settle path was taken. **This test should PASS
immediately** - it asserts existing behaviour and is the control that makes test 2 meaningful. It is a
declared exemption from the red census for that reason.

**2. `WorktreeSucceededAttempt_EmitsAnAttemptFinishedRow`**
On that same real deferred-settle path, assert `events.jsonl` contains an `attempt-finished` row whose
`outcome` is `succeeded`. **This is Bug A and it MUST FAIL** - nothing raises the event on this path
today. Its failure message is the behavioural proof the plan needs; make it say plainly that the
worktree success path emitted no completion event.

**3. `SerialSucceededAttempt_StillEmitsAnAttemptFinishedRow`**
The contrast case. On the serial path the event already fires, so this **PASSES today** and must keep
passing after task 09 - it is what proves the fix added a path rather than moving one. Also a declared
exemption from the red census.

### Done when

The project compiles, test 2 **fails**, and tests 1 and 3 execute and pass. Do NOT touch `Scheduler.cs`
- that is task 09.
