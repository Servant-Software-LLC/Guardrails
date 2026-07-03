## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `14-update-plan-breakdown-skill`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "14-update-plan-breakdown-skill": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Migrate the `plan-breakdown` skill to the four-folder model (Deliverable 9). Edit ONLY files under
`.claude/skills/plan-breakdown/` (SKILL.md and its `references/`: `guardrail-catalogue.md`, `schemas.md`,
`example-breakdown.md`, `stacks/dotnet.md`). This is SSOT-first and gated on the harness loader (Deliverable
2) — it lands after the loader.

`plan-breakdown` must now:
- **Emit the four folders** — plan-level `<plan>/preflights/` (positive AND negative baselines) and
  `<plan>/guardrails/` (terminal whole-repo checks), and task-level `tasks/<id>/preflights/` for the JIT
  dependency-delivery case keyed to a `dependsOn` edge the author already drew.
- **Catalogue the idioms + polarity rules:** positive/negative at plan-level (negative = one-shot,
  plan-level-only — cross-reference the existing `tests-fail-on-current-code`/`tests-fail-on-stubs`
  anti-tautology archetype, do NOT fork it); positive-monotone-safe at task-level.
- **#181 REFRAMED, not replaced:** the brownfield green-test baseline becomes a `<plan>/preflights/`
  POSITIVE check (the general positive-baseline/preflight archetype). The intent survives; the carrier moves
  from a no-op ROOT task to the plan-level folder.
- **Remove the no-op ROOT/END task scaffolding + the #174/#182 short-circuit dependence from the baseline
  story** (the #174/#182 short-circuit remains a general §7 rule for any REAL task that no-ops — untouched;
  it just no longer participates in the preflight story). **Remove the `scope:"precondition"` simulated
  marker** (no third scope value exists under this model — scopes are `integration | local`).
- **The re-homed GR2018 authoring rule:** a multi-leaf/fan-in plan's `<plan>/guardrails/` must carry ≥1 real
  integration-set re-run (the union invariant / build / suite), not a tautological file.

DO NOT alter the `<!-- canonical-schema:promptRunners ... -->` block in `references/schemas.md` — it is
drift-tested byte-for-byte against SSOT §2 and is NOT changed by this feature. Honor the skill's
SELF-UPDATING convention. Keep the edits coherent and internally consistent. Publish nothing to state.
