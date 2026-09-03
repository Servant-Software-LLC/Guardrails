## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `15-wire-projections-into-composition-root`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "15-wire-projections-into-composition-root": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Make `RunCommandObserverWiringTests` pass: inside `BuildObserverChain` (task 13's extracted seam),
construct `RunEventStream` and `ObserverProjection` and insert both into the composed chain, so a real
`guardrails run` writes `events.jsonl` and `observer.jsonl` without anyone asking for them.

Because task 13 made both branches call the one method, wiring it once covers the live-UI branch AND the
`--no-ui` branch. Verify that is still true - if the branches have diverged, wire both and say so.

This is the task that makes the feature exist. Everything upstream is inert until the production
assembler constructs these two objects: unit tests inject their own seams, so they stay green over a
completely unwired composition root.

Do NOT edit the authored tests.
