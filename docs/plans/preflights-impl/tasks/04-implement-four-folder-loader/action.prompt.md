## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `04-implement-four-folder-loader`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "04-implement-four-folder-loader": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Implement the loader + validation for the four folders (Deliverable 2) so the FourFolder tests authored
upstream pass, and — in the SAME change (invariant 4) — land the matching SSOT edits in
`docs/plans/02-schemas-and-contracts.md`.

Loader/validator (`src/Guardrails.Core/Loading/`, `src/Guardrails.Core/Model/`):
- **Parse all four folders**, reusing the EXISTING guardrail-file parser in `PlanLoader.cs` (the same
  `NN-name.ps1|.sh|.py` + optional `.json` sidecar, or `NN-name.prompt.md` with frontmatter, `catches:`
  comment, ordinal filename sort). The folders differ only in WHERE they live and WHEN they run:
  - plan-level `<plan>/preflights/` and `<plan>/guardrails/` at the plan root → populate
    `PlanDefinition.PlanPreflights` / `PlanDefinition.PlanGuardrails` (the stub properties authored upstream);
  - task-level `tasks/<id>/preflights/` → populate `TaskNode.Preflights`.
- **New diagnostics GR2027+** in `DiagnosticCodes.cs` — use the exact contiguous codes and meanings the
  upstream author task already added as stubs (read them from `DiagnosticCodes.cs` / the `03-author-tests-...`
  state fragment). Emit them for a malformed/empty plan-level `<plan>/preflights/` folder, the re-homed
  GR2018 rule on `<plan>/guardrails/`, a malformed task-level `tasks/<id>/preflights/` folder, and a legacy
  `integrationGate` key (see below). Each guardrail file in a folder must still carry its `catches:` comment,
  a resolvable interpreter, and a well-formed sidecar — reuse the existing per-file diagnostics.
- **Retire GR2017 + the `integrationGate: true` task kind entirely** — no coexistence window. A plan no
  longer declares a terminal sink task; do NOT require one. A plan that STILL declares `integrationGate: true`
  gets an explicit unsupported-key diagnostic (one of the GR2027+ codes) — an honest diagnostic over silent
  ignore (resolves plan note #3).
- **Re-home GR2018's CONTENT teeth onto the `<plan>/guardrails/` folder** — a new GR2027+ code, NOT weakened
  to "non-empty folder": a multi-leaf or fan-in plan MUST have a non-empty `<plan>/guardrails/` folder
  carrying ≥1 deterministic check that ACTUALLY re-runs the integration set (a whole-repo build / full suite
  / a union invariant). A tautological `exit 0`-only file FAILS validation. Implement the "counts toward the
  terminal gate" marker however is cleanest (reuse the §4.3 `scope:"integration"` tag as the folder-file
  marker, OR a folder-scoped equivalent — resolves plan note #2); the OBLIGATION (≥1 real integration-set
  re-run) is what the tests assert.
- **KEEP `scope: "integration"` as the per-union tag** — the §4.3 per-union re-verify is UNCHANGED (still
  drives the integration-scoped set at every union). Only the terminal-sink obligation moved to the folder.

SSOT edits in `docs/plans/02-schemas-and-contracts.md` (SAME change):
- **§1 layout** — add `<plan>/preflights/`, `<plan>/guardrails/` (plan-root), and `tasks/<id>/preflights/`
  to the plan-folder tree.
- **§3.3** — retire GR2017 + the `integrationGate` task kind; re-home GR2018 as the "≥1 real integration-set
  re-run in `<plan>/guardrails/`" folder rule; note `scope:"integration"` stays the §4.3 per-union tag.
- **GR-code table** — document the new GR2027+ codes (the four-folder malformed-declaration codes, the
  legacy-`integrationGate` diagnostic, and the re-homed GR2018 replacement) with the exact numbers used.

Make the FourFolder tests pass WITHOUT modifying them (editing `tests/**` is outside this task's writeScope
and fails the harness git-diff check). If the authored tests are genuinely wrong, write
`{"needsHuman": "<why>"}` rather than editing them. Do not regress the existing suite. Keep everything
warning-clean (`TreatWarningsAsErrors=true`). Publish nothing to state.
