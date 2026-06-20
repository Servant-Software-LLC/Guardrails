## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 M2's logs elevation + RunConfig additions so `LogsAndRunConfigTests` pass:
- Move the per-attempt log tree out of `state/` to a top-level `logs/` sibling, divided by `runId`:
  `logs/<runId>/<task-id>/attempt-N/`. Update `State/` log-path resolution so `GUARDRAILS_LOG_DIR`
  resolves under `logs/<runId>/...`. `--fresh` clears `logs/` for the abandoned run.
- `Model/RunConfig.cs`: change `MaxParallelism` default to **3**; add `WorktreeRoot` (default null),
  `RunOnCurrentBranch` (default false), `MergeOnSuccess` (default false), surfaced from
  `guardrails.json`.

Make `LogsAndRunConfigTests` pass WITHOUT editing them; if genuinely wrong, emit
`{"needsHuman": "<why>"}`. Keep `src/Guardrails.Core` building. Publish nothing to state.
