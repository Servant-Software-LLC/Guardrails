## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/09-disclose-resolved-route-and-warnings`, NOT the
  stableId and NOT the bare folder name. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/09-disclose-resolved-route-and-warnings": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

The route now decides what runs (task 07) and a dead rung halts honestly (task 08). What is still
missing is the half a **human** consumes: a route change and a cost-bound ceiling must be **visible**,
not merely recorded in a JSON field nobody reads mid-run.

Make these two `Stage2ConformanceTests` clauses pass:

- `Reattempt_BoundByCostlyCeiling_WarnsNamingTheExcludedOnlyForCostBlock`
- `Climb_ToStrongerRung_EmitsLoudWarningLine`

**Do NOT edit `Stage2ConformanceTests.cs` or `Stage2PlanHarness.cs`.** If a clause is genuinely wrong
or incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than changing it.

**`docs/plans/17-model-tiering.md` §6.2 (D28, the climb) and §9.3 are the design of record and win
over any paraphrase here.**

### The surface, already pinned by the conformance suite

**`attempt-route.log`, in the attempt's own log dir** — a sibling of the existing
`attempt-tool-grants.log`. Follow that function's shape exactly (grep for `WriteToolGrantHeader` in
`TaskExecutor.cs`; **do not rely on a line number** — tasks 07 and 08 both edited this file before
you, so any line reference is stale by construction, and treat this description as authoring-time
state you should verify):

- written at attempt launch, from the same resolution task 07 already computed — **resolve once per
  attempt, not again here**;
- **best-effort**: an `IOException`/`UnauthorizedAccessException` while writing a disclosure artifact
  must never fail an attempt, exactly as the tool-grant header does. The machine-readable copy is
  already safe in `attempt-provenance.json`;
- absent for a **script** attempt (no route) and absent for an attempt with no resolution to report.

Contents:

1. A header naming the resolved **runner block**, **model**, **effort**, the **requested** rung, the
   **served** rung, and the **`tierSource`** — the human-readable twin of the provenance object.
2. A line prefixed **`WARNING:`** when the resolver **climbed**, naming BOTH rungs: *asked for
   `<requested>`, served at `<served>`* — §6.2 says a climb is recorded **and** logged, not silently
   absorbed. A route the operator did not ask for and cannot see is a cost and latency change they
   will attribute to the prompt.
3. A line prefixed **`WARNING:`** when the **D28 binding costly ceiling** applies **and the task is
   going to a re-attempt**, NAMING the blocks the harness was not permitted to pick. Without it, a
   failure caused by the weaker model running out of reasoning is indistinguishable from an ordinary
   failure, and the operator tunes prompts against a constraint they cannot see.

### Three rules that are easy to get wrong

- **Read the D28 datum; do not re-derive it.** `TierResolution.CostlyCeilingBound` and
  `CostlyCeilingBlocks` ride on the result task 07 already has. Re-testing `PromptRunnerConfig.Costly`
  here would duplicate the candidacy predicate D22a forbids duplicating, and would trip wave 1's own
  guardrails. A guardrail enforces this.
- **This changes what is LOGGED, never what is SELECTED.** The costly floor is untouched: no
  override, no dial, no new path to a costly model. If you find yourself editing a selection
  decision, stop — you are in the wrong task.
- **"On re-attempt" means attempt ≥ 2.** The first attempt has not failed yet, so a ceiling warning
  there is noise on every single tiered run. The clause drives a task that fails its first attempt
  and asserts the warning on the SECOND.

### Do not disturb the seven clauses tasks 07 and 08 made green

Your guardrail runs the whole suite — all three of you edit `TaskExecutor.cs` in a chain, and you are
last, so this is the first place a chain-wide regression can be caught by a task that still has a
retry budget.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/TaskExecutor.cs` and
`src/Guardrails.Core/Execution/AttemptArtifacts.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including `TierResolver.cs` /
`TierResolution.cs` (wave 1 owns them), the journal model, the conformance suite, or the `.csproj`.
An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error
caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
