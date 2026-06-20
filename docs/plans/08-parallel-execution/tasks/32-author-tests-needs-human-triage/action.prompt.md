## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author xUnit.v3 tests in the single new file
`tests/Guardrails.Integration.Tests/NeedsHumanTriageTests.cs` (class name exactly
`NeedsHumanTriageTests`, selected via `--filter "FullyQualifiedName~NeedsHumanTriageTests"`). Encode
plan 08 §9 (AI triage on needs-human, PO #7 / Decision 8) BEFORE the `NeedsHumanTriage` type exists,
driving it through the existing `IPromptRunner` seam with a FAKE runner (the same style as
`AiMergeWorkerTests` — no real `claude` process). The triage step is a constrained `ai-triage` prompt
profile behind `IPromptRunner`; it writes a TASK-LEVEL `logs/<runId>/<task-id>/feedback.md` (a sibling
of the `attempt-N/` dirs, NOT inside an attempt dir).

Pin EACH of the following scenarios as a SEPARATELY NAMED test method (the scenarios-present guardrail
greps for these EXACT method names — a bare keyword mention is NOT sufficient; the 2nd review proved
that gap). Use these method names verbatim:

- `Triage_RunsOnAttemptExhaustion` — triage runs exactly ONCE when a task reaches `needs-human` because
  it exhausted its retry budget (action/guardrail failures across all attempts). Assert the fake
  `IPromptRunner` received exactly one `ai-triage`-profile invocation for that task.
- `Triage_SkippedOnAgentEmittedNeedsHuman` — when the agent itself emitted `{"needsHuman": "..."}`
  (a clean short-circuit — the human is already being asked), triage does NOT run. Assert ZERO
  `ai-triage` invocations.
- `Triage_NotRunMidRetry` — triage does NOT run between attempts while the task can still retry (only
  on the terminal exhaustion transition). Assert no `ai-triage` invocation occurs on a non-final failed
  attempt that is followed by another attempt.
- `Triage_WritesFeedbackMdWithToolVsLocalDiagnosis` — triage writes `logs/<runId>/<task-id>/feedback.md`
  containing a diagnosis classified as either a Guardrails-TOOL problem (warrants a drafted GH issue
  against the Guardrails repo) or a problem LOCAL to the current repo. Assert the file exists at the
  task-level path and that both diagnosis classes are representable (drive one case of each via the
  fake runner's canned output and assert the written file reflects it).
- `Triage_NeedsHumanMessageReferencesFeedbackPath` — the task's `needs-human` message (the text the run
  summary / `status` render) references the `feedback.md` path. Assert the surfaced message contains the
  `logs/<runId>/<task-id>/feedback.md` path.
- `Triage_IsAdvisory_ThrownTriageDoesNotChangeVerdictOrBlock` — triage is ADVISORY and gates NOTHING.
  Drive the fake runner to FAIL/throw (or return `IsError == true`); assert (a) the task verdict is
  STILL `needs-human` (triage cannot change it, cannot re-open, cannot mark done), (b) the run is NOT
  blocked/aborted (a failed triage just means "no feedback.md produced", logged), and (c) the triage
  `PromptResult.IsError`/exit code is NEVER read as the verdict.
- `Triage_AutoFileOffByDefault_DraftsOnly` — with `triageAutoFile` unset/default, triage only DRAFTS the
  GH issue (title+body) INTO `feedback.md` and files NOTHING to a remote. Assert no auto-file/GH-API
  side effect occurs by default (drafts only).

These tests reference the not-yet-existing `NeedsHumanTriage` type (and an `ai-triage` profile), so the
project will not compile against current code — that is the intended "fails on current code" signal
(real compile-coupling). Do NOT implement the triage step — tests only, in this one file. Publish
nothing to state.
