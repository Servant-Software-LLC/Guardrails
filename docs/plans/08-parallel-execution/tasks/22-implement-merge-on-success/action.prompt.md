## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 §5's `--merge-on-success` so `MergeOnSuccessTests` pass:
- `Guardrails.Cli`: a `--merge-on-success` flag; `Model/RunConfig.cs` already carries
  `MergeOnSuccess` (added in M2) - wire the CLI flag to override it.
- `Scheduler`: an end-of-run hook that, ONLY when the run drained wholly green AND the terminal gate
  passed, merges the plan branch `guardrails/<plan-name>` into the user's ORIGINAL branch (ff-only when
  the user branch has not advanced, else a real merge whose re-verify must pass).
- **AI-merge is WITHHELD here:** a conflict, a failed post-merge re-verify, or a dirty user tree halts to
  needs-human with the plan branch INTACT - never a force-overwrite, never an AI auto-resolve of the
  user's own commits. Default OFF leaves the plan branch for the user.

Make `MergeOnSuccessTests` pass WITHOUT editing them; if genuinely wrong, emit
`{"needsHuman": "<why>"}`. Do NOT implement the AI-merge worker (M5). Keep the solution building.
Publish nothing to state.
