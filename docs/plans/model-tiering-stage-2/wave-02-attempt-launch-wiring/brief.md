# Wave 2 — attempt-launch wiring

> **JIT stub.** This wave is declared but not yet broken down. Its tasks reference the *actual*
> `TierResolver` API surface wave 1 produces — signatures, the `TierResolution` shape, where the
> no-route condition surfaces — none of which exist yet. Authoring them now would mean guessing, which
> is the failure the durable-marker rule (#203) patches over after the fact. Instead the harness
> auto-fires `/plan-breakdown` against this brief **and the materialized integration worktree** when
> the run reaches this checkpoint, then halts for `/guardrails-review`.

## Intent

Make the resolver **real**: replace today's two-level model fallback with a call to the wave-1
`TierResolver`, so a tagged task actually routes.

Source of record: `docs/plans/17-model-tiering.md` **§6.1, §6.2, §6.3, §9.3, §12.4, §12.5**, plus
`docs/plans/model-tiering-stage-2.charter.md` sections B, C and E. **The DoR wins over the charter
where they differ.**

## What this wave must accomplish

- **Resolution runs immediately before EVERY attempt, including retries** — not once per task. In
  static v1 the resolver is a pure function of *(tag + registry)* and returns the same block each
  time, but **neither input is frozen for the life of a run**: a resumed run whose `guardrails.json`
  was edited between sessions, an overwatcher-applied change (#269), or a human hand-edit between
  waves all move an input mid-run. Resolving once per task would silently serve a stale route.
- **Provenance (§9.3 / §12.4)** — record `runner`, `kind`, `tier` and **`tierSource`**
  (`task | plan-default | override`) per attempt, plus the resolved model/effort in the attempt log
  header, extending the per-attempt model logging from #198.

  :white_check_mark: **`tierSource` IS computable — wave 1 restored its input; do not re-derive it.**
  The blocker this brief originally carried (the loader collapsing the two sources at
  `Tier = rawAction?.Tier ?? defaultTier`, with `ActionDefinition` keeping no provenance) was fixed in
  wave 1 by the pair `05-author-tests-tier-provenance` / `06-implement-tier-provenance`. Read
  **`ActionDefinition.TierOrigin`** (`None` / `Task` / `PlanDefault`) and map it:

  | `TierOrigin` | plus | journal `tierSource` | `provenance.tier` |
  |---|---|---|---|
  | `Task` | — | `"task"` | the rung |
  | `PlanDefault` | — | `"plan-default"` | the rung |
  | *(any)* | a full `action.runner`/`action.model` pin | `"override"` (D31) | **absent** — no rung resolved |
  | `None` | — | **absent** — legacy fallback (D30) | absent |

  **Do NOT reconstruct the origin by comparing `Tier` to `tiering.defaultTier`.** That is
  `PlanValidator.cs`'s shipped workaround and it is wrong exactly when a task's own tier equals the
  default — the common case. Wave 1 has a test pinned on that case
  (`ActionTier_SameTokenAsDefault_OriginIsStillTask`); re-deriving here would reintroduce the bug
  behind a green wave-1 gate.

  **`override` now has a producing rule** — DoR revision 5, D31: a full pin is its producer, and
  "bypasses tier resolution entirely" (§6.1 item 1) governs what is *selected*, not what is *logged*.
  Every v1 enum value has exactly one producer; §12.4 carries the table.
- **The `no-route` outcome** — resolution finds zero candidate blocks at runtime for a used rung (a
  config gap GR2048 should have caught). It settles **needs-human** with an actionable message naming
  the rung, not a silent fallback.

  **D30 draws the line, and it moved in DoR revision 5.** Legacy fallback and `no-route` used to both
  claim "an effective tier exists but no block serves it" — §6.1 item 3 said fall back, §6.2/D26 said
  halt. Severed in favour of the halt: **legacy is the no-rung path, `no-route` is the no-candidate
  path, and nothing is both.** So once an effective tier exists, this wave must never route to
  `promptRunners.<name>.model` — climb (§6.2), then `no-route`. Wave 1 pins the boundary with a test;
  breaking it here fails the wave-1 tests on the merged tree, not just this wave's own.
- **§6.3 — connection-level failure classification.** A DNS failure / connection refused / TLS
  timeout / missing CLI at launch is `Transient`/*unavailable* and routes to the **shipped #115
  transient-pause machinery** — no budget consumption, existing bounded backoff. The DoR hands this
  wave an explicit open question: **does the shipped `PromptFailureKind` quarantine already catch a
  bare DNS/refused shape, or does it need an additive `Unavailable` classification?** Answer it in
  this wave; no new probe enum is introduced in v1.
- **D28 — a binding costly ceiling must be LOUD** *(charter review; now DoR §6.2)*. When a stronger
  block was excluded **only** because it is `costly: true` and the task goes to a re-attempt, log a
  strong warning naming the block that could not be picked. **Wave 1's `TierResolution` carries this
  datum** — read it from the result; do NOT re-test `Costly` here, which would duplicate the
  candidacy predicate (D22a) and trip wave 1's own guardrails.
- **The climb's loud log line (§6.2)** — a climb to a stronger rung is recorded in provenance **and**
  logged, not silently absorbed.
- **#230-lite — the per-tier spend line, with the Invariant 7 suppression rule.** Aggregate cost and
  tokens per tier from provenance. **§9.3's rule is stricter than "add a per-tier section": on a
  tiering-INACTIVE run the summary prints exactly today's cost line — no per-tier section and no
  `untiered:` bucket.** A naive aggregator that emits an empty or `untiered:` section on every
  existing user's run is the single most likely way this wave breaks Invariant 7.
- **§12.4 / §12.5 SSOT edits, in the SAME change as the code (Invariant 4).** The attempt `outcome`
  enum gains `no-route`; `provenance` gains `runner`/`kind`/`tier`/`tierSource`; the attempt record
  gains optional `usage { inputTokens, outputTokens }` and the `judge {…}` object; §12.5 adds the §9
  seam note and §9.6's v1 normative content. **Some task in this wave must own
  `docs/plans/02-schemas-and-contracts.md` in its `writeScope`.** The terminal gate asserts this.

## Upstream artifacts this wave builds on

From wave 1, on the integration worktree — **read the real signatures, do not assume these names**:

- `src/Guardrails.Core/Prompts/TierResolver.cs` — `SelectCandidate(...)` (§6.2 selection) and
  `Resolve(...)` (§6.1 precedence).
- `src/Guardrails.Core/Prompts/TierResolution.cs` — the result record: the selected block, model,
  effort, the rung served, whether the resolver **climbed**, the **D28 binding-ceiling datum**, and
  the **`tierSource`**.
- `PromptRunnerConfig.ServesTier` / `DeclaresTier` — the shared candidacy predicate (Stage 1).
  **Wave 1's resolver legitimately calls `DeclaresTier` internally**: the D28 binding-ceiling datum is
  definitionally `DeclaresTier ∧ ¬ServesTier`, and the harness computes exactly that in GR2048
  (`r.Costly is true && atOrAbove.Any(r.DeclaresTier)`, `PlanValidator.cs`). **Wave 2 must not
  re-derive the ceiling** — read the datum off `TierResolution`. An earlier draft of this brief said
  "the resolver must never call `DeclaresTier`" flatly; that was wrong, and a wave-2 guardrail
  enforcing it would red already-merged, correct wave-1 code.

## The production path to replace (durable markers — grep, do not trust a line number)

- **`PromptExecutionSupport.ResolveModelForDisplay`** — declared in
  `src/Guardrails.Core/Execution/PromptExecutionSupport.cs`, called from
  `src/Guardrails.Core/Execution/TaskExecutor.cs`. **Both files are likely in `writeScope`** — an
  earlier draft of this brief named only `TaskExecutor`, which would have omitted the file where the
  function actually lives.
- **`tests/Guardrails.Core.Tests/PromptExecutionSupportModelTests.cs`** pins today's two-level
  precedence — i.e. Invariant 7's shipped guard. Expect it to need **deliberate** attention, not
  incidental breakage. Verify this is still the shape before relying on it: this reflects the
  authoring-time state, before wave 1 had actually run.

## The real-seam conformance suite — a REQUIRED deliverable of this wave

The plan-root terminal gate (`<plan>/guardrails/03-dor-section-6-contract-landed.ps1`) no longer tries
to prove behaviour by grepping `src/`. That design was measured and failed: against a tree holding only
wave 1's `TierResolution` record and **no wiring at all**, 10 of its 14 grep clauses went green — a
`bool NoRoute` property satisfied "the no-route outcome exists". A grep cannot tell "a property with
this name exists" from "this value is written to per-attempt provenance".

So the gate now discovers **`tests/Guardrails.Integration.Tests/…/Stage2ConformanceTests.cs`** with
`--list-tests` and requires **one named test per required behaviour**, then requires them to pass.
A bare count was tried first and measured gameable — `dotnet test` counts **theory data rows**, so one
`[Theory]` with six `[InlineData]` rows cleared an "≥6 executed" floor while proving one behaviour.

**Name each test after what it proves**; the gate matches these markers (case-insensitive):

| behaviour | name must match | owed by |
|---|---|---|
| resolution per ATTEMPT → provenance | `per-attempt` / `attempt…provenance` | wave 2 |
| no candidate ⇒ no-route / needs-human | `no-route` / `needsHuman` | wave 2 |
| D28 binding ceiling surfaced | `ceiling` / `excludedOnly` / `boundByCost`… | wave 2 |
| D22a candidate set agrees with `ServesTier` | `ServesTier` / `agree` / `candidacy` | wave 2 |
| Invariant 7 (routing-enabled, zero-tag ⇒ legacy) | `invariant7` / `routingEnabled` / `zeroTag` | wave 2 |
| §6.5/D29 judge resolves through the same resolver | `judge` / `verifier` / `strengthBump` / `minTier` | **wave 3** |

This is what makes the gate ratchet: **wave 3 lands a judge test and its clause goes green by itself**
— nobody has to remember to edit a shell script. Equally, a run that reaches the terminal gate with
the verifier route unbuilt fails that clause and `mergeOnSuccess` does not deliver.

It is a **real-seam test (#382)**: drive the ACTUAL attempt-launch path. Fake the process/CLI boundary
underneath if you must, **never the in-process seam itself** — a test that injects the resolver proves
the resolver, not the wiring. Minimum coverage this wave owes:

- resolution runs per **ATTEMPT**, not once per task (assert two attempts of one task each resolve);
- the resolved route reaches **per-attempt provenance**, including `tierSource`;
- a rung whose only capable block is `costly: true` settles **no-route / needs-human** and never
  selects the costly block;
- a **binding costly ceiling** emits the D28 warning on re-attempt;
- **Invariant 7**: a routing-**ENABLED** config with a zero-tag plan resolves via the legacy path with
  **zero** tier-resolution activity, and the existing golden plans run byte-identically.

Wave 3 **extends the same class** for §6.5 (the judge resolves through the same resolver; the STRENGTH
bump; D29). Extending an existing suite is what keeps the gate honest without anyone remembering to
edit a shell script.

## Notes for whoever breaks this down

- This is **composition-root wiring (#120)**: the failure mode is that nothing constructs the
  component on the production path. The wiring needs its own named task and a guardrail that drives
  the **real** attempt-launch path — never one that injects the resolver itself, which would pass
  even unwired.
- Expect the **#378 fan-in-sink trigger** to fire: the executor call site, the log header, the
  provenance write and the run-report aggregation are separate integration points. **Split per
  collaborator** rather than emitting one over-scoped sink — which makes this wave **multi-leaf, so
  GR2028 applies per wave**: this wave needs its own `guardrails/` EXIT gate carrying ≥1 real
  integration re-run (a union-safe conditional invariant, or a build/suite), and a positive
  `preflights/` ENTRY gate asserting wave 1's artifacts materialized.
- The `tests/Guardrails.Integration.Tests` area baseline **already exists** at the plan root
  (`preflights/02-baseline-integration-tests-green.ps1`) — do not add a second one for that area
  (#181 is deduped one-per-area). Add one only for a *new* area this wave touches.
- **§12.5's §9.6 verifier content belongs to WAVE 3, not here.** §12.5 folds in the verifier route,
  its strength bump and its advisory degradation — documenting those in the SSOT while their code is a
  wave away inverts Invariant 4 ("in the SAME change as its code"). This wave owns §12.4 plus §12.5's
  §9 `kind`/`FromConfig` seam note; leave the verifier half to wave 3.
- **#229 is NOT in this plan, and that is a deferral, not an omission.** DoR §10 lists Stage 2 as
  `#226-static ∥ #229 review check ∥ #230-lite`, and §11 still lists "#229 placement" as an open
  maintainer sign-off. #229 is a `guardrails-review` **skill** change with no code deliverable, so no
  wave's `writeScope` would surface it. Recorded here so it is a visible decision rather than a silent
  drop — the exact failure mode this stage exists to break.
- **Invariant 7 is at its highest risk in THIS wave**, not wave 1 (where the resolver is dead code).
  Carry both halves of its stage acceptance: (a) existing golden plans run byte-identically, and
  (b) the **routing-enabled config + zero-tag plan** fixture — `routing` blocks PRESENT, no tag, no
  `defaultTier` ⇒ legacy resolution with **zero** tier-resolution activity. (b) is the case an
  implementer gets wrong ("routing is configured, so route").
- The turn budget settled in review is **`maxTurns: 80`** for implementation tasks.
- **Tiering stays NOT configured for this plan's own run** — emit no `action.tier`, no `tiering`
  block, no classification report lines. This concerns the HARNESS RUN and discharges nothing about
  Invariant 7 in the shipped code (previous bullet).

## Before you finish: restore the one-ahead invariant (#365)

**Create and seed the next stub, `wave-03-verifier-route/`** (declared dir, empty `tasks/`, contiguous
`NN`, plus a `brief.md`), so exactly one un-authored wave stays visible ahead. Seed its brief from the
charter's section D and DoR **§6.5** (judge resolution rules 1–7; the STRENGTH bump, never a tier
bump; `guardrailOverrides` composing with the resolved **JUDGE** block; the advisory de-duplication
rule across the preflight and JIT boundaries), **§6.5.1** (`tiering.verifier.minTier` is a FLOOR, not
a default), and **D29** (a pinned costly ACTOR licenses a costly judge bump; the `default` pointer
does NOT).

**Do not skip this step.** The charter settled three waves (`s2-waved`), `mergeOnSuccess` defaults ON,
and a run that reaches the terminal gate with the verifier route unbuilt would otherwise **deliver**
two thirds of Stage 2 as done. The terminal conformance gate now asserts §6.5 and D29 as clauses, so
it will fail rather than deliver — but that is the backstop, not the plan.
