## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Update the **guardrails-review** skill (`.claude/skills/guardrails-review/**`) for plan 08 M6 to add the
new adversarial probes:
- **vacuous/over-broad `writeScope`** → BLOCKER (a `**`/bare-top-level scope is protection in name only)
  / WEAK for a merely loose scope.
- **scope-intent mismatch** (a declared scope that does not match the task's described artifact) → WEAK.
- **independent-sibling scope OVERLAP** (two independent siblings whose `writeScope`s `Overlaps`) → WEAK,
  conflict-risk → suggest a `dependsOn` edge or a re-breakdown.
- **implementation-scope-includes-its-test-files** → BLOCKER (the TDD test-protection hole).
- **missing terminal `integrationGate`** on a multi-leaf / fan-in plan → BLOCKER.
- **thin gate** (an `integrationGate` sink with no `scope: "integration"` guardrail) → BLOCKER.

Keep the skill internally consistent and aligned with the post-plan-08 schema (writeScope, scope,
integrationGate; the triad is gone). Publish nothing to state.
