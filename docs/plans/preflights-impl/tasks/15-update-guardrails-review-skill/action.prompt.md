## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `15-update-guardrails-review-skill`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "15-update-guardrails-review-skill": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Migrate the `guardrails-review` skill to the four-folder model (Deliverable 9). Edit ONLY files under
`.claude/skills/guardrails-review/` (it currently has just `SKILL.md`).

`guardrails-review` must now:
- **Probe the four folders as BLOCKERs** where a required folder/check is missing — e.g. a multi-leaf/fan-in
  plan with an EMPTY or TAUTOLOGICAL terminal-gate `<plan>/guardrails/` folder is a BLOCKER (the re-homed
  GR2018 obligation: ≥1 real integration-set re-run).
- **Emit the live-probe guidance as an ADVISORY WARN, not a BLOCK** (the harness enforces nothing):
  - Plan-level: process-start (a full `dotnet test` / build over committed bytes) is FINE — it is the
    canonical Full Flight Check. Steer away from network / poll / daemon / live-service probes (WARN) — a
    flake there halts the whole run (maximal blast radius).
  - Task-level: PREFER a byte/exit check (runs per task, before the attempt loop); steer away from
    network/poll (WARN).
  - The property protected is FLAKE-FREEDOM, not process-count. `guardrails validate`/`run` neither warns nor
    blocks on a live probe — this is authoring advice only.
- Reflect the retirement of GR2017 + the `integrationGate` task kind (no terminal sink task to probe) and the
  re-homing of GR2018 onto the folder.

Honor the skill's SELF-UPDATING convention. Keep it coherent. Publish nothing to state.
