## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `12-implement-events-endpoint`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "12-implement-events-endpoint": { "someKey": "someValue" } }`.
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

Make `EventsEndpointTests` pass by adding a top-level `/events` route to
`src/Guardrails.Cli/Ui/LogServer.cs`.

Add it as an explicit single case in `Handle`, beside the existing `diagram.html` case and BEFORE the
`segments[0] != "tasks"` gate - matching how `/diagram.html` was added, and staying an explicit case
rather than a wildcard static-file server over the logs root (the existing tests pin that nothing else
under `logs/<runId>/` is reachable that way).

Requirements:

- a late subscriber receives the events already written, then subsequent ones as they are appended
- one parseable event per message
- a missing `events.jsonl` is an empty stream, not an error - a run that has emitted nothing yet is healthy
- every existing route keeps working

Do NOT edit the authored tests.
