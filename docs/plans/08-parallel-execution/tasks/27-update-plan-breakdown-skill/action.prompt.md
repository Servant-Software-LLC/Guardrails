## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Update the **plan-breakdown** skill (`.claude/skills/plan-breakdown/**`) for plan 08 M6 so that, on the
post-plan-08 harness, a generated folder uses the NEW mechanisms instead of the deleted triad:
- Emit a `writeScope` per task derived from its primary artifact(s); for a TDD pair, the test-author
  task owns the test files and the implementation task's `writeScope` EXCLUDES them (the triad
  replacement). OMIT `writeScope` for tasks that cannot be confidently scoped - never emit a vacuous
  `**` or a bare top-level dir.
- Emit `scope: "integration"` on the whole-repo build / whole-suite test guardrails, and emit exactly
  one terminal `integrationGate` sink that carries them.
- STOP emitting the `captureHashes` / `restoreOnRetry` / `tests-untouched` triad and the `exclusive`
  field (they are deleted from the harness by this plan). Update the references (guardrail-catalogue,
  schemas, stacks/dotnet, example-breakdown) consistently so the worked example and doctrine match.

Update the SKILL.md procedure, the `references/`, and `examples/` so they are internally consistent.
Keep the skill self-consistent (don't leave half the doc describing the triad and half describing
writeScope). Publish nothing to state.
