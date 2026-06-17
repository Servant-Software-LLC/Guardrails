## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement the `WriteScope` value type and the pure function
`public static bool Overlaps(WriteScope a, WriteScope b)` in a NEW file
`src/Guardrails.Core/Execution/WriteScope.cs`, placed next to
`WorkspaceContainment.cs` and reusing its workspace-relative conventions. The design
of record is §4.1 and §4.4 of `docs/plans/05-disjoint-scope-ownership.md`:

- Parse an ordered list of workspace-relative globs. Glob subset only: literal
  segments, `*` (within a segment), `**` (any depth); a bare `dir` or `dir/` means
  `dir/**`. Reject `?`, brace-expansion, and negation. A universal sentinel `["**"]`.
- `Overlaps` algorithm, in this exact order:
  0. Empty short-circuit: if EITHER side is empty `[]`, return false (disjoint —
     including against `["**"]`).
  1. Universal short-circuit: otherwise if either side contains `**`, return true.
  2. Pairwise glob comparison: walk segments in lockstep; two literals overlap iff
     equal; `*`/`**` overlaps any literal; `**` absorbs the tail.
  3. Conservative bias: anything the walker cannot PROVE disjoint is treated as
     overlapping. Pure, deterministic, no I/O.

Make the `FullyQualifiedName~WriteScope` tests in
`tests/Guardrails.Core.Tests/WriteScopeTests.cs` pass WITHOUT modifying the tests.
If a test looks genuinely wrong or contradicts the design doc, do NOT edit it — write
{"needsHuman": "<why>"} to the state-out path and stop.

Do NOT add a `writeScope` field to `task.json`/the loader/`TaskNode` here — that wiring
is a later milestone (M2/M4); this task delivers only the pure type and function.
Publish nothing to state.
