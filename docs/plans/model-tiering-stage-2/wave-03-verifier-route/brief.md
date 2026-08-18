# Wave 3 — the verifier route (§6.5 / §6.5.1 / D24 / D27 / D29)

**This wave is NOT yet broken down.** It is the JIT stub that keeps the one-ahead invariant (#365)
visible. Author it with `/plan-breakdown` **against the materialized wave-2 tree**, not from this
brief alone — every type named below exists on the plan branch now, so read the real signatures
rather than trusting a paraphrase written before they landed.

> **Why this wave exists, and why it was nearly lost.** The charter question `s2-verifier-scope`
> asked whether §6.5 ships inside Stage 2 or splits into its own stage. The maintainer answered
> **"Include §6.5 in Stage 2."** The wave-2 JIT breakdown was instructed to create this stub and
> **truncated before it did** (#385), so the plan silently became a two-wave plan. Both waves then
> ran fully green — 20 tasks, $115.32, every wave gate green, the whole test suite green, the
> conformance suite 9/9 — and the **plan terminal gate refused to certify**, on exactly one clause:
> `6.5/D29`. That is the gate doing its job (#477 asks for the invariant to be enforced earlier).
> **Wave 3 is what makes that clause go green, and nothing else in the plan will.**

## The failure this wave prevents

The model-layer analogue of #382 (passing-but-blind): if a task runs on a local 32B and its **judge**
guardrail runs on the same local 32B, a plausible-but-wrong implementation and a plausible-but-wrong
"looks good to me" can agree, and the run goes green over broken work.

**Scope, stated first because it bounds everything else.** This governs ONLY the layer where a model
renders the verdict — prompt-judge guardrails, a terminal `<plan>/guardrails/` phase carrying a
judge, and (charter Decision 5) the autonomous review-gate (#361) and overwatcher (#269).
**Deterministic guardrails run no model and are untouched.** Tiering does not weaken
deterministic-first; it hardens the one place a model's opinion is load-bearing.

## Upstream artifacts this wave builds on — all MATERIALIZED, read them

Wave 2 shipped the resolver and the seams. Do not re-derive any of it.

| surface | where | note |
|---|---|---|
| `TierResolver.SelectCandidate(RunConfig, string tier)` | `src/Guardrails.Core/Prompts/TierResolver.cs:46` | §6.2 candidate selection |
| `TierResolver.Resolve(ActionDefinition, RunConfig, string?)` | same, `:123` | §6.1 precedence — **the judge resolves in the SAME resolver** |
| `PromptRunnerConfig.ServesTier` / `.DeclaresTier` | `Model/PromptRunnerConfig.cs` | **the ONE candidacy predicate (D22a)** — the judge path must CALL it, never re-implement it |
| `.Strength` (`int?`), `.Specialization`, `.Costly`, `.Routing`, `.Kind` | `Model/PromptRunnerConfig.cs:26-68` | every input the judge rules need |
| `TieringVerifierConfig.MinTier` | `Model/TieringConfig.cs:47-59` | **the floor's config shape already exists and is unread** — wave 3 is its first consumer |
| `ActionTiers.All` (`easy`,`medium`,`hard`) | `Model/TieringConfig.cs:67-79` | canonical ascending rung order |
| `Stage2PlanHarness` | `tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs:62` | the real-seam host: drives the REAL `PlanLoader`/`TaskExecutor`/`Scheduler`, fakes only `IPromptRunner`. **Extend it; do not build a second host** |
| `Stage2ConformanceTests` (9 facts, green) | same folder | wave 3 **EXTENDS this class** — the plan terminal gate matches its test NAMES |

**Not yet landed, and therefore this wave's job:** the **`judge { runner, kind, model, effort, tier,
strength, bumped }`** provenance object (DoR §12.4). Wave 2's journal task deliberately did not claim
it — `JournalModel.cs` has no `Judge` member today. Verify that before authoring.

## The contract to implement (DoR §6.5 wins over this summary)

A judge guardrail resolves its own (provider, model, effort) **at attempt launch, alongside the
actor, in the same `TierResolver`**:

1. **Explicit wins.** A judge's frontmatter `tier` (SSOT §4.2) or `runner` pin resolves like an
   action's (§6.1). No rule below applies.
2. **Otherwise the judge's rung = the actor's effective RUNG** — not the actor's *strength*, because
   rung is what `routing.tiers` is expressed in.
3. **The bump is in STRENGTH, never in TIER (D24a).** When the actor is *weak*, the judge is the
   **weakest candidate at the actor's rung whose `strength` is strictly greater than the actor's**.
   Bumping the *tier* would mean "pretend the work is harder", contradicting the difficulty≠strength
   split and dragging the judge into a rung nobody declared for this work. If no such candidate
   exists, see (5).
4. **"Weak" = `strength` when declared; otherwise the provider-kind fallback** — verifier-only, and
   reads `kind != "claude"` ⇒ weak-unless-declared. **Equal-and-strong needs NO bump** (Opus judging
   Opus is a real check); **equal-and-weak DOES** (one blind spot talking to itself).
5. **It degrades; it never overspends.** The bump obeys the costly floor (§6.2): if the only stronger
   block is `costly: true`, **the judge stays at the actor's route and the #229 advisory fires — the
   run proceeds.** Note the deliberate asymmetry the actor side already implements: the **actor does
   NOT degrade, it halts** (`no-route`). Same situation, opposite response — degrade what is
   advisory, halt what is load-bearing.
   - **D29 carve-out.** When the **actor** runs on an explicitly **pinned** `costly` model (§6.1
     item 1), costly spend for that task is already human-authorized, so the judge **may** bump into
     a `costly` block with no halt and no prompt. The floor constrains *the harness choosing*, never
     *the human assigning*. **The `default` pointer does NOT trigger this** — it is a plan-wide
     fallback, not a decision about this task. Absent such a pin, rule 5 stands exactly as written.
6. **Specialization breaks ties, and ONLY ties.** Among candidates already meeting the required
   strength, prefer `planning-reasoning`, then fall back to §6.2 ascending-strength order. It can
   neither satisfy nor violate ≥, and a mismatch is never a finding.
7. **`guardrailOverrides` compose with the resolved JUDGE block, not the actor's.** Resolve the
   judge's route first; the overrides that then apply are **that block's**, since overrides are a
   per-block verdict profile (permissions/tools/turns). Applying the actor block's overrides to a
   judge running on a different block would silently mis-profile every bumped judge. *An implementer
   will get this backwards unless the tests pin it.*

### §6.5.1 — the verifier floor is a FLOOR, not a default (D27)

`tiering.verifier.minTier` **never selects**; it only refuses a result that came out too low.

| | a default (rejected) | a floor (settled) |
|---|---|---|
| chooses the judge's tier? | yes | **never** — rules 2–3 still choose |
| when does it act? | always | **only** when the result came out *below* it |
| can it lower a judge? | yes | **no — it only ever raises** |

Applied **after** steps 1–3: if the rung from (2)–(3) is below `minTier`, raise to `minTier` and
re-select from `Candidates(minTier)`. A result at or above `minTier` is untouched. The costly floor
still applies to every selection; specialization breaks remaining ties.

It is reachable in static v1 because the judge's tier **varies across tasks** (it tracks each task's
actor), even though it cannot move across attempts of one task: *"never verify anything with less
than a medium judge, however trivial the task looked"* is a real policy in a purely static run.

### Surfacing is ADVISORY at BOTH boundaries, and both are v1

A judge weaker than its actor, or **equal-and-weak**, is surfaced as a **#229 review finding**, a
**startup preflight warning line**, and a **per-attempt JIT re-check**. **Never a hard error, never a
load-time refusal, never a halt — attended or unattended.** The harness does not block on a
model-quality opinion.

**De-duplication ruling — implement it; it is the reason three surfaces are tolerable.** The
preflight emits **one pre-run summary line per affected task**; the JIT re-check records
`judge.advisory` in that attempt's provenance **always**, but emits a **log line only when the
observed pair differs from what the preflight predicted**. The run summary aggregates from
provenance, so nothing is lost by the quieter log. Three surfaces each shouting the same condition is
how an advisory trains people to ignore it.

**Why the JIT half is not redundant with the preflight** (an earlier DoR draft proposed deferring it
and was overruled): the preflight is a **model** of the resolver; the JIT check **is** the resolver.
Agreement between them is the point — a disagreement is by definition a resolver bug no preflight
could catch. It is also the only boundary that sees a mid-run mutation (an edited `guardrails.json`
on resume, an overwatcher action change, a hand-edit between waves), and it costs almost nothing
because §9.3 already requires the `judge {...}` provenance per attempt.

## What the terminal gate is waiting for

The plan's `guardrails/03-dor-section-6-contract-landed.ps1` behaviour manifest has **one unsatisfied
clause**, and this wave owes it:

```
6.5/D29  the judge resolves through the SAME resolver (strength bump; pinned-costly actor)
         pattern: (?i)judge|verifier|strengthbump|mintier|pinnedactor
```

It matches against **discovered test names** in `Stage2ConformanceTests`, so the clause goes green
when this wave lands a named test there — no edit to the gate required. **That ratchet is the design;
do not weaken the gate to make the wave pass.** Wave 2's `02-stage2-conformance-green` gate also
asserts a floor of **9** executed conformance facts; wave 3 raises the real count, so re-check that
floor when authoring rather than letting it silently under-assert.

## Authoring notes

- **Read `docs/plans/17-model-tiering.md` §6.5, §6.5.1 and the D24/D27/D29 registry entries** — they
  are the design of record and win over this brief. `docs/plans/model-tiering-verifier.charter.md`
  owns the rationale and the review record (10 numbered Decisions); the DoR owns the contract.
- **The judge must resolve through `TierResolver` and consume `ServesTier`** — a second candidacy
  implementation is the D22a violation the whole design exists to avoid, and wave 1 already has a
  property test asserting agreement.
- **Extend `Stage2ConformanceTests` and `Stage2PlanHarness`.** The harness is forbidden from calling
  `TierResolver` itself (it observes through the journal and the captured invocation), and that
  prohibition holds for anything wave 3 adds.
- **Trace every datum's real path before writing a `writeScope` (#474).** Wave 2 lost a task to a
  scope that could not reach its own deliverable: the journaler reads an `ActionRun`, which lacked
  the member. The reliable trick is to find the nearest existing sibling datum that already makes the
  whole trip — for judge provenance that is `provenance.tierSource` — and cover every file it passes
  through.
- **Before you finish: restore the one-ahead invariant (#365).** If any wave remains after this one,
  create and seed its stub. If wave 3 is the LAST wave, **say so explicitly in the breakdown report**
  — "no successor stub; wave 3 is terminal" — so the next reader can tell a finished plan from a
  truncated one. That ambiguity is exactly what cost this plan a full run (#477).
