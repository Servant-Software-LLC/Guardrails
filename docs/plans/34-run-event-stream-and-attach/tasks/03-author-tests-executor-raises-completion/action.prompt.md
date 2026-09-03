## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `03-author-tests-executor-raises-completion`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "03-author-tests-executor-raises-completion": { "someKey": "someValue" } }`.
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

## Task

The seam exists (task 01) but nothing raises it. `TaskExecutor` already KNOWS each attempt's outcome: it
builds an `AttemptRecord` carrying `Outcome = AttemptOutcome.<value>` on every completion path (see
`src/Guardrails.Core/Execution/TaskExecutor.cs` - grep for `Outcome = AttemptOutcome` rather than relying
on a line number, which moves). This is a plumbing gap, not a knowledge gap.

Create `tests/Guardrails.Core.Tests/RunEvents/TaskExecutorAttemptCompletionTests.cs`, class
**`TaskExecutorAttemptCompletionTests`**, every test carrying `[Trait("Category", "RunEvents")]`.

Pin these test METHOD names:

- `FailedAttempt_RaisesAttemptFinished_WithGuardrailFailedOutcome`
- `MaxTurnsAttempt_RaisesAttemptFinished_WithMaxTurnsOutcome` - the distinction the whole stream exists
  for: `MaxTurns` means the harness already escalated the budget (let it run), `GuardrailFailed` means
  stop and fix, and they demand opposite responses
- `SucceededAttempt_RaisesAttemptFinished_WithSucceededOutcome`
- `RetriedTask_RaisesAttemptFinished_OncePerAttempt` - a task that fails then succeeds raises the member
  twice, with the attempt numbers in order

Drive the REAL `TaskExecutor` and observe through a recording `IRunObserver`; do NOT assert by reading
the journal (that would prove the journal, not the seam). These MUST COMPILE and FAIL - the executor does
not raise the member yet. Do NOT edit `TaskExecutor.cs`; that is task 04.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/RunEvents/TaskExecutorAttemptCompletionTests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
