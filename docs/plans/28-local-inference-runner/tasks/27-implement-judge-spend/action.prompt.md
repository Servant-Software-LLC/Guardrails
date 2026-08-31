## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "27-implement-judge-spend": { "someKey": "someValue" } }`.
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

This task implements part of `docs/plans/28-local-inference-runner.md`. READ THE SECTION(S) NAMED BELOW before you start -
the plan carries the reasoning, the rejected alternatives, and the exact file:line evidence.
Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

Read: **plan section 11 finding 3**.

## Task

### What to build

Make `JudgeSpendRecordingTests` pass. Record the judge's `costUsd` and `usage` on the attempt from
`GuardrailRunner`, surfaced through `AttemptJudge` provenance and the per-tier spend report.

**Record it; do NOT fold it into `JournalCost.Total`.** That separation is the deliverable, not an
oversight - see the plan's finding 3. A runner reporting no cost records `null`, never `0`.

Whether verifier spend *should* count against the cap is a real question the plan deliberately files
rather than answers. Do not answer it here.

**Do NOT edit the test file.**

### `JournalModel.cs` is now in scope, for one record and one reason (added after this task halted)

The first attempt correctly refused: `AttemptJudge` (`src/Guardrails.Core/Journal/JournalModel.cs`) is a
sealed record carrying `Runner`/`Kind`/`Model`/`Effort`/`Tier`/`Strength`/`Bumped`/`Advisory` and **no**
`CostUsd`/`Usage`, and it is serialized to `run.json` by reflection over those properties. There is no
converter or partial-class seam in `GuardrailRunner.cs` or `JournalTierSpend.cs` that can add a
`costUsd`/`usage` key to the emitted `judge` object without the record gaining the members. The
deliverable was unreachable from the old scope.

The plan already authorized this - stage 9's `filesTouched` reads `Execution/GuardrailRunner.cs`,
**`Journal/`**, `JournalTierSpend.cs`, and `Journal/` is the whole folder. The breakdown narrowed it to
`JournalTierSpend.cs` alone. So this restores the plan's own surface; it does not widen it.

**Add `CostUsd` and `Usage` to `AttemptJudge`, and nothing else in that file.** Mirror how
`AttemptRecord` already carries the same two, so the shapes stay recognisable to a reader and to the
telemetry ingest. Do not add, remove or retype members on any other record in `JournalModel.cs` - it is
the schema every run journal on disk is read back through, and an unrelated edit there is a silent
compatibility break for existing `run.json` files.

**The property stage 9 states, and the one that actually matters:** `JournalCost.Total` must be
**provably unchanged**. Judge spend is recorded ALONGSIDE the actor's, never folded into it - a judge is
overhead against the run, not part of the task's own cost, and quietly adding it to the actor's total
would inflate every per-tier and per-model figure the #533 evidence arc depends on. If you cannot record
judge spend without moving that total, stop and write `needsHuman` rather than shipping the smaller
change.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/GuardrailRunner.cs`, `src/Guardrails.Core/Journal/JournalModel.cs`, `src/Guardrails.Core/Journal/JournalTierSpend.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
