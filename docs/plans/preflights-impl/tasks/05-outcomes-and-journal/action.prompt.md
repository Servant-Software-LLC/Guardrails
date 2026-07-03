## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `05-outcomes-and-journal`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-outcomes-and-journal": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Add the three new outcomes and the two additive journal sections (Deliverable 6, "the F9 split", and
test-group #11), and land SSOT §7 in the same change (invariant 4). This is a **data-model** change
(enum members + additive record sections) — there is no behavioral stub, so it is authored and made green
in ONE task (the round-trip/backward-compat tests are the verification).

Journal / outcome model (`src/Guardrails.Core/Journal/`, and `src/Guardrails.Core/Execution/TaskOutcome.cs`
if the in-memory per-task outcome needs it):
- **`task-preflight-failed`** — a per-task result: add it to the attempt-outcome enum (`AttemptOutcome` in
  `Journal/AttemptOutcome.cs`, with its kebab wire string in `Journal/JournalJson.cs`), journaled as an
  `AttemptRecord.outcome` INSIDE `tasks{}`, alongside `guardrail-failed` / `action-failed` / `output-cap` /
  `max-turns` / `rate-limited`. Reuses exit 2. (If the `TaskOutcome` enum in `Execution/` also needs a member
  for the in-memory settle, add it there too.)
- **`plan-preflight-failed`** — pre-DAG phase failed → journaled in a NEW top-level `planPreflights` section
  (OUTSIDE `tasks{}`): `{ "status": "passed"|"failed", "planHash": "sha256:…", "evaluatedAt": "…", "checks": [ … ] }`.
- **`plan-guardrail-failed`** — terminal `<plan>/guardrails/` gate failed on merged HEAD → journaled in a NEW
  top-level `planGuardrails` section (OUTSIDE `tasks{}`):
  `{ "status": "failed", "planHash": "sha256:…", "failedChecks": [ … ] }`.
- Add `planPreflights` and `planGuardrails` as new top-level keys on `JournalDocument`
  (`Journal/JournalModel.cs`) + their converters (`Journal/JournalJson.cs`). They must be OPTIONAL: an older
  reader ignores them; a plan without the feature omits them; the existing `tasks{}` shape is UNTOUCHED.
- Distinct outcome NAMES (not a shared name + a `scope` field) — a reader sees `plan-preflight-failed` and
  knows the whole run halted; the plan-level scope is also encoded structurally (the result lives outside
  `tasks{}`).

Tests (in `tests/Guardrails.Core.Tests/JournalOutcomesRoundTripTests.cs`, tagged
`[Trait("Category","Preflights")]`, reusing the existing journal-JSON test conventions):
- A journal WITH both `planPreflights` + `planGuardrails` sections serializes/deserializes **byte-losslessly**
  (golden round-trip).
- A journal WITHOUT the two sections round-trips losslessly (they are omitted, not emitted as null).
- An **older-shape** journal (no new sections) still loads (backward-compat).
- A per-task attempt with `outcome: "task-preflight-failed"` round-trips (the kebab wire string is correct).

SSOT edit in `docs/plans/02-schemas-and-contracts.md` §7 (SAME change): document the three new outcomes,
the two additive top-level sections (each `planHash`-keyed), and that all three halts reuse **exit 2**. (The
two new RESUME rules and §7.1 revalidate narrative are landed by the pre-DAG / terminal phase deliverables
that implement them — add only the journal-shape/outcome facts here.)

Keep everything warning-clean (`TreatWarningsAsErrors=true`). Do not regress the existing suite. Publish
nothing to state.
