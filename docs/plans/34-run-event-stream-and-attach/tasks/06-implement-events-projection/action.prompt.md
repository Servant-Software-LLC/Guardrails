## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `06-implement-events-projection`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "06-implement-events-projection": { "someKey": "someValue" } }`.
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

## Task

Fill real logic over the stubs in `src/Guardrails.Core/Execution/RunEventStream.cs` so
`RunEventStreamTests` passes.

The row shape is **not yours to invent**. It is the telemetry corpus row emitted LIVE rather than at
settle, so an event and its eventual telemetry row agree field-for-field on everything they share. Read
`src/Guardrails.Core/Telemetry/TelemetryRow.cs` and reuse its field names and value tokens verbatim -
`runId`, `taskId`, `attempt`, `outcome`, `model`, `tier`, `costUsd`. Add only what a LIVE stream needs
and a settled row does not (a timestamp, and the event `kind`).

Requirements:

- append one JSON object per line, flushed as it happens - a consumer tailing the file must see a
  completed attempt without waiting for the run to end
- every line independently parseable (no trailing-comma array, no pretty-printing across lines)
- forward every observed member to the inner observer

Do NOT edit the authored tests.
