## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `08-implement-corpus-report`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "08-implement-corpus-report": { "someKey": "someValue" } }`.
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

## Task

Fill real logic over the stub in `src/Guardrails.Core/Telemetry/TelemetryReport.cs` so that
`tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs` passes. Read that test file first; it is
the specification, and in this task it is also the ethics: each of those tests is one of the honesty
rules from `docs/plans/model-evidence-and-graduation.charter.md` §5.

**Do NOT edit the authored tests.** If one is genuinely wrong, write
`{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` rather than changing it.

Implementation notes:

- **Make the stratification structural.** The natural shortcut — compute per-model aggregates, then
  group for display — leaves an unstratified per-model number reachable in the API, and the first
  caller that wants "just the average for this model" will use it. Shape the type so a cross-tier
  per-model figure is not expressible.
- **"Insufficient evidence" is a value, not a missing value.** Model it explicitly so a caller cannot
  render a number that was never earned.
- **Pair the metrics in the TYPE.** Attempts-to-green and abandonment rate come out of one computation
  over one denominator; if they can be read separately, one day they will be.
- **Median and p90, not just the mean** — a single mean over a non-deterministic process hides the
  spread that decides whether a model is usable.
- Reuse `src/Guardrails.Core/Journal/JournalTierSpend.cs`'s null-versus-zero handling rather than
  inventing a second convention for the same distinction.

The fingerprint bucket is derived from what the corpus rows already carry — do not add new inputs, and
do not infer a bucket from the task's name.
