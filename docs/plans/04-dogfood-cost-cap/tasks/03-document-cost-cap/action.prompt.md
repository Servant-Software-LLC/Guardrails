## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Document the now-implemented per-run cost cap. Make these doc edits to match the real
behavior shipped in task 02:

1. `docs/plans/02-schemas-and-contracts.md` — in the §2 `guardrails.json` schema, add the
   `maxCostUsd` field to the JSONC example with a short comment (optional decimal USD;
   absent = no cap), and add a sentence under §2 explaining the semantics: when the
   cumulative journaled cost (SSOT §7 costUsd) reaches/exceeds `maxCostUsd`, the harness
   stops launching new attempts and marks remaining work needs-human with reason
   "cost cap reached". This is the schema SSOT — keep it accurate and minimal.

2. `.claude/skills/guardrails-domain-knowledge/SKILL.md` — note the `maxCostUsd` per-run
   cost-cap semantics in the appropriate place (execution semantics / config), per that
   skill's SELF-UPDATING clause.

Keep edits tight and consistent with the surrounding prose. Do not restate the feature
plan; document the contract. Publish nothing to state.
