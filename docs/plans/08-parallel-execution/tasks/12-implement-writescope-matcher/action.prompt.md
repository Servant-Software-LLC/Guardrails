## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement `src/Guardrails.Core/Execution/WriteScope.cs` fresh to the plan 08 §2.1 specification so the
upstream `WriteScopeMatcherTests` (the 27-row truth table + both fuzz properties) pass:
- `IsInScope(path, scope)` and `Overlaps(scopeA, scopeB)`, both deriving from ONE shared
  segment-match helper (DRY on the primitive with cross-task reach).
- Real segment-glob parsing: a literal prefix (before the first `*`), a literal suffix (after the last
  `*`), and every interior literal between two `*`s are all load-bearing; `**` spans zero-or-more whole
  segments (leading, bounded mid-pattern, trailing); a bare directory normalizes to `<dir>/**`; `[]`
  matches nothing; `["**"]` matches everything; comparison per a single `SegmentComparison` constant
  (OrdinalIgnoreCase on Windows). Do NOT take the naive "if the segment contains `*`, skip its literal
  prefix/suffix and accept" shortcut - that is the permissive bug the proof harness is RED against.
- `Overlaps` must be complete (no false negatives) so the §2.2 completeness property passes.

Make `WriteScopeMatcherTests` pass (all 27 rows + both fuzz properties) WITHOUT editing them; if a row
or property is genuinely wrong, emit `{"needsHuman": "<why>"}`. Do NOT yet build the WriteScopeCheck
or wire it into the executor (that is task 13). Keep `src/Guardrails.Core` building. Publish nothing to
state.
