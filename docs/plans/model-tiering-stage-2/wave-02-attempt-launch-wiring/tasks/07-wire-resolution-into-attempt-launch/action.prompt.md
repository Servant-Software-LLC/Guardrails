## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/07-wire-resolution-into-attempt-launch`, NOT the
  stableId and NOT the bare folder name. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/07-wire-resolution-into-attempt-launch": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

**This is the composition-root wiring, and it is the point of the whole wave.** Wave 1 built
`TierResolver` and proved it against its own unit tests; nothing in production calls it, so today it
is dead code. Make it real: resolve once immediately before **every** attempt launch — retries
included — and let that ONE resolution decide both what is RECORDED and what actually RUNS.

Make these six `Stage2ConformanceTests` clauses (authored by task 06) pass:

- `Resolution_RunsPerAttempt_AndReachesAttemptProvenance`
- `ResolverCandidacy_AgreesWith_ServesTier_Predicate`
- `Invariant7_RoutingEnabledConfig_ZeroTagPlan_UsesLegacyPath_WithNoTierActivity`
- `D30_TieredPlan_ClimbsToStrongerRung_AndNeverFallsBackToLegacy`
- `D31_FullPin_RecordsTierSourceOverride_WithProvenanceTierAbsent`
- `Climb_ToStrongerRung_IsRecordedInProvenance`

The other three clauses (`NoCandidateAtOrAboveRung_…`, `Reattempt_BoundByCostlyCeiling_…`,
`Climb_ToStrongerRung_EmitsLoudWarningLine`) belong to tasks 08 and 09 and are **expected to stay
red** after this task — your guardrail's filter selects only the six above. Do not implement them
here, and do not edit the suite.

**Do NOT edit `Stage2ConformanceTests.cs` or `Stage2PlanHarness.cs`.** If a clause is genuinely wrong
or incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than changing it — an
out-of-scope edit to a test file fails the write-scope check and burns a retry.

**`docs/plans/17-model-tiering.md` §6.1/§6.2 and §9.3 are the design of record and win over any
paraphrase here.**

### The production path you are replacing (durable markers — grep, do not trust a line number)

- **`PromptExecutionSupport.ResolveModelForDisplay`**, declared in
  `src/Guardrails.Core/Execution/PromptExecutionSupport.cs`. It is the two-level fallback (task
  `action.model` > the runner block's `model` > the `"(cli default)"` display sentinel) the DoR says
  the resolver replaces.
- Its two callers: **`TaskExecutor.ResolveModel`** (called from `BuildProvenance`, which is called at
  the top of `RunAttemptAsync`) and — for the value actually passed to the CLI —
  **`ActionRunner`**'s `PromptExecutionSupport.ApplyModelOverride(settings, task.Action.Model)`.
  Grep for those names; **do not rely on a line number**, and treat this description as the
  authoring-time state — verify it still holds before assuming the same shape.

That pair is exactly the drift risk the shipped doc comments warn about ("so provenance can never
drift from what actually ran"): two code paths that agree only by construction. **After your change
there must be ONE resolution per attempt whose result feeds both.**

### What to build

1. **Resolve at attempt launch.** In `TaskExecutor.RunAttemptAsync`, before the provenance is built,
   call **`TierResolver.Resolve(task.Action, _plan.Config, cliDefaultModel)`** — once, on every
   attempt, retries included. Do **not** hoist it to a per-task computation "because v1 is a pure
   function": neither the tag nor the registry is frozen for the life of a run (a resumed run whose
   `guardrails.json` was edited between sessions moves an input mid-run), and the seam is where the
   v2 dynamic inputs slot in.
   Skip it entirely for a **script** action — no model, no route, and today's `ResolveModel` already
   returns null there.
2. **Feed provenance from that result.** `BuildProvenance` records, on `AttemptProvenance` (the
   members task 02 landed): `Runner` (the resolved block name), `Kind` (that block's `kind` WIRE
   TOKEN via `PromptRunnerKinds.Token`), `Model`, `Effort`, `Tier` (the rung actually SERVED), and
   `TierSource`. Every one is absent-not-null when it does not apply.
3. **`tierSource` is READ, never re-derived (D31).** Map it from the resolution and from
   `ActionDefinition.TierOrigin`, which wave 1 restored precisely so this is possible:

   | condition | `tierSource` | `provenance.tier` |
   |---|---|---|
   | the resolution is a full **pin** (`Pinned`) | `Override` | **absent** — no rung resolved |
   | `TierOrigin.Task` | `Task` | the rung served |
   | `TierOrigin.PlanDefault` | `PlanDefault` | the rung served |
   | the resolution is **legacy** (`Legacy`) / `TierOrigin.None` | **absent** | absent |

   **Do NOT reconstruct the origin by comparing `action.Tier` to `config.Tiering?.DefaultTier`.**
   That is `PlanValidator.cs`'s shipped workaround, and it is wrong exactly when a task's own tier
   equals the plan default — the common case. Wave 1 pins that case with a test
   (`ActionTier_SameTokenAsDefault_OriginIsStillTask`); re-deriving here reintroduces the bug behind
   a green wave-1 gate.
4. **The resolved route must actually REACH the invocation.** Thread the resolution's `Model` (and
   `Effort`, where the runner can express it) down to `ActionRunner` so the prompt invocation is
   built from the RESOLVED route rather than from `task.Action.Model`. Reuse
   `ApplyModelOverride`; just give it the resolved value. A provenance that records a route the
   runner never received is the drift this task exists to remove — and clause 1 asserts the two
   agree per attempt.
   *If the Claude runner has no way to express `effort` on its CLI today, record it in provenance,
   leave the argv alone, and say so in a comment — do not invent a flag.*
