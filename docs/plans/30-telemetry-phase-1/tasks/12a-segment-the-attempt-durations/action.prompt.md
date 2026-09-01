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

**Count the recorders yourself before you edit anything.** **Grep for `new AttemptRecord` in
`src/Guardrails.Core/Execution/AttemptJournaler.cs`.** At authoring time it returned **nine hits, in
nine different methods** — there is no shared recorder in this file, and nothing funnels. Each method
builds its own `AttemptRecord` and is called **directly** from `TaskExecutor`. If your grep returns a
different number, trust the grep, cover what it found, and say so in your summary.

Widening one method is therefore not the job. Segments set only on `FailedAttempt` would leave the
other eight recorders — the serial success settle among them — recording `Segments = null`,
**including `needs-human`, which is an outcome real `run.json` rows in the corpus actually carry.**
That is section 2's survivorship finding one column over: durations present on exactly the attempts
that converged, absent on the ones whose cost the comparison is trying to see.

#### The nine recorders, and what each one can honestly say

The question that decides each row is **not** "does the journaller method take an `ActionRun`". It is
**does the CALLER hold the action** — because the shipped pattern for `FailedAttempt` and `Cancelled`
is a caller that already passes `costUsd: action.CostUsd, usage: action.Usage` into optional
parameters. `12-record-the-turn-count` has just widened both the same way for `Turns`; extend that
pattern rather than inventing a second one. Two rows below are counter-intuitive precisely because the
method signature does not tell you which road it is on.

**Carry the segments — the caller holds an action (so `ActionMs` is in hand):**

| recorder | how the value arrives |
|---|---|
| `CompleteSucceededOrInvalidFragment` | takes BOTH `ActionRun action` and `GuardrailRunResult guardrails` — the only recorder that does; it can build the whole `AttemptSegments` inside the method |
| `NeedsHuman` | takes `ActionRun action` but no guardrail result |
| `PermissionWall` | takes `ActionRun action` but no guardrail result |
| `StructuralWallHalt` | takes `ActionRun action` but no guardrail result — **and its call site DOES hold one** (guardrails ran and failed there), so the guardrail half must arrive from the caller |
| `FailedAttempt` | **takes NO `ActionRun`** and no guardrail result. Add an optional segments parameter and forward it from every one of its `TaskExecutor` call sites — five at authoring time — exactly beside the `costUsd:`/`usage:`/`turns:` they already forward |
| `Cancelled` | **takes NO `ActionRun`** and no guardrail result. Same optional-parameter treatment, but at only TWO of its three call sites — see the split below |

**A segment pair is assembled from TWO sources, and this is where the turn count's shape stops being a
template.** `Turns` lives entirely on `ActionRun`, so `12-record-the-turn-count` could read it inside
the four `ActionRun`-taking methods. `ActionMs` lives on the `ActionRun`; `GuardrailMs` lives on the
`GuardrailRunResult` — and **only `CompleteSucceededOrInvalidFragment` receives both.** Copying task
12's shape method-for-method therefore produces a `StructuralWallHalt` that silently records
`GuardrailMs = null` on a path where guardrails demonstrably ran and failed.

The uniform answer that is correct at every one of the six: **give each carrying recorder an optional
`AttemptSegments? segments = null` parameter and let the `TaskExecutor` CALL SITE build the pair** from
what it holds — `action.ActionMs` always, and `guardrails.GuardrailMs` wherever a `GuardrailRunResult`
is in scope. One mechanism, no per-method special case, and the same additive optional-parameter shape
`costUsd`/`usage`/`turns` already use. (`CompleteSucceededOrInvalidFragment` may build its own, since
it already has both; do not restructure it if it reads better that way.)

**Record `null` — the caller holds no action, and null is the honest answer:**

| recorder | why there is nothing to measure |
|---|---|
| `RateLimitExhausted` | a settle marker for a model call that never happened |
| `NoRoute` | the route never resolved, so the attempt was never launched |
| `TaskPreflightFailed` | fires BEFORE the attempt loop exists; the action never ran |
| `Cancelled`, at its pre-attempt call site | cancelled between attempts inside a transient backoff; that call site already passes `costUsd: null` for the same reason |

