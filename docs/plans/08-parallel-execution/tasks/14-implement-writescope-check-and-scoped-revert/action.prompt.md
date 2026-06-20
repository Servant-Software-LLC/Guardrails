## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 §2 / §3.4 so `WriteScopeCheckTests` pass:
- New `src/Guardrails.Core/Execution/WriteScopeCheck.cs`: keyed on `task.json.writeScope` PRESENCE
  (absent ⇒ no check). Runs AFTER the action and BEFORE the task's own `guardrails/`. Computes
  `git diff --name-status <taskBase>..<segmentHEAD>` in the task's segment worktree; every A/M/D path
  must satisfy `WriteScope.IsInScope`. A rename is a paired D+A (no `-M`); a deletion's path must be in
  scope. On a violation: a guardrail-class failure, then a **scoped revert of ONLY the out-of-scope
  paths** (`git checkout <taskBase> -- <paths>`, KEEPING in-scope WIP) and a retry whose feedback names
  the offending files. The verdict path itself writes NOTHING.
- `Model/TaskNode.cs`: add the `WriteScope` field.
- `Loading/PlanValidator.cs` + `Loading/DiagnosticCodes.cs`: **GR2019** (error - a scope entry escapes
  the workspace root) and **GR2020** (warning - a vacuous/over-broad scope like `**` or a bare
  top-level dir).
- Wire the check into `TaskExecutor` between the action and the guardrails.

Make `WriteScopeCheckTests` pass WITHOUT editing them; if genuinely wrong, emit
`{"needsHuman": "<why>"}`. Do NOT touch integration/merge (M4) or the AI-merge worker (M5). Keep the
solution building. Publish nothing to state.