5. **Do not re-derive anything the resolution already carries.** `Climbed`, `CostlyCeilingBound` and
   `CostlyCeilingBlocks` ride on the result; read them. Re-testing `Costly` here would duplicate the
   candidacy predicate D22a forbids duplicating, and would trip wave 1's own guardrails.
   This task RECORDS the climb (`Climbed`, and the requested-vs-served rung pair); task 09 LOGS it.
6. **Invariant 7 is at its highest risk in this task.** Two fixtures, and the second is the one
   implementers get wrong:
   **(a)** no tags, no `routing` anywhere ⇒ legacy, byte-identical to today.
   **(b)** `routing` blocks **PRESENT**, the task carrying **no** tier and **no**
   `tiering.defaultTier` ⇒ *still* legacy, with **zero** tier-resolution activity: no
   `provenance.tier`, no `tierSource`, no climb, no ceiling datum. Activation is PLAN-scoped, not
   config-scoped. "Routing is configured, so route" is the wrong reading, and clause 3 asserts (b)
   directly. `TierResolver.Resolve` already gets this right — your job is not to undo it by, say,
   defaulting an untagged action to a rung before calling it.
7. **Once an effective tier exists, resolution OWNS the outcome (D30).** Never fall back to
   `promptRunners.<name>.model` after a tier was resolved: the resolver climbs, and a genuinely empty
   registry at-or-above the rung returns `NoRoute`. **In THIS task**, a `NoRoute` result should not
   be quietly turned into a legacy launch — task 08 settles it properly, so leave the branch
   explicitly unhandled with a `// task 08 settles NoRoute` marker rather than papering over it.

### `PromptExecutionSupportModelTests.cs` — deliberate attention, not incidental breakage

`tests/Guardrails.Core.Tests/PromptExecutionSupportModelTests.cs` pins the two-level precedence you
are replacing, and it is **in your writeScope** so you can own that re-baseline rather than being
trapped by a test you may not edit. It is Invariant 7's shipped guard, so:

- if `ResolveModelForDisplay` survives as a pure helper (the legacy branch's semantics are identical),
  **change nothing** — the cheapest correct answer;
- if you retire it, **MOVE its precedence coverage** to the new seam rather than deleting it: the
  same three cases (`action.model` wins > runner `model` wins > the `"(cli default)"` sentinel) must
  still be asserted somewhere in that file. A guardrail checks the coverage survives.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/TaskExecutor.cs`,
`src/Guardrails.Core/Execution/PromptExecutionSupport.cs`,
`src/Guardrails.Core/Execution/ActionRunner.cs` and
`tests/Guardrails.Core.Tests/PromptExecutionSupportModelTests.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these
paths — including `TierResolver.cs`/`TierResolution.cs` (wave 1 owns them; a change there would red
wave 1's merged tests), the journal model (task 02 owns it), `AttemptJournaler.cs` (task 08), the
conformance suite, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
