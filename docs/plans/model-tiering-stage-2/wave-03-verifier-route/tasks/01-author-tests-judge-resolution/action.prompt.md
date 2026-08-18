## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/01-author-tests-judge-resolution`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/01-author-tests-judge-resolution": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the **failing tests** for **DoR §6.5 judge resolution (rules 1–7)** and **§6.5.1's verifier
floor**, plus the minimal stubs they compile against.

- **`tests/Guardrails.Core.Tests/ModelTiering/JudgeResolutionTests.cs`**
- namespace `Guardrails.Core.Tests.ModelTiering`
- class **`JudgeResolutionTests`** — this exact name; the implementation guardrail and the wave exit
  gate filter on it
- decorated **`[Trait("Category", "TierResolution")]`** at class level (the plan-root baseline
  preflight excludes `Category!=TierResolution`)

**`docs/plans/17-model-tiering.md` §6.5 and §6.5.1 are the design of record and win over this
summary.** Read them before writing a single assertion — the rules below are a checklist, not the
contract.

### The stubs to write (so the tests COMPILE and FAIL)

Wave 2 shipped the actor half. Read it first — `TierResolver.SelectCandidate` / `.Resolve`,
`PromptRunnerConfig.ServesTier` / `.DeclaresTier`, `.Strength`, `.Specialization`, `.Costly`, and
`TierResolution`'s datums. You are extending that file, not starting a new one.

1. **`src/Guardrails.Core/Prompts/TierResolver.cs`** — a `public static` **`ResolveJudge(...)`**
   entry point that **throws `NotImplementedException`**. It must take enough to decide rules 1–7:
   the judge's prompt frontmatter (for rule 1), the **actor's already-computed `TierResolution`**
   (rules 2–4 key off the actor's rung and strength), and the `RunConfig` (the registry and the
   `tiering.verifier.minTier` floor). Design the signature; the tests pin it.
2. **`src/Guardrails.Core/Prompts/TierResolution.cs`** — a **`JudgeResolution`** record carrying what
   §12.4's `judge {...}` object needs: the resolved block/runner name, kind, model, effort, tier,
   strength, and **`Bumped`** (true when the weak-actor strength bump fired). Add whatever datum the
   advisory needs to know a judge came out weak — task 09 consumes it, so make it observable rather
   than re-derivable.
3. **`src/Guardrails.Core/Prompts/PromptFile.cs`** — add an optional **`Tier`** to
   `PromptFrontmatter` and parse it, so rule 1's frontmatter pin is expressible. It is genuinely
   absent today (verify), and SSOT §4.2 is where it lands.

### The behaviours the tests MUST encode

1. **Explicit wins.** A judge's frontmatter `tier` or `runner` pin resolves like an action's (§6.1);
   no later rule applies.
2. **Otherwise the judge's rung = the actor's effective RUNG** — not the actor's *strength*. Assert
   this with an actor whose strength and rung differ, or the two are indistinguishable.
3. **The bump is in STRENGTH, never in TIER (D24a).** A *weak* actor gets the **weakest candidate at
   the actor's rung whose `strength` is strictly greater than the actor's**. Assert the resolved
   judge stays at the actor's **rung** — a test that only checks "the judge is stronger" passes a
   tier bump too, which is the exact error D24a exists to forbid.
4. **"Weak" is `strength` when declared, else the provider-kind fallback** (`kind != "claude"` ⇒
   weak-unless-declared, verifier-only). Assert **equal-and-strong needs NO bump** and
   **equal-and-weak DOES** — one blind spot talking to itself is the failure this whole route exists
   to prevent.
5. **It degrades, never overspends.** When the only stronger block is `costly: true`, the judge
   **stays at the actor's route** and the advisory fires — **the run proceeds**. Assert the
   proceeds-not-halts half explicitly: the actor side halts (`no-route`) in the same situation, and a
   test that only checks "no bump happened" cannot tell degrade from halt.
   - **D29:** when the ACTOR is on an explicitly **pinned** `costly` model, the judge **may** bump
     into a `costly` block. Assert both halves — the pin licenses it, and the `default` pointer does
     **NOT** (a plan-wide fallback is not a decision about this task).
6. **Specialization breaks ties, and ONLY ties.** Among candidates already meeting the required
   strength, `planning-reasoning` wins; otherwise §6.2 ascending-strength order. Assert it cannot
   override the strength requirement — a specialized-but-too-weak block must not be chosen.
7. **The §6.5.1 floor RAISES, never lowers.** `tiering.verifier.minTier` is applied **after** rules
   1–3: a result below it is raised and re-selected from `Candidates(minTier)`; a result **at or
   above** it is **untouched**. Assert the never-lowers half — a plan-wide `easy` floor must not drag
   a `hard` judge down. That asymmetry is the whole distinction between a floor and a default.

### The test METHOD NAMES are PINNED

Your `03-covers-key-behaviors` guardrail runs `dotnet test --list-tests` and looks for each marker in
the DISCOVERED name list — it never reads the file's text, so a behaviour named in a comment earns
nothing and a renamed test reads as a missing one. Add more tests freely; do not rename these.

| behaviour | method name |
|---|---|
| rule 1 | `FrontmatterPin_WinsOutright` |
| rule 2 | `JudgeRung_IsActorsRung_NotActorsStrength` |
| rule 3 / D24a | `WeakActor_StrengthBump_KeepsActorsRung` |
| rule 4 | `EqualAndStrong_NoBump` and `EqualAndWeak_Bumps` |
| rule 5 | `OnlyStrongerBlockCostly_DegradesAndProceeds` |
| D29 | `D29_PinnedCostlyActor_MayBumpIntoCostly` and `D29_DefaultPointer_DoesNotLicenseIt` |
| §6.5.1 | `MinTierFloor_RaisesTooLow` and `MinTierFloor_NeverLowersHigher` |

Tests must **COMPILE and FAIL** — failing is intentional; NOT compiling is a mistake to fix. Do not
implement `ResolveJudge`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/JudgeResolutionTests.cs`,
`src/Guardrails.Core/Prompts/TierResolver.cs`, `src/Guardrails.Core/Prompts/TierResolution.cs` and
`src/Guardrails.Core/Prompts/PromptFile.cs`. After this task completes, the harness runs a `git diff`
check and rejects any edit outside those paths — including `GuardrailRunner.cs` (task 07 owns the
wiring), `JournalModel.cs` (tasks 03/04), or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

**Do not change the ACTOR half.** `SelectCandidate` and `Resolve` are wave 1/2 deliverables with
their own passing tests; extending the file is expected, altering their behaviour is not.
