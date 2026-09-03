## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `09-fix-worktree-settle-event`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "09-fix-worktree-settle-event": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "09-fix-worktree-settle-event": { "someKey": "someValue" },
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

Fix Bug A: the worktree-mode success path currently emits no `attempt-finished` event at all.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT edit the tests authored upstream.** They are the specification. If one is genuinely wrong,
write `{"needsHuman": "<why>"}` to the state-out path and stop rather than changing it - an
out-of-scope edit to a test file fails the task immediately and consumes a retry.

### The change

In `RecordSucceededSettle`, immediately after `RecordSettleWithAttempt`:

```csharp
_observer.AttemptFinished(task, record);
```

Ordering is already correct - `OnSettledAsync` reaches this before it raises `TaskFinished`, so the
stream reads `attempt-finished` then `task-settled`. Do not reorder anything.

Leave the `PendingAttempt is null` early-return branch (the fake-provider path) raising **nothing**:
there is no record there, and inventing one would put a fact in `events.jsonl` that is not in
`run.json`.

### The comment - the exact wording matters

Comment the raise with the serial-versus-worktree trap the SSOT documents at section 15.2a, and say
that this is **"the worktree SUCCESS path's only route to this event"**.

Do **not** write "the default mode's only route to this event". That is false, and shipping it false
is worse than shipping it uncommented: worktree settles that end `needs-human` - a failed union
re-verify, an unresolvable AI-merge, a non-FF integration failure - call `RecordSettle(..., NeedsHuman,
null)` and build no `AttemptRecord` at all, so they still raise nothing after your change. That
residual is a journal-completeness gap with its own issue, deliberately not fixed here.

### Done when

`WorktreeSucceededAttempt_EmitsAnAttemptFinishedRow` passes, the two control tests in that class still
pass, and nothing else in either suite regresses.
