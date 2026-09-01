## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `16-carry-phase1-facts-through-the-worktree-settle`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "16-carry-phase1-facts-through-the-worktree-settle": { "someKey": "someValue" } }`.
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

## Why this task exists

Three upstream tasks have already journalled the Phase-1 attempt facts on the **serial** settle path:

- `06-journal-the-bucket-serial` — the task-fingerprint bucket
- `12-record-the-turn-count` — the attempt's turn count
- `12a-segment-the-attempt-durations` — the action and guardrail segment durations

**Worktree is the DEFAULT execution mode, and it does not use that path.** The worktree settle builds
its OWN `AttemptRecord` and never consults the journaller. So as this task begins, every one of those
three facts reaches serial runs only — and the majority of real runs are worktree runs.

That is not a hypothesis. `src/Guardrails.Core/Journal/JournalModel.cs` documents it in prose (grep for
**`A member hung directly off the attempt record`**) and `src/Guardrails.Core/Execution/RunReport.cs`
carries the worked example (grep for
**`WITHOUT this line the value the record above sets reaches serial runs only`** — the doc comment on
`PendingAttempt.Usage`). `CostUsd` survived that path for exactly one reason: it was declared on
`PendingAttempt`. Its `Usage` sibling did not, until #475 noticed.

This task closes the same gap for the three new facts, in the two places it has to be closed.

## Plan of record

This task implements the worktree half of section 3.2's bucket and section 3.4's turns and segmented
durations, in `docs/plans/30-telemetry-phase-1.md`. Read sections 3.2 and 3.4; where this prompt and
the plan disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

## Two things you do NOT have to carry, and why

`AttemptProvenance.ModelDigest` (task 10) and `AttemptProvenance.RouteWarm` (task 14) already reach
both settle paths **for free**, because `PendingAttempt.Provenance` already exists and
`Scheduler.RecordSucceededSettle` already reads it — grep for `Provenance = pending.Provenance`. That
is the whole reason those two facts were put on the provenance rather than on the record. Do not add
duplicate carriers for them; a second copy of a fact that already arrives is a second thing that can
disagree with the first.

## Site 1: `AttemptJournaler.ValidateFragmentForSettle`

**Authoring-time state — VERIFY IT.** `06-journal-the-bucket-serial`, `12-record-the-turn-count` and
`12a-segment-the-attempt-durations` all edit `src/Guardrails.Core/Execution/AttemptJournaler.cs` before
this task runs. **Grep for the markers; never trust a line number, and re-read what you find.**

Grep for **`new PendingAttempt`**. As authored, the initializer inside `ValidateFragmentForSettle`
already carries `Attempt`, `StartedAt`, `ActionExitCode`, `CostUsd`, `Usage`, `LogDir` and
`Provenance` — and the `Usage` line carries the `#475` comment that is the register to write in.

Add `Bucket`, `Turns` and `Segments` beside them, sourced exactly the way the serial path sources
them (read `CompleteSucceededOrInvalidFragment` — grep for it — and mirror it):

- `ValidateFragmentForSettle` already receives **`ActionRun action`** and
  **`GuardrailRunResult guardrails`** as parameters, so `action.Turns`, `action.ActionMs` and
  `guardrails.GuardrailMs` are all in scope without a new dependency.
- It already receives **`TaskNode task`** as its first parameter, so the bucket is computable there
  through the same classifier the serial path uses. Read what
  `06-journal-the-bucket-serial` actually did before copying: compute it the same way, do not invent a
  second computation, and do not read the bucket off the task's name — `TaskFingerprintBucket.Classify`
  takes a write-scope list and a guardrail list precisely so that reading it off the name is impossible.

Each new line gets a doc-style comment in the register `PendingAttempt.Usage`'s already uses: name the
FAILURE the line prevents, not what the line does.

## Site 2: `Scheduler.RecordSucceededSettle`

**Authoring-time state — VERIFY IT.** Grep for **`new Journal.AttemptRecord`** in
`src/Guardrails.Core/Execution/Scheduler.cs`. **Note the `Journal.` qualifier: a bare
`new AttemptRecord` grep misses this site entirely**, and it is the only one of the twelve
`AttemptRecord` construction sites that lives outside `AttemptJournaler.cs` and
`TaskExecutor.RevalidateAsync`.

As authored, the initializer carries `Attempt`, `StartedAt`, `EndedAt`, `ActionExitCode`, `Outcome`,
`CostUsd`, `Usage`, `LogDir` and `Provenance`, every one of them read off `pending`. Then:

- **`Turns` and `Segments` go INTO that initializer**, read off `pending` like their neighbours.
- **`Bucket` does NOT.** `Bucket` is a **TASK-grain** fact — constant across a task's own retries
  within one run — so `03-extend-the-journal-record-shape` declared it on `TaskJournalEntry`, not on
  `AttemptRecord`. Writing `Bucket = pending.Bucket` inside the `new Journal.AttemptRecord { … }`
  initializer will not compile. It travels instead through the recorder call on the next line: grep for
  **`RecordSettleWithAttempt`** and pass `pending.Bucket` through the optional `string? bucket`
  parameter `06-journal-the-bucket-serial` added to it. Read that method's current signature before
  writing the call — 06 also documented the explicit-interface arity forwarder that sits beside it, and
  a call written against a remembered signature will not compile.

## Guardrail 03 is a source-shape check, and it is one of only two in this plan

`guardrails/03-both-settle-records-set-every-phase1-member.ps1` reads `Scheduler.cs` as TEXT and
asserts that the initializer and the recorder call above really do read `pending`. It exists because
the property is a fact about **two construction sites agreeing**, which no test can observe without
driving the entire scheduler through a real worktree provider. Everything else in this plan was
demoted to a test under the #468 gate; this survived it.

It is the SECOND line of defence. The first is
`tests/Guardrails.Core.Tests/Execution/WorktreeSettlePhase1Tests.cs`, which
`15-author-tests-worktree-settle-carries-phase1` authored and which guardrail 02 runs.

**If guardrail 03 reports something absent that you can see is present, read its message before
escalating.** It strips comments and string literals before matching, so a member named only in a
comment does not satisfy it — that is deliberate, not a defect.

## Do not do these

- **Do NOT edit the tests.** `tests/Guardrails.Core.Tests/Execution/WorktreeSettlePhase1Tests.cs` is
  outside this task's writeScope; an edit there fails the write-scope check and burns a retry. If a
  test is genuinely wrong, write `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the
  state-out path.
- **Do NOT change the serial path.** `CompleteSucceededOrInvalidFragment` and the failure paths were
  finished by tasks 06, 12 and 12a. Read them; do not rewrite them. If the serial path looks wrong to
  you, that is a finding to report in your summary, not a change to make here — it would put this
  task's diff outside what its guardrails certify.
- **Do NOT hang a new member directly off `AttemptRecord` as a shortcut.** That is the exact defect
  this task exists to close, one level down.

## Scope boundary (harness-enforced)

Write only to `src/Guardrails.Core/Execution/AttemptJournaler.cs` and
`src/Guardrails.Core/Execution/Scheduler.cs`. After this task completes, the harness runs a `git diff`
check and rejects any edit outside those paths — including changes to other production files, the
authored test file, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry.
