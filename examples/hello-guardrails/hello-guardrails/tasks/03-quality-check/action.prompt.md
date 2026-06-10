## Harness contract (do not remove)
- Read your input state from the JSON file at the `GUARDRAILS_STATE_IN` path the
  harness provides in this prompt's appended sections.
- Write ONLY your new/changed keys as a JSON object fragment to the
  `GUARDRAILS_STATE_OUT` path. Do not echo the whole snapshot back.
- If a previous-attempt feedback section is appended below, this is a RETRY: read it
  first and fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write
  `{ "needsHuman": "<your question>" }` to the state-out path and stop.

## Task

Quality-check the greeting.

1. Read the shared state. Find the greeting file path under
   `02-generate-greeting.greetingPath` (it was published by the upstream task).
2. Read the greeting file.
3. Write a short markdown report to `out/report.md` containing exactly these
   sections:
   - `# Greeting Quality Report`
   - `## Greeting` — quote the greeting text verbatim.
   - `## Tone assessment` — one or two sentences assessing whether the greeting is
     friendly and welcoming, written in a warm, constructive voice.
4. No state fragment is required for this task (it is the terminal task); writing
   nothing to the state-out path is fine.

Completion criteria (your guardrails check these):
- `out/report.md` exists and contains the three sections above.
- The tone assessment reads as friendly to an independent reviewer.
