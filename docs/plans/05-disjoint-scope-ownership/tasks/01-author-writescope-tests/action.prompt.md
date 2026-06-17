## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author failing xUnit.v3 tests in `tests/Guardrails.Core.Tests/WriteScopeTests.cs`
(mirror the existing test conventions, package versions, and `[Fact]`/`[Theory]`
style already used in that project — e.g. `WorkspaceContainment`-adjacent tests).
The tests encode the M1 design (§4.1, §4.4, §10) for a new `WriteScope` value type
and its pure `Overlaps(WriteScope a, WriteScope b)` function, which must live next to
`WorkspaceContainment` in `src/Guardrails.Core/Execution/WriteScope.cs` (you do NOT
implement it here — only the tests).

The truth-table the tests MUST assert (this is the spec):
- Parse: a list of workspace-relative globs parses; `dir` is normalized to `dir/**`;
  a bare empty list `[]` means "writes nothing"; `["**"]` is the universal sentinel.
- Empty short-circuit: `[]` is **disjoint from every scope, including `["**"]`** and
  including another `[]`. (`Overlaps([], anything)` is false.)
- Universal short-circuit: a non-empty `["**"]` **overlaps every non-empty scope**
  (and overlaps `["**"]`), but does NOT overlap `[]` (the empty rule wins first).
- Disjoint narrow: `["src/A/**"]` vs `["src/B/**"]` → disjoint (no overlap).
- Overlapping narrow: `["src/Feature/**"]` vs `["src/Feature/**"]` → overlap; and
  `["src/Feature/**"]` vs `["src/Feature/Thing.cs"]` → overlap.
- Sibling-prefix trap: `["src/FeatureX/**"]` does NOT overlap `["src/Feature/**"]`
  (FeatureX is a sibling directory, not a child of Feature).
- Normalization equivalence: `["dir"]` and `["dir/**"]` behave identically.
- Conservative bias: a pair the walker cannot prove disjoint is treated as
  overlapping (assert at least one ambiguous case resolves to overlap).
- Malformed globs (`?`, brace `{a,b}`, negation `!`) are rejected at parse time
  (these become GR2017 later, but at the type level parsing must throw/refuse).

The tests MUST fail (or be unable to compile, because `WriteScope` does not exist
yet) against the current code — that failure is intentional and is exactly what the
guardrail checks. Do NOT create `WriteScope.cs` or implement any production behavior.

You do NOT need to hash the test file or write anything to state — the task's
`captureHashes` declaration makes the harness record its SHA-256 automatically.
Publish nothing to state.
