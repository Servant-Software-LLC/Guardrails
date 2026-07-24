## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/03-implement-run-outcome-policy` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the **pure `RunOutcomePolicy`** (issue #361 Phase 4) by filling real logic over the stub the
test-author task created, so the authored `RunOutcomePolicyTests` pass. Design of record:
`docs/plans/12-autonomous-mode.md` §1 (the mergeOnSuccess hard rule) + §5.2 (Option P) + §7.1 (exit code).

Fill `src/Guardrails.Core/Execution/RunOutcomePolicy.cs` over the skeleton:
- **`SuppressesDelivery(decisions)`** returns TRUE iff the run recorded **any** decision whose
  `Decision == DecisionTokens.ProceededBestGuess` **OR** `Decision == DecisionTokens.ProceededUnreviewed`.
  (Machine-decided work is never auto-delivered — §1.) Returns FALSE when no such decision exists.
- **`ProceededUnreviewedWaveCount(decisions)`** returns the COUNT of decisions whose
  `Decision == DecisionTokens.ProceededUnreviewed` (0 when none). This is the "ran with N unreviewed waves"
  count and the distinct-exit trigger (§5.2 / §7.1).

Keep it a pure function — no disk, no prompt, no run state beyond the decisions passed in. Use the token
constants `DecisionTokens.ProceededBestGuess` / `DecisionTokens.ProceededUnreviewed` (do NOT hard-code the
string literals — the constants are the SSOT). Do NOT edit the authored tests; make them pass by fixing
the implementation. If the authored tests are genuinely wrong or incompatible, emit
`{"needsHuman": "<why>"}` rather than changing them.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~RunOutcomePolicyTests"`.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/RunOutcomePolicy.cs`. The harness runs a post-action `git diff` check and
rejects any edit outside this path — including the test file (`RunOutcomePolicyTests.cs`) or
`DecisionEntry.cs`. An out-of-scope edit fails the task immediately and consumes a retry. If a compile
error is caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` and stop.

Completion criteria (your guardrail checks this): the authored `RunOutcomePolicyTests` pass.
