## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author xUnit.v3 tests in the single new file
`tests/Guardrails.Core.Tests/WriteScopeMatcherTests.cs` (class name exactly `WriteScopeMatcherTests`,
selected via `--filter "FullyQualifiedName~WriteScopeMatcherTests"`). This is the matcher PROOF
HARNESS - the milestone gate for plan 08 §2.1/§2.2. Encode, BEFORE `Execution/WriteScope.cs` exists:

1. **The full 27-row `IsInScope` truth table** from plan 08 §2.1(d), pinned VERBATIM as a data-driven
   `[Theory]`/`[InlineData]` set - every row's `(scope glob, path, expected IsInScope)`. Use
   `[InlineData(...)]` (one attribute per row, ending in the expected `true`/`false`), not member-data,
   so each row reads `InlineData("<glob>", "<path>", <expected>)`. All 27 rows matter; rows 1, 4, 7,
   18, 19, 21, 23, 25 are the permissive-bug traps and MUST be present. **The trap rows whose CORRECT
   `IsInScope` is `false` MUST assert `false`** - in particular row 1 (`src/Feat*/**` vs
   `src/OtherDir/Z.cs`), row 19 (`src/*Tests/**` vs `src/UnitTestsExtra/X.cs`), row 21 (`src/*-*.cs` vs
   `src/foobar.cs`), and row 23 (`a/**/b/**` vs `a/x/c/y.cs`) must each be an `InlineData(..., false)`
   row. A permissive (prefix/suffix-discarding) matcher returns `true` for these, so asserting them
   `false` is what makes the table RED against the naive matcher - do not soften them to `true`.
2. **Two seeded, reproducible generative/property tests** (§2.2). **Name the two test methods exactly
   `MembershipImpliesOverlap` and `OverlapsCompleteness`** (the scenarios-present guardrail greps for
   those two method names, so the authored test and the guardrail must agree):
   - **`MembershipImpliesOverlap` (membership-implies-overlap):** for any non-empty scope S and path p,
     if `IsInScope(p, S)` then `Overlaps(S, Parse([p as a literal glob]))`.
   - **`OverlapsCompleteness` (`Overlaps` completeness, no false negatives):** generate scope PAIRS that
     share a CONSTRUCTED witness path w (`IsInScope(w, A) && IsInScope(w, B)` by construction); assert
     `Overlaps(A, B)` is true for every such pair. Seed the generator so a counterexample replays.

These reference the not-yet-existing `WriteScope.IsInScope`/`Overlaps`, so the project will not compile
against current code - that is the intended "fails on current code" signal. **Author so the suite is
RED against a deliberately-naive permissive matcher** (the "if a segment contains `*`, skip its
literal prefix/suffix and accept" shortcut): the trap rows + the completeness property are what go red
against the naive matcher. Do NOT implement the matcher - tests only, in this one file. Publish
nothing to state.
