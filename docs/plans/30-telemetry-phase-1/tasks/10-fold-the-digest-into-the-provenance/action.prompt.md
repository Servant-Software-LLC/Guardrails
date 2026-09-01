## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `10-fold-the-digest-into-the-provenance`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "10-fold-the-digest-into-the-provenance": { "someKey": "someValue" } }`.
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

This task completes the delivery leg of section 3.3 of `docs/plans/30-telemetry-phase-1.md`: task 08
captures the digest off the wire, and this task carries it to `run.json`. Read section 3.3, including
its `DECIDED 2026-09-01` block. Where this prompt and the plan disagree, the plan is authoritative and
you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
The provenance object you are folding into is the one that section already ships; do not rework it.

## READ THIS BEFORE YOU NAVIGATE

Every claim below about how the code currently works was read while this prompt was written and is
**authoring-time state to verify, not settled fact**. Two sibling tasks edit these same two files
before you run — `04-extend-the-transport-record-shape` adds members to `ActionRunner.cs`, and later
tasks edit `TaskExecutor.cs` — so **every line number here would be stale on arrival**. Navigate by the
greppable markers named in bold; if a marker no longer matches what this prompt describes, trust the
code and say so in your summary.

## Task

Make `09-author-tests-digest-reaches-the-provenance`'s `ModelDigestProvenanceTests` pass. Two edits, in
two files.

### 1. `src/Guardrails.Core/Execution/ActionRunner.cs` — carry the digest one hop

**Grep for `FromPrompt`.** `ActionRun.FromPrompt` restates the `PromptResult` in the shape the attempt
loop consumes; at authoring time it copies `CostUsd`, `Usage` and `ObservedModel` and drops the digest.
`ActionRun.ModelDigest` was declared by task 04 and is populated by nobody.

Copy it the way `ObservedModel` is copied — **a straight member copy, nothing recomputed and nothing
defaulted**. Read the comment block above the `ObservedModel` assignment: absent stays absent, because
the fold downstream treats null as "learned nothing". The digest obeys the same rule for a stronger
reason: a fabricated digest would make two different quantizations of one model look like one sample,
which is the precise failure §3.3 exists to prevent.

`ActionRun.FromScript` is not part of this: a script invokes no model and reports no digest.

### 2. `src/Guardrails.Core/Execution/TaskExecutor.cs` — fold it onto the provenance

**Grep for `ObservedModel is { } observedModel`.** That block is the existing fold: it reassigns
`provenance` through a `with` expression setting `Model` and `RequestedModel`, then re-mirrors the
result through `AttemptArtifacts.WriteProvenance` and rewrites the prose route disclosure.

**Extend that existing `with` expression. Do not add a second fold.** Records are immutable and a
`with` whose result is discarded changes nothing — the block's own comment says so — and two folds
against the same local is the shape where the second silently erases the first's output. The digest
becomes one more member of the one expression that is already there, and the re-mirror already in the
block then carries it with no further edit.

Mind the **guard condition**, which is the one thing extending this block makes you think about. Today
the fold is gated on `action.ObservedModel is { } observedModel`, so a runner that reported a digest and
no model tag would skip the fold entirely and lose the digest. Handle that case; the authored tests
pin the ordinary shape, and this is the near-miss they do not. Whatever you choose, keep the existing
behaviour intact: **a runner that reported nothing must change nothing at all** — silence is not a
disagreement, and assigning over a real route model (or over the `"(cli default)"` sentinel, the only
thing per-attempt provenance has to say for an operator who configured no model anywhere) would erase a
fact.

The re-mirror is not optional. On the guardrail-FAILED path `attempt-provenance.json` is the only
surface that records this at all, because the journaller's failure method takes no provenance
parameter — the comment beside the existing `AttemptArtifacts.WriteProvenance` call says exactly this.
An attempt that learned what served it must not lose that the moment it goes red.

### Why the provenance and not the attempt record

`AttemptProvenance` is the one member that already rides `PendingAttempt`, so a value folded onto it
reaches **both** record-construction paths for free — the serial `AttemptJournaler` and
`Scheduler.RecordSucceededSettle`, which is the DEFAULT worktree mode. A member hung directly off
`AttemptRecord` lands in serial mode and silently vanishes in worktree mode. This is documented at
`src/Guardrails.Core/Journal/JournalModel.cs` (**grep `Placement is D32`**), and one of the authored
tests asserts by reflection that `ModelDigest` is on `AttemptProvenance` and NOT on `AttemptRecord`.
Do not "helpfully" mirror it onto the record: two fields claiming one fact is how they drift, and that
test will go red.

### Do NOT edit the authored tests

`tests/Guardrails.Core.Tests/Execution/ModelDigestProvenanceTests.cs` is outside this task's
writeScope. Make the tests pass by fixing the implementation. If a test is genuinely wrong or
incompatible with the plan, emit `{"needsHuman": "<why>"}` to the state-out path rather than changing
it — an out-of-scope edit fails the write-scope check and burns a retry.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/ActionRunner.cs` and `src/Guardrails.Core/Execution/TaskExecutor.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside those two
paths — including changes to other production files, the authored test file, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry.
