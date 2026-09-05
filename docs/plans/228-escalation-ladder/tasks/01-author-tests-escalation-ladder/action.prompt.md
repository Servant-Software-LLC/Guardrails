## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `01-author-tests-escalation-ladder`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-author-tests-escalation-ladder": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "01-author-tests-escalation-ladder": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author the FAILING unit tests for the **escalation ladder** — the pure rung-selection function that
turns "this attempt failed its guardrails" into "the next attempt resolves one rung stronger" (#228) —
together with the minimal stubs they compile against.

**Write only these three files:**

1. `tests/Guardrails.Core.Tests/Escalation/EscalationLadderTests.cs` — the test file. Class name
   **`EscalationLadderTests`**, in namespace `Guardrails.Core.Tests.Escalation`, carrying the class-level
   attribute `[Trait("Category", "EscalationLadder")]`. Both are pinned: this plan's guardrails select
   this pair with `--filter 'Category=EscalationLadder&FullyQualifiedName~EscalationLadderTests'`, so a
   different class name or trait makes the guardrails select nothing.
   **Do NOT use `[Trait("Category", "TierResolution")]`** — that trait already exists in this project and
   belongs to the shipped tier-resolution suite; reusing it would sweep this plan's deliberately-red
   tests into the baseline preflight's exclusion set and into other tasks' filters.
2. `src/Guardrails.Core/Prompts/EscalationLadder.cs` — the minimal stub, so the test project COMPILES:

   ```csharp
   using Guardrails.Core.Model;

   namespace Guardrails.Core.Prompts;

   public static class EscalationLadder
   {
       public static string? NextRung(string? servedRung) => throw new NotImplementedException();

       public static TierResolution Apply(RunConfig config, TierResolution route, int escalations) =>
           throw new NotImplementedException();
   }
   ```

   Both members throw. Do NOT implement either — task `02-implement-escalation-ladder` fills them in.
   You MAY add XML doc comments; do not change the signatures (the tests and the next task's guardrail
   both bind to them).
3. `src/Guardrails.Core/Prompts/TierResolution.cs` — add ONE new member to the existing
   `TierResolution` record, and change nothing else in that file:

   ```csharp
   public string? EscalatedFrom { get; init; }
   ```

   Non-null means *"a previous attempt of this task failed its guardrails, so this attempt was moved up
   the ladder"*, and its value is the rung the FIRST (un-escalated) resolution served. Null means no
   escalation. Give it an XML doc comment that says so, and that says plainly what it is **not**:

   > **`EscalatedFrom` is NOT `Climbed`.** `Climbed` is TRUE when `Candidates(RequestedTier)` was EMPTY
   > and the resolver walked to a stronger rung inside ONE attempt — a CAPABILITY fact ("no configured
   > runner serves the requested rung"). `EscalatedFrom` is a different reason to be on a higher rung:
   > a previous attempt of this task FAILED ITS GUARDRAILS. The two are independent — an escalated
   > attempt may also climb — and they must stay separately readable in the journal and the report.

   Do NOT widen, reuse, or re-document `Climbed`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Escalation/EscalationLadderTests.cs`,
`src/Guardrails.Core/Prompts/EscalationLadder.cs` and
`src/Guardrails.Core/Prompts/TierResolution.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including changes to other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### The contract the tests must encode

Read `src/Guardrails.Core/Prompts/TierResolver.cs` and `src/Guardrails.Core/Model/TieringConfig.cs`
before writing anything. Two facts about the tree you should CONFIRM rather than take on trust — run
the commands, and if they disagree with this prompt, trust the tree and say so in your summary:

- `grep -n "public static IReadOnlyList<string> All" src/Guardrails.Core/Model/TieringConfig.cs` —
  at authoring time `ActionTiers.All` is `[easy, medium, hard]`, **ascending by difficulty**.
- `grep -c "SelectCandidate" src/Guardrails.Core/Prompts/TierResolver.cs` — at authoring time this
  returned **8**, all inside that one file: `TierResolver.SelectCandidate(RunConfig, string)` is the
  ONE candidate selector, and it already walks *rungs at or above* the requested one, returning a
  `TierResolution` (or one with `NoRoute = true`). **The ladder CALLS it. Do not write, and do not
  test for, a second resolution algorithm.**

`NextRung(servedRung)` is the pure ladder step: the rung immediately ABOVE `servedRung` on
`ActionTiers.All`, or `null` when there is none — `servedRung` is the top rung, is null, or is not on
the ladder at all.

`Apply(config, route, escalations)` is what the retry loop will call. `escalations` is the number of
guardrail-failed attempts this task has already had. Its rules, in order:

- `escalations <= 0` ⇒ return `route` **unchanged** (the first attempt of every task, and every task in
  a plan that never fails a guardrail — this is the byte-identical case).
- `route` resolved **no rung** — it is `Pinned`, it is `Legacy`, it `NoRoute`s, or its `Tier` is null ⇒
  return `route` **unchanged**. There is no rung to climb from, and a pin is a human's assignment that
  the harness does not override.
- Otherwise climb, once per escalation: take `NextRung` of the rung currently SERVED, resolve it with
  `TierResolver.SelectCandidate`, and if that resolution has a runner, that is the new current
  resolution. If `NextRung` is null (already at the top) or the resolution `NoRoute`s (nothing at or
  above that rung serves), **stop and keep what you have** — never error, never invent a route.
  (`SelectCandidate` already keeps climbing internally past a rung with no candidate, so "if the rung
  no-routes, keep climbing" is its job, not a second loop here.)
- The returned resolution carries `EscalatedFrom` = **the rung the ORIGINAL `route` served**, set once
  and preserved across every step, so `escalated from easy to hard on attempt 3` is readable off one
  record. If NO step succeeded — the ladder was capped immediately — return the original `route` with
  `EscalatedFrom` still **null**: nothing escalated, so nothing may claim it did.

### The behaviours, and the EXACT test method name each must carry

The guardrail `02-tests-fail-on-stubs.ps1` is a per-test red census (#375): it reads the runner's own
TRX and requires each name below to be present and observed `Failed`. Use these names verbatim.

| behaviour | test method name |
|---|---|
| `easy` is one rung below `medium` | `NextRung_FromEasy_IsMedium` |
| `medium` is one rung below `hard` | `NextRung_FromMedium_IsHard` |
| `hard` is the top rung — nothing above it | `NextRung_FromHard_IsNull` |
| a rung not on the ladder (and null) has no successor | `NextRung_FromAnUnrecognizedRung_IsNull` |
| zero guardrail failures changes nothing | `Apply_WithNoGuardrailFailures_ReturnsTheRouteUnchanged` |
| one guardrail failure serves one rung stronger | `Apply_AfterOneGuardrailFailure_ServesOneRungStronger` |
| the record names the rung it started from | `Apply_AfterOneGuardrailFailure_RecordsTheOriginalRungInEscalatedFrom` |
| already on the strongest REGISTERED rung: stay put, no error, not marked escalated | `Apply_OnTheStrongestRegisteredRung_StaysPutAndIsNotMarkedEscalated` |
| a single-runner config has nowhere to climb: today's behaviour, silently | `Apply_OnASingleRunnerLegacyConfig_ReturnsTodaysResolutionUnchanged` |
| the next rung has no candidate, a stronger one does: keep climbing | `Apply_WhenTheNextRungHasNoCandidate_KeepsClimbingToOneThatServes` |
| nothing at or above the next rung routes: stay put | `Apply_WhenNoRungAtOrAboveRoutes_StaysPut` |
| a pinned route is never escalated | `Apply_OnAPinnedRoute_ReturnsItUnchanged` |
| two guardrail failures climb two rungs, `EscalatedFrom` still names the original | `Apply_AcrossTwoGuardrailFailuresClimbsTwoRungsAndKeepsTheOriginalEscalatedFrom` |

Three of those thirteen are the **cap-and-degrade** cases, and they are the silent-failure surface of
this whole feature — write them as real assertions over real `RunConfig` registries, not as smoke:

- **strongest registered rung** — a registry whose blocks serve only up to `medium`, a route already
  served at `medium`: the result must be the same route, `EscalatedFrom` null, and no exception.
- **single-runner config** — one `promptRunners` block with **no `routing` block at all**, so the route
  is the LEGACY one (`Legacy = true`, `Tier` null). This is every plan in existence today; the result
  must be byte-equal to the input route. A regression here breaks everyone.
- **`NoRoute` at the higher rung** — a registry serving `easy` and `hard` but not `medium`: escalating
  from `easy` must land on `hard`. And a registry serving only `easy`: escalating from `easy` must stay
  at `easy` with `EscalatedFrom` null.

Build the `RunConfig` registries the way the existing tests in
`tests/Guardrails.Core.Tests/ModelTiering/TierResolverCandidateSelectionTests.cs` do — read that file
first and reuse its helpers/shape rather than inventing a second way to spell a registry.

**The tests MUST COMPILE and FAIL.** Failing is the point — the stubs throw
`NotImplementedException`. NOT compiling is a mistake to fix. Do NOT implement the ladder.
