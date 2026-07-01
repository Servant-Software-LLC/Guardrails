## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `16-update-domain-knowledge-skill`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "16-update-domain-knowledge-skill": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Update the `guardrails-domain-knowledge` skill for the domain-model/contract facts this feature moved
(Deliverable 9 acceptance + the skill's own SELF-UPDATING clause). Edit ONLY files under
`.claude/skills/guardrails-domain-knowledge/` (currently just `SKILL.md`). Update only the affected
section(s); do not rewrite unrelated content.

Facts that moved (reflect each where the skill carries it):
- **The two-scope, four-folder model:** preflights and guardrails are each first-class at TWO scopes —
  PLAN-LEVEL (`<plan>/preflights/` "Full Flight Checks" run once before the DAG; `<plan>/guardrails/`
  "Terminal Gate" run once on merged HEAD) and TASK-LEVEL (`tasks/<id>/preflights/` JIT dependency-delivery,
  and the existing `tasks/<id>/guardrails/` postconditions). The plan-level folders are siblings of `tasks/`,
  `guardrails.json`, `state/` at the plan root.
- **The three new outcomes (all exit 2):** `plan-preflight-failed` (halt before scheduling),
  `task-preflight-failed` (needs-human for the cone, no attempt burned), `plan-guardrail-failed` (terminal
  halt on merged HEAD).
- **The two additive journal sections:** top-level `planPreflights` + `planGuardrails` (each `planHash`-keyed,
  outside `tasks{}`), and the two new resume rules (SKIP pre-DAG on a matching marker; terminal-only resume).
- **The revalidate ids:** `--revalidate-task plan:guardrails` (and `plan:preflights`).
- **The migration:** GR2017 + the `integrationGate` task kind are RETIRED; GR2018's content teeth are
  RE-HOMED onto the `<plan>/guardrails/` folder (≥1 real integration-set re-run); `scope:"integration"` stays
  the §4.3 per-union tag.

Point to `docs/plans/02-schemas-and-contracts.md` as the SSOT where relevant. Keep it coherent. Publish
nothing to state.
