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

**Count the recorders yourself before you edit anything.** **Grep for `new AttemptRecord` in this
file.** At authoring time it returned **nine hits, in nine different methods** — there is no shared
recorder here, and nothing funnels. Each method builds its own `AttemptRecord` and is called
**directly** from `TaskExecutor`. If your grep returns a different number, trust the grep, cover what
it found, and say so in your summary.

Widening one method is therefore not the job. A change made only to `FailedAttempt` would leave the
other eight recorders — the serial success settle among them — recording `Turns = null`, **including
`needs-human`, which is an outcome real `run.json` rows in the corpus actually carry.** That is
section 2's survivorship finding one column over: the column would be populated on exactly the
attempts that converged and empty on the ones a first-pass-rate comparison is trying to measure.

#### The nine recorders, and what each one can honestly say

The question that decides each row is **not** "does the journaller method take an `ActionRun`". It is
**does the CALLER hold the action** — because the shipped pattern for `FailedAttempt` and `Cancelled`
is a caller that already passes `costUsd: action.CostUsd, usage: action.Usage` into optional
parameters. The turn count travels the same road, and two rows below are counter-intuitive precisely
because the method signature does not tell you which road it is on.

**Carry the turn count — the caller holds an action:**

| recorder | how the value arrives |
|---|---|
| `CompleteSucceededOrInvalidFragment` | takes `ActionRun action` — set `Turns = action.Turns` directly |
| `NeedsHuman` | takes `ActionRun action` — directly |
| `PermissionWall` | takes `ActionRun action` — directly |
| `StructuralWallHalt` | takes `ActionRun action` — directly |
| `FailedAttempt` | **takes NO `ActionRun`.** Add `int? turns = null` and forward `turns: action.Turns` from every one of its `TaskExecutor` call sites — five at authoring time — exactly beside the `costUsd:`/`usage:` they already forward |
| `Cancelled` | **takes NO `ActionRun`.** Same optional-parameter treatment, but at only TWO of its three call sites — see the split below |

**Record `null` — the caller holds no action, and null is the honest answer:**

| recorder | why there is nothing to record |
|---|---|
| `RateLimitExhausted` | a settle marker for a model call that never happened |
| `NoRoute` | the route never resolved, so the attempt was never launched |
| `TaskPreflightFailed` | fires BEFORE the attempt loop exists; the action never ran |
| `Cancelled`, at its pre-attempt call site | cancelled between attempts inside a transient backoff; that call site already passes `costUsd: null` for the same reason |

**`Cancelled` is SPLIT, and it is the row most likely to be got wrong.** It has three `TaskExecutor`
call sites. Two are mid-attempt — after the action returned — and both already pass
`action.AsProcessResult(), action.CostUsd, action.Usage`; those carry the turn count. The third is the
pre-attempt cancel inside the transient-backoff loop and passes `costUsd: null` because no model ran;
that one passes nothing and its record keeps its null. **One method, two answers, decided at each call
site — never inside the method.** Do not "fix" the asymmetry by defaulting the pre-attempt site to
anything.

`ValidateFragmentForSettle` also builds an attempt, which is why you may notice it while reading — but
it builds a `PendingAttempt`, not an `AttemptRecord`, which is why the grep does not find it. It is the
WORKTREE settle and belongs to `16-carry-phase1-facts-through-the-worktree-settle`. **Leave it alone**;
`Scheduler.cs` is not in your writeScope either.

#### `null`, never `0` — and a null there is a FACT, not a gap

For every row in the second table, record **`null`**. Never `0`, never a sentinel, and never a number
borrowed from elsewhere to make the column look full. `0` turns is a CLAIM that a model was invoked
and took no turns; `null` says no model was invoked at all, which is what actually happened.

This is the same null-versus-zero line `TelemetryRow.CostUsd` already draws in its own doc-comment —
*"or null when the runner never reported a cost — NOT the same claim as a recorded `0`"* — and it is
why the three recorders in that table carry no `CostUsd` and no `Provenance` at all, and why the
pre-attempt `Cancelled` site passes `costUsd: null`. **Read their existing `#532` comments before you
touch them**: each states, in the code, why its field is deliberately absent. You are extending an
established discipline, not filling a hole someone forgot.

A reader stratifying first-pass rate must be able to tell *"this attempt ran and took 4 turns"* from
*"no attempt ran here at all"*. Both halves of that distinction are load-bearing, and only one of them
is a number. `11-author-tests-attempt-envelope` pins both halves — one of your tests asserts a
deliberate null and is GREEN before you start; its job is to stay green.

#### Mechanics

Add `int? turns = null` **last** in the parameter lists of `FailedAttempt` and `Cancelled`, after the
existing optionals, so no call site that passes nothing has to change — the same additive shape
`usage` and `provenance` already use in those signatures, and the same shape
`06-journal-the-bucket-serial` used for the bucket. Do not invent a new dependency, do not add a
constructor parameter and do not reach for a static: every value you need is already in the caller's
hand.

### 3. `src/Guardrails.Core/Execution/TaskExecutor.cs` — forward it at the call sites

The executor is what calls the journaller, and it is where the SPLIT above is decided. **Grep for
`_journaler.`** — one grep finds every call site, and every one of them appears in one of the two
tables above. Walk the hits in order and give each one its row's answer:

- the `FailedAttempt` sites (including the **guardrail-failed** one, which is the path section 2 is
  about) and the two mid-attempt `Cancelled` sites get `turns: action.Turns`, right beside the
  `action.CostUsd` / `action.Usage` they already pass;
- the pre-attempt `Cancelled`, `RateLimitExhausted`, `NoRoute` and `TaskPreflightFailed` sites pass
  nothing. All four run BEFORE the action launches — three of them in a different method entirely —
  so there is no `action` in scope to pass, and that is the mechanical form of the honesty rule, not
  an oversight.

If a call site's answer is not obvious, the deciding question is the one above: **is there an
`ActionRun` in scope here?** If the compiler says no, the record's `Turns` is null and correct.

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
