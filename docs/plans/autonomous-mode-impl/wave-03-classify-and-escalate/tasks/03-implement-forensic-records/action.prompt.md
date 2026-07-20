## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/03-implement-forensic-records` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the `logs/<runId>/autonomy.jsonl` writer (issue #361 Phase 3) by filling REAL logic over the
`AutonomyJsonl` stub the previous task authored. The design of record is
`docs/plans/12-autonomous-mode.md` §6.3 (the record shape) and §6.1 (what lands after every gate). Make
the authored `AutonomyForensicRecordsTests` pass WITHOUT editing them.

Implement:
- **`AutonomyJsonl.Append(...)`** (in `src/Guardrails.Core/Journal/AutonomyJsonl.cs`): APPEND one
  compact single-line JSON object per call to `logs/<runId>/autonomy.jsonl` (create the file/dir if
  absent; never truncate — N calls ⇒ N lines). The record carries the §6.3 fields (`at`, `gate`,
  `boundary`, `subject`, `classification`, `criticality`, `confidence`, `threshold`, `decision`, and the
  gate-specific `question`/`bestGuess`/`rationale`). Use the repo's existing JSON options style (grep for
  `JournalJson` / `PlanJson.Options`) and its atomic/append file helper if one exists (grep for
  `AtomicFile`); write UTF-8, one object per line, no pretty-printing. This is the multi-fire DETAIL
  stream (the durable AUDIT is `decisions[]` in run.json — the exact overwatch pattern
  `decisions[]` + `overwatch.jsonl`, one level up).
- The **DecisionEntry additive fields + tokens** were landed by the previous task; leave them as-is
  unless a test requires a fix to their shape. Do NOT re-map or re-order the existing required members.

Do NOT change `RunJournal.RecordDecision` semantics or the journal model here (the escalation-sink and
wiring tasks route entries through the shipped `RunJournal.RecordDecision`); this task owns ONLY the
`autonomy.jsonl` detail writer.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AutonomyForensicRecordsTests"`.
Do NOT run the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking
classes drop `outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback,
#253).

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Journal/AutonomyJsonl.cs` and
`src/Guardrails.Core/Execution/DecisionEntry.cs`. Do NOT edit the authored tests, `RunJournal.cs`, or the
journal model — if a test is genuinely wrong, emit `{"needsHuman": "<why>"}` rather than changing it (an
out-of-scope edit fails the write-scope check and burns a retry).

Completion criteria (your guardrail checks these): `AutonomyForensicRecordsTests` and the existing
journal tests in `tests/Guardrails.Core.Tests` all pass.