**`Cancelled` is SPLIT, and it is the row most likely to be got wrong.** It has three `TaskExecutor`
call sites. Two are mid-attempt — after the action returned — and both already pass
`action.AsProcessResult(), action.CostUsd, action.Usage`; those carry the segments. The third is the
pre-attempt cancel inside the transient-backoff loop and passes `costUsd: null` because no model ran;
that one passes nothing and its record keeps its null. **One method, two answers, decided at each call
site — never inside the method.** Do not "fix" the asymmetry by defaulting the pre-attempt site to
anything.

`ValidateFragmentForSettle` also builds an attempt, which is why you may notice it while reading — but
it builds a `PendingAttempt`, not an `AttemptRecord`, which is why the grep does not find it. It is the
WORKTREE settle and belongs to `16-carry-phase1-facts-through-the-worktree-settle`. **Leave it alone**;
`Scheduler.cs` is not in your writeScope either.

#### A HALF-populated `AttemptSegments` is correct, and it is not the zeroed-record trap

**Most** of the carrying call sites settle BETWEEN the two phases — after the action returned and
before any guardrail ran. `NeedsHuman` short-circuits on the state-out signal; both `PermissionWall`
sites and the action-failed, staging, harness-write and write-scope `FailedAttempt` sites all report
"guardrails skipped". Only three failure sites sit downstream of the guardrail pass — the
guardrail-failed `FailedAttempt`, `StructuralWallHalt`, and the LATER of `Cancelled`'s two mid-attempt
sites (its earlier one fires right after the action, so even that one method's two carrying sites
differ here).

At an in-between site the honest record is `ActionMs` set and **`GuardrailMs` null** — one phase
happened, the other did not. That is a different thing from the both-null record the next section
forbids, and **you must not withhold the action segment just because the guardrail segment is
missing.** Only when NEITHER phase ran is `Segments` itself null. Do not read a `GuardrailRunResult`
into scope at a site that has none in order to make the pair look complete; there is no measurement
there to report.

#### Forward it at the `TaskExecutor` call sites

**Grep for `_journaler.`** — one grep finds every call site, and every one appears in a table above.
Walk the hits in order and give each one its row's answer. The `FailedAttempt` sites — **including the
guardrail-failed one**, the path section 2 is about, where an attempt that burned twenty minutes before
going red is exactly the evidence the corpus is missing — and the two mid-attempt `Cancelled` sites
forward the values. The pre-attempt `Cancelled`, `RateLimitExhausted`, `NoRoute` and
`TaskPreflightFailed` sites pass nothing: all four run BEFORE the action launches — three of them in a
different method entirely — so there is no `action` in scope to pass. If a call site's answer is not
obvious, the deciding question is: **is there an `ActionRun` in scope here?** If the compiler says no,
the record's `Segments` is null and correct.

### Absent, never a zeroed record — and a null there is a FACT, not a gap

An `AttemptSegments` whose members are both null is a CLAIM that a measurement was taken and came back
empty. Follow the discipline `ActionRun.Usage`'s comment already states — *absent stays absent, never a
zeroed record* — and leave `Segments` null when there is nothing to record. Do not emit `0` for a phase
that did not run, and never borrow a number from elsewhere to make the column look full.

For the rows in the null table above this is not a shortfall you are conceding, it is the **same
null-versus-zero line `TelemetryRow.CostUsd` already draws** in its own doc-comment — *"or null when
the runner never reported a cost — NOT the same claim as a recorded `0`"* — and it is why the three
recorders in that table carry no `CostUsd` and no `Provenance` at all, and why the pre-attempt
`Cancelled` site passes `costUsd: null`. **Read their existing `#532` comments before you touch
them**: each states, in the code, why its field is deliberately absent. You are extending an
established discipline, not filling a hole someone forgot.

A reader stratifying attempt cost must be able to tell *"this attempt ran and burned 90 seconds of
model time"* from *"no attempt ran here at all"*. Both halves are load-bearing, and only one of them is
a number. `11-author-tests-attempt-envelope` pins both halves — one of your tests asserts a deliberate
null and is GREEN before you start; its job is to stay green.

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
