## Harness contract (do not remove)
- Read your input state from the JSON file at the `GUARDRAILS_STATE_IN` path the
  harness provides in this prompt's appended sections.
- Write ONLY your new/changed keys as a JSON object fragment to the
  `GUARDRAILS_STATE_OUT` path. Do not echo the whole snapshot back.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this `task.json` lives in (here
  `02-generate-greeting`), NOT the `stableId`. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "02-generate-greeting": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended below, this is a RETRY: read it
  first and fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write
  `{ "needsHuman": "<your question>" }` to the state-out path and stop.

## Task

Generate the greeting.

1. Read the shared state and find the `recipientName` value.
2. Run the existing script `out/greet.ps1` (relative to your working directory)
   with that name, e.g. `pwsh -NoProfile -File out/greet.ps1 -Name <recipientName>`.
3. Save the script's output, exactly as printed, to `out/greeting.txt`.
4. Publish where the greeting landed for downstream tasks by writing this fragment
   to the state-out path:

```json
{ "02-generate-greeting": { "greetingPath": "out/greeting.txt" } }
```

Completion criteria (your guardrails check these):
- `out/greeting.txt` exists.
- Its content matches `Hello, <recipientName>!`.
