## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-01-correct-the-record/01-correct-retrypolicy-rationale": { "someKey": "someValue" } }`. The harness REJECTS a fragment keyed by
  anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.


## Task
PR #426 shipped two statements that are factually wrong. Remove both. This task changes WORDING and
COMMENTS only — do not change which commands the advice recommends (wave 4 owns that).

1. In `src/Guardrails.Core/Execution/RetryPolicy.cs`, `AppendSalvageSection`:
   - DELETE the sentence asserting `This works under the default read-only git permissions`. It is
     false: `docs/plans/diagram-live-status-and-search/guardrails.json` grants NO git at all and that
     plan DID fire salvage, so the claim is wrong for real plans in this repo.
   - In the `// Issue #374:` comment, DELETE the claim that the editing-tool route is
     "writeScope-enforced at write time" / safer than a git write. There is NO write-time writeScope
     enforcement: `WriteScope.IsInScope` is called in exactly three places — `HarnessWrite.cs`
     (the harness's OWN write), `StagingMover.cs` (a path match), and `WriteScopeCheck.cs` (the
     RETROSPECTIVE check). `WorktreeContainmentHook` enforces worktree containment only, and says so
     in its own doc comment. Both routes are caught by the same retroactive check.
   - Replace the removed rationale with the accurate one: the `git show` route is recommended because
     it is the only route guaranteed under a plan that grants no git write verbs, NOT because it is
     more strongly enforced.
2. In `SalvageRef.cs` and `RunConfig.cs`, the `<c>git show &lt;ref&gt;:&lt;path&gt;</c>` doc-comment
   references are already correct — leave them. Only remove any surviving claim about write-time
   enforcement if one appears there.

Do NOT weaken or delete the existing tests. Keep the file compiling.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/RetryPolicy.cs`,
`src/Guardrails.Core/Execution/SalvageRef.cs` and `src/Guardrails.Core/Model/RunConfig.cs`. After this
task completes the harness runs a `git diff` check and rejects any edit outside these paths — including
test files and the SSOT doc (other tasks own those). An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that
file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
