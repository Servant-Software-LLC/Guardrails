## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `12a-segment-the-attempt-durations`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "12a-segment-the-attempt-durations": { "someKey": "someValue" } }`.
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

This task implements the second item of section 3.4 of `docs/plans/30-telemetry-phase-1.md`:
*segmented durations*. Read section 3.4, and read section 2, the survivorship finding that reordered
the plan: **an attempt's cost is evidence whether or not it converged**, so a duration recorded only on
the success path measures only the runs that worked. Where this prompt and the plan disagree, the plan
is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

## READ THIS BEFORE YOU NAVIGATE

Every claim below about how the code currently works was read while this prompt was written and is
**authoring-time state to verify, not settled fact**. `12-record-the-turn-count` edits two of your three
files immediately before you run — `TaskExecutor.cs` and `AttemptJournaler.cs` — and tasks 04, 06 and 10
edited them before that, so **every line number here would be stale on arrival**. Navigate by the
greppable markers named in bold; if a marker no longer matches what this prompt describes, trust the
code and say so in your summary.

## Task

Make `11-author-tests-attempt-envelope`'s **`AttemptSegmentsTests`** pass. `AttemptRecord.Segments` and
the `AttemptSegments` record (task 03) and `GuardrailRunResult.GuardrailMs` and `ActionRun.ActionMs`
(task 04) all exist already, and nothing populates any of them. There is no clock to read them off
either — you are adding the measurement, not plumbing an existing one.

### The measurement does not exist today, and one that looks like it is a trap

`GuardrailRunner` has **no stopwatch at all** — nothing in it times anything. And `ProcessResult` does
carry a `Duration`, but `ActionRun.AsProcessResult` (**grep `AsProcessResult`** in
`src/Guardrails.Core/Execution/ActionRunner.cs`, read-only for you) sets `Duration = TimeSpan.Zero`
for a prompt action deliberately: a prompt action synthesizes its `ProcessResult` for the log
artifacts and has no child-process clock to report. **Prompt wall-time must be measured, not read
back.** Reading `AsProcessResult().Duration` would give you a confident `0` for every prompt attempt,
which is the silent direction.

### 1. The action phase — measure it in `TaskExecutor`

**Grep for `_actionRunner.RunAsync`.** Start a `Stopwatch` immediately before that call and stop it
immediately after; that interval is the action phase. Fold the value onto the returned `ActionRun`
with a `with` expression — the record is immutable and `ActionMs` is an `init` member declared by task
04, so `action = action with { ActionMs = … }` is the in-scope way to carry it. **A `with` whose
result is discarded changes nothing**; the file's existing provenance folds carry that same warning in
their own comments, for the same reason.

`src/Guardrails.Core/Execution/ActionRunner.cs` is **not** in your writeScope. You do not need it: the
carrier member is already declared and the executor can set it.

### 2. The guardrail phase — measure it inside `GuardrailRunner`

**Grep for `new GuardrailRunResult`.** The runner builds its aggregated result at one place; time the
pass it just ran and set `GuardrailMs` there. Measuring inside the runner rather than at a call site is
deliberate: the executor calls `RunAsync` from more than one place (the ordinary attempt pass and the
re-verify path), and a clock at one call site would silently report nothing at the other.

### 3. Journal both, on every path

**Grep for `new AttemptRecord`** in `src/Guardrails.Core/Execution/AttemptJournaler.cs` to find every
construction site, and for the method names that own them: `CompleteSucceededOrInvalidFragment` (the
serial success settle) and `FailedAttempt` (the shared failure recorder the other outcome methods funnel
through). Build one `AttemptSegments` and set it on the record.

`FailedAttempt` takes its cost and usage as explicit optional parameters (**grep for `costUsd:`** to
see the existing call sites forwarding them) rather than reading an `ActionRun`; the segments follow
that established precedent, and `12-record-the-turn-count` will have just widened the same list the
same way — extend that pattern rather than inventing a second one. Then forward the values from the
`TaskExecutor` call sites, **including the guardrail-failed path**: that is the path section 2 is
about, and an attempt that burned twenty minutes before going red is exactly the evidence the corpus is
missing.

### Absent, never a zeroed record

An `AttemptSegments` whose members are both null is a CLAIM that a measurement was taken and came back
empty. Follow the discipline `ActionRun.Usage`'s comment already states — *absent stays absent, never a
zeroed record* — and leave `Segments` null when there is nothing to record. Do not emit `0` for a phase
that did not run.

### What is NOT in this task

- **The turn count.** `12-record-the-turn-count` owns `AttemptRecord.Turns` and has already landed when
  you run. Do not re-do it, and do not "tidy" it.
- **The worktree settle.** `PendingAttempt.Segments` exists (task 04) and
  `16-carry-phase1-facts-through-the-worktree-settle` is the task that sets it and reads it at the
  scheduler's own record construction. `Scheduler.cs` is not in your writeScope.

### Do NOT edit the authored tests

`tests/Guardrails.Core.Tests/Execution/AttemptEnvelopeTests.cs` is outside this task's writeScope. Make
`AttemptSegmentsTests` pass by fixing the implementation. Its duration assertions are deliberately
lower bounds — if one is failing, a phase is going unmeasured or the two clocks are the same clock
copied twice, not "the box was slow". If a test is genuinely wrong or incompatible with the plan, emit
`{"needsHuman": "<why>"}` to the state-out path rather than changing it — an out-of-scope edit fails
the write-scope check and burns a retry.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/GuardrailRunner.cs`, `src/Guardrails.Core/Execution/TaskExecutor.cs` and
`src/Guardrails.Core/Execution/AttemptJournaler.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside those three paths — including changes to other production
files, the authored test file, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry.
