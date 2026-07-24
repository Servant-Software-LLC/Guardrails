## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/02-author-tests-run-outcome-policy` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the **pure `RunOutcomePolicy`** (issue #361 Phase 4,
doc 12 §1 "delivery interacts with best-guessing" + §5.2 Option P), plus the MINIMAL stub to compile. The
repo uses **xUnit (xunit.v3)**; mirror an existing pure-decision test for shape (e.g.
`tests/Guardrails.Core.Tests/GateClassifierTests.cs`). `RunOutcomePolicy` is a PURE function over a run's
recorded decisions — it runs no prompt, touches no disk; it is the ideal unit-test base.

**Anchor on these REAL shipped types (grep them — durable markers, not line numbers):**
- `DecisionEntry` (`src/Guardrails.Core/Execution/DecisionEntry.cs`) — the `decisions[]` entry. Its
  `Decision` string field holds the token.
- `DecisionTokens` (same file) — the token constants. **`DecisionTokens.ProceededBestGuess`**
  (`"proceeded-best-guess"`) and **`DecisionTokens.ProceededUnreviewed`** (`"proceeded-unreviewed"`)
  ALREADY exist (wave 3 + the wave-2 review-gate model shipped them). Other tokens present:
  `Escalated`, `AnswerInjected`, `BlockerRetried`, `Halted`, `AutoApplied`.

Write two artifacts (both in scope):

1. **The test file** `tests/Guardrails.Core.Tests/RunOutcomePolicyTests.cs` — tests that must FAIL against
   the stub. Assert, over an `IReadOnlyList<DecisionEntry>` (or `IEnumerable<DecisionEntry>`) the policy is
   given, EACH of these load-bearing cases (doc 12 §1 hard rule + §5.2 + §7.1):
   - **`SuppressesDelivery` is TRUE when a `proceeded-best-guess` decision was recorded** (a best-guess
     shaped the result ⇒ machine-decided work is never auto-delivered — `mergeOnSuccess` OFF).
   - **`SuppressesDelivery` is TRUE when a `proceeded-unreviewed` decision was recorded** (Option P).
   - **`SuppressesDelivery` is FALSE when NO machine decision was recorded** — a run whose only decisions
     are ordinary (`escalated` / `halted` / none at all) delivers normally. This is the load-bearing
     negative case — do NOT drop it.
   - **`ProceededUnreviewedWaveCount` counts the `proceeded-unreviewed` decisions** (0 when none; N for N
     distinct unreviewed waves) — the "ran with N unreviewed waves" flag + the distinct-exit trigger.
   - A run that best-guessed but was NOT unreviewed ⇒ `SuppressesDelivery == true` **AND**
     `ProceededUnreviewedWaveCount == 0` (best-guess suppresses delivery but is NOT the distinct-exit case).

2. **The minimal stub**: `src/Guardrails.Core/Execution/RunOutcomePolicy.cs` — a NEW `public static class
   RunOutcomePolicy` with `public static bool SuppressesDelivery(IEnumerable<DecisionEntry> decisions)` and
   `public static int ProceededUnreviewedWaveCount(IEnumerable<DecisionEntry> decisions)` as
   THROWING/`default`-returning stubs so the tests COMPILE but FAIL (TDD red). Do NOT implement the real
   logic. (You may adjust the exact signatures if the shipped `DecisionEntry`/`RunJournal` shape makes a
   cleaner seam obvious — keep the two named members `SuppressesDelivery` and `ProceededUnreviewedWaveCount`
   so the downstream guardrails and wiring tasks match.)

   The tests MUST COMPILE and FAIL (not compiling is a mistake).

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~RunOutcomePolicyTests"`. Do NOT run
the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop
`outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/RunOutcomePolicyTests.cs` and
`src/Guardrails.Core/Execution/RunOutcomePolicy.cs`. After this task the harness runs a `git diff` check
and rejects any edit outside these paths — including `DecisionEntry.cs` / `RunJournal.cs`. An out-of-scope
edit fails the task immediately and consumes a retry. If a shipped type is missing a member you need, do
NOT edit it — write `{"needsHuman": "<what is missing>"}` and stop.
