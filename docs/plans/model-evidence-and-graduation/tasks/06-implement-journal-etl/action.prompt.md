## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `06-implement-journal-etl`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "06-implement-journal-etl": { "someKey": "someValue" } }`.
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

Fill real logic over the stub in `src/Guardrails.Core/Telemetry/TelemetryIngest.cs` so that
`tests/Guardrails.Core.Tests/Telemetry/TelemetryIngestTests.cs` passes. Read that test file first; it is
the specification.

**Do NOT edit the authored tests.** If one is genuinely wrong, write
`{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` rather than changing it.

The ETL reads a `JournalDocument` and writes two grains of row through the corpus store, classifying
every `guardrail-failed` attempt through `TelemetryFailureClassifier` on the way. Both collaborators are
already implemented and tested — construct the real ones; do not re-implement either inline.

Points where an implementation typically drifts from the design of record
(`docs/plans/model-evidence-and-graduation.charter.md` §3.1, §5):

- **Every attempt is a row, retries included.** Do not fold a task down to its successful attempt.
- **Null is not zero.** Carry `costUsd` and `usage` through as nullable; a runner that reported nothing
  must not be recorded as having reported zero.
- **The task row's identity is `definitionHash`**, which is what makes the same task comparable across
  runs and machines. Carry it verbatim; do not recompute or normalize it.
- **`tierSource` matters as much as `tier`.** A rung that came from a pin, a climb or the plan-wide
  default is not the same evidence as one the resolver chose, and the report later stratifies on it.

Also add the entry point the CLI will call: a method that takes a plan folder path, reads its
`state/run.json` via the existing `JournalReader`, and ingests it. Ingesting a folder with no journal
must be a reported no-op, never an exception — backfill will be pointed at directories of plans, some of
which never ran.
