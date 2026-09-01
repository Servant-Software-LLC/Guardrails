## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `12-record-the-turn-count`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "12-record-the-turn-count": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements the first item of section 3.4 of `docs/plans/30-telemetry-phase-1.md`:
*turns-used — computed, printed and discarded today*. Read section 3.4, and read section 2, the
survivorship finding that reordered the plan: **every failure carrying no record is exactly how the
corpus came to read 100% first-pass everywhere.** A turn count recorded only on the success path
repeats that mistake with a new datum. Where this prompt and the plan disagree, the plan is
authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

The plan's own citation of `Scheduler.cs:1908` for the turn count was checked and points at the JIT
wave-breakdown gate's turn count — a different number about a different invocation. The number this
task is about dies at `ActionRun.FromPrompt`.

## READ THIS BEFORE YOU NAVIGATE

Every claim below about how the code currently works was read while this prompt was written and is
**authoring-time state to verify, not settled fact**. Tasks 04, 06 and 10 all edit these files before
you run — `ActionRunner.cs` and `TaskExecutor.cs` twice over, `AttemptJournaler.cs` for the bucket — so
**every line number here would be stale on arrival**. Navigate by the greppable markers named in bold;
if a marker no longer matches what this prompt describes, trust the code and say so in your summary.

## Task

Make `11-author-tests-attempt-envelope`'s **`AttemptTurnsTests`** pass. `PromptResult.NumTurns` already
exists and every runner populates it; `ActionRun.Turns` (task 04) and `AttemptRecord.Turns` (task 03)
already exist and nobody populates either. Connect the three.

### 1. `src/Guardrails.Core/Execution/ActionRunner.cs` — stop discarding it

**Grep for `FromPrompt`.** `ActionRun.FromPrompt` restates the `PromptResult` in the shape the attempt
loop consumes; at authoring time it copies `CostUsd`, `Usage` and `ObservedModel` and drops
`NumTurns`. Copy it the way `CostUsd` is copied — a straight member copy, nothing recomputed and
nothing defaulted.

**`ActionRun.FromScript` gets nothing.** A script runs no turns, and `null` is the honest answer;
`0` would CLAIM a model was invoked and took no turns. One of the authored tests asserts exactly this
and is green before you start — its job is to stay green, and a `?? 0` anywhere on this path is the
failure it exists to catch. This is the same null-versus-zero line `TelemetryRow.CostUsd` and
`AttemptRecord.Usage` already draw, and both of their doc-comments state it.

### 2. `src/Guardrails.Core/Execution/AttemptJournaler.cs` — journal it, on every path

The journaller is where an `AttemptRecord` is constructed for serial mode. **Grep for
`new AttemptRecord`** to find every construction site in this file — there are several, one per outcome
— and for the method names that own them: `CompleteSucceededOrInvalidFragment` (the serial success
settle) and `FailedAttempt` (the shared failure recorder that the other outcome methods funnel
through).

The turn count belongs on **the success path and the failure paths alike**. `FailedAttempt` takes its
cost and usage as explicit optional parameters (**grep for `costUsd:`** to see the existing call sites
forwarding them) rather than reading an `ActionRun`; the turn count follows that established
precedent. Widen the parameter list the way `usage` already widened it, defaulting to null, and forward
it from every call site that has a value to give.

Do not invent a new dependency and do not reach for a static: every one of these methods already
receives everything it needs, and the value arrives on the `ActionRun` the caller already holds.

### 3. `src/Guardrails.Core/Execution/TaskExecutor.cs` — forward it at the call sites

The executor is what calls the journaller. **Grep for the journaller method names** to find the call
sites that must now pass the turn count through — including the guardrail-failed path, which is the one
section 2 is about.

### What is NOT in this task

- **The segment durations.** `12a-segment-the-attempt-durations` owns `AttemptRecord.Segments`,
  `ActionMs` and `GuardrailMs`, and it runs after you. Do not add a stopwatch here, do not touch
  `GuardrailRunner.cs` (it is not in your writeScope), and do not populate `Segments`. Your guardrail
  filters on `AttemptTurnsTests` alone precisely so you do not have to.
- **The worktree settle.** `PendingAttempt.Turns` exists (task 04) and
  `16-carry-phase1-facts-through-the-worktree-settle` is the task that sets it and reads it at the
  scheduler's own record construction. Setting it here is not wrong, but the guardrail does not ask for
  it and the later task owns the agreement test — do not go looking for `Scheduler.cs`, which is not in
  your writeScope.

### Do NOT edit the authored tests

`tests/Guardrails.Core.Tests/Execution/AttemptEnvelopeTests.cs` is outside this task's writeScope, and
it also carries `AttemptSegmentsTests`, which is expected to be RED while you work — that is the next
task's red, not a regression you introduced. Make `AttemptTurnsTests` pass by fixing the
implementation. If a test is genuinely wrong or incompatible with the plan, emit
`{"needsHuman": "<why>"}` to the state-out path rather than changing it — an out-of-scope edit fails
the write-scope check and burns a retry.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/ActionRunner.cs`, `src/Guardrails.Core/Execution/TaskExecutor.cs` and
`src/Guardrails.Core/Execution/AttemptJournaler.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside those three paths — including changes to other production
files, the authored test file, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry.
