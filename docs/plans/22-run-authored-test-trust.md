# Architecture: trusting run-authored tests — the per-test red census and the authored-test review halt

> **Status: DRAFT design-of-record. Not implemented.** Per **#106** this document goes out as a
> **draft PR** for inline review before any implementation milestone starts. Issue: **#375**. This
> document does not close it.

---

## What's being asked

In the JIT staged-breakdown flow (SSOT §14.4, `plan-breakdown` §9.5) a wave is authored,
`/guardrails-review`'d, then run. The review sees **structure** — guardrails and prompts. The **tests
are authored by the agent at run time**, so the reviewer cannot see the assertions whose correctness
matters most. For an ordinary wave that is acceptable: the implementation must pass the authored tests,
and a `covers-key-behaviors` floor (#75) nudges the tests toward the enumerated behaviours.

For a **security-sensitive** wave it is not acceptable. The load-bearing invariants are enforced only by
agent-authored tests that no human reviewed before the wave's exit gate trusted them, behind a floor
that is a **naming lower bound** — a comment or a string literal satisfies it.

**Measured, not argued.** On the answer-injection wave of `autonomous-mode-impl`, the review pass proved
by execution that `covers-security-matrix.ps1` exits 0 against a test file that merely *names* every wire
token (`stale`, `replayed`, `runId`, `review-attested`, `proceed-unreviewed`) with hollow `Assert.NotNull`
/ `Assert.True(true)` bodies. The five answer-injection security invariants — reject **stale**,
**replayed/already-consumed**, **wrong-bound**, **review-forged**, **clamped** — were pinned by nothing a
human vetted. A cheapest-wrong implementation **and** cheapest-wrong tests can ship a green wave. In that
dogfood the backstop was human code review before the feature branch merged. A general run has none.

### The ambiguity named, and the narrowing

"The tests are not trustworthy" is three different claims with three different mechanisms, and conflating
them is how this issue produces a wolf:

| reading | the defect | decidable by a machine? |
|---|---|---|
| **(V) vacuous** — the test is named for the behaviour and asserts nothing that can fail | `Assert.True(true)` under `RejectsStaleAnswer` | **yes** — §2 |
| **(W) wrong** — the test asserts a real, falsifiable thing, and it is the *wrong* thing | asserts `RejectedStale` where the invariant is `RejectedReplayed` | **no** — needs a reader who knows the invariant |
| **(M) missing** — the invariant has no test at all, under any name | no test mentions the `{runId,seq,gate,subject}` binding | **partly** — the existing name/token floors, sharpened by (V) |

**This document narrows to: (V) is v1 and deterministic; (M) is the existing floors made honest by (V);
(W) is permanently a human-reading problem and gets a halt, not a check.** Anyone who reads #375 as "add
a stronger regex and the tests become trustworthy" will build the exact class of guardrail #468 measured
as systematically defect-prone. §3 says so with the standing test applied and the answer written out.

---

## Placement

| piece | placement |
|---|---|
| **Probe B operator 21** — the hollow-assertion mutant | **skill** (`guardrails-review`) + catalogue anti-pattern taxonomy |
| **The per-test red census** — strengthen `tests-fail-on-stubs` from suite-exit to per-name observed-FAILED | **skill** (`plan-breakdown` catalogue + `stacks/dotnet.md`). **No harness change, no schema change.** |
| **`reviewAuthoredTests`** task field + the halt + `decisions[]` gate token | **harness** (`PlanValidator`, `Scheduler`, journal, renderers) + **schema** (SSOT §3, new §3.6; §7; §7.2) |
| **GR2064** — the marker on a task with an empty `writeScope` | **harness** (`PlanValidator`) + schema |
| **`sensitivity` as a graded enum** | **rejected** — §4 |
| **A rejection-shaped-assertion regex as a guardrail** | **rejected as a certifier**, retained as a review probe — §3 |
| **Real mutation testing over authored tests** (Stryker.NET or equivalent) | **v2 bet** — §7, with an evidence gate |
| **An adversarial AI test-reviewer that can CLEAR the halt** | **rejected, permanently** — §5.4. A one-way (fail-only) judge is the only sound future shape |

---

## Invariants in play

**1 — Deterministic guardrails over prompt-judges; judges never alone.** This is the invariant #375
strains hardest, because the tempting fix is "have an agent read the tests and pass judgement". The
design refuses that: §2's census is exit-coded and behavioural, and §5's halt is a **stop for a human**,
not a judge that can pass. §5.4 pre-refuses the AI-reviewer substitution and names the only composition
that would be sound (a judge that may fail a gate and may never clear one).

**3 — Prompt-guardrail verdicts come from verdict files, never CLI exit codes.** Untouched. The census is
a *deterministic* guardrail; its exit code is its verdict, which is exactly what invariant 3 permits.

**5 — Honest halts; nothing is marked done unverified; needs-human is a feature.** §5 is a pure
application of this invariant one level down: the thing not yet verified is *the verifier*. The design
also refuses to oversell the halt — §5.5 states plainly that it buys **attention, not proof**.

**6 — Plain files, light setup.** The census reads the test runner's own per-test result file (TRX for
.NET). No new tool, no daemon, no service. The halt introduces **no new state file and no new marker** —
deliberately, §5.3.

**Standing maintainer ruling (`dial:critical` + `proceed-unreviewed` is FORBIDDEN — "Guardrails without
guardrails is self-defeating").** #375 is the same family. §5.3 discharges it: the halt is a **floor** in
the exact sense §2.1 of the SSOT uses the word for `review-gate` — **no `autonomyPolicy` value and no
`autonomy` dial value satisfies it**, and it is non-answerable *by construction* rather than by policy.

---

## 1. The finding under the finding: the catalogue's stated residual is already broken

The `covers-key-behaviors` section of `guardrail-catalogue.md` (#75) states its own honest limit, and
then names the mitigation for the residual:

> *"the residual (does the test actually exercise the behavior?) is the `tests-fail-on-current-code` red
> plus human review."*

**That sentence is false as written, and #375 is the measurement of its falsity.** The red guardrail is
`dotnet test --filter … exits non-zero`. Non-zero fires if **any** test in the filter fails. A hollow
`Assert.True(true)` test **passes** on the pre-implementation tree and hides behind its genuinely-failing
siblings. The red proves *the suite as a whole is not yet satisfied*; it proves nothing about any
individual test in it.

This is #479's own headline pathology, one level down. #479 measured that *"a pre-satisfied clause hides
behind its siblings' failures"* in a multi-clause guardrail script and concluded that a baseline pre-run
reports the wave clean. The identical shape here: **a pre-satisfied test hides behind its siblings'
failures**, and the red gate reports the file honest. #479's fix was per-item, not aggregate. So is this
one.

That reframing is the whole design. The mechanism #375 needs is **not new machinery** — it is the
existing red gate, evaluated at the granularity the claim was always made at.

---

## 2. The per-test red census — the deterministic half

### 2.1 The rule

The stub-based-TDD archetype already requires a test-author task to emit **throwing stubs** alongside the
test file, so that `build-passes` proves the tests compile and `tests-fail-on-stubs` proves the behaviour
is genuinely absent. The census keeps both and **replaces the second's predicate**:

> **`tests-fail-on-stubs` (suite form, today):** `dotnet test --filter <Class>` exits non-zero.
>
> **The per-test red census (this design):** for **every** behaviour in the task's manifest, the test
> bound to it is observed with outcome **`Failed`** in the runner's own per-test result file. A
> manifested behaviour whose test is `Passed`, `Skipped`, or **absent** is a finding, named
> individually. A test outside the manifest is not the census's business.

The second-sided half is the shipped `specific-tests-pass` on the implement task: the same names must be
observed **`Passed`** after implementation. Two trees, per test, both sides.

### 2.2 What it kills, exactly

| shape | on the stub tree | census |
|---|---|---|
| `Assert.True(true)` | passes | **RED — caught** |
| `Assert.NotNull(sut)` where `sut` is merely constructed | passes | **RED — caught** |
| a comment / string literal naming the behaviour, no test | absent from results | **RED — caught** |
| `[Fact(Skip="…")]` named for the behaviour | skipped | **RED — caught** |
| one `[Theory]` with N rows standing in for N behaviours | one name for N manifest entries | **RED — caught** (N−1 entries unbound) |
| a genuine rejection test driving the stub | throws → fails | green — correct |

The measured dogfood file — every wire token named, `Assert.NotNull` / `Assert.True(true)` bodies — is
**entirely inside the caught column**. Not incidentally: those bodies are hollow *because they never
invoke the subject*, and never invoking the subject is precisely what makes them pass against a throwing
stub.

### 2.3 What it does NOT kill — the honest boundary

A test that **invokes** the subject and then asserts something hollow:

```csharp
var result = sut.Consume(staleAnswer);   // stub throws → the test fails on the stub tree
Assert.NotNull(result);                   // ...and asserts nothing after implementation
```

This is red on stubs and green after. **The census passes it.** State this loudly rather than burying
it: the census proves the test is **coupled to the code path**; it does not prove the assertion is
**correct**. That is strictly weaker than "the assertions are right" and strictly stronger than "the test
exists and is named right" — and the second gap is the one #375 measured.

Closing the invoking-hollow gap requires a second mutant beyond the null implementation, i.e. real
mutation testing. That is §7's v2 bet, with an evidence gate, not v1.

### 2.4 Where it does not apply, and what to use instead

- **Data-model waves.** The catalogue already rules that a pure data model has no behavioural stub and
  the TDD split should collapse. With no stub tree there is no red side and the census is inert. The
  right tool for a data-model security invariant is the **negative assertion** (archetype #11) — *"the
  answer-kind enum contains no `review-attested` member"* is a source fact with no runtime proxy, which
  is exactly #468's carve-out for a legitimate source-shape check.
- **Tests authored before the run** and reviewed with the plan. The defect is *run-authored* tests; a
  pre-existing, human-read test file is not in scope for either half of this design.

### 2.5 Verdict on #468 proposal 4 — the manifest over discovered NAMES

The question was put directly, so here is the direct answer.

**#468 proposal 4 is right to kill the count and does not close #375.** It achieves both of its stated
purposes: it removes the theory-row gaming (`dotnet test` counts data rows, not methods) and it ratchets
(a later wave lands a named test and its clause goes green with nobody editing a script). Both are real.

But a manifest over `--list-tests` output asks *"does a test with this name exist?"* — and a hollow body
satisfies it, exactly as a comment satisfies a token floor. It **relocates the naming problem one
abstraction up**; it does not solve it. The 2026 formulation:

> **#468 proposal 4 chose the right data structure and the wrong predicate over it.** Keep the manifest.
> Change `discovered` to `observed FAILED on the stub tree, then observed PASSED after`.

Adopting the census is therefore not a competing proposal — it is proposal 4 finished. The manifest is
the shared artifact; the census is what reads it.

### 2.6 Relationship to #382 / doc 18 — reuse, not a parallel vocabulary

Doc 18 established the **assertion requirement** for the drive-the-real-seam archetype: *the test must
assert an effect only the production implementation emits; a recording double / call count / `Verify`
IS the passing-but-blind shape.* The census is the **same claim made mechanically**. Doc 18 states the
requirement in prose and enforces it by review + Probe B operator 20; the census enforces the weakest
mechanically-decidable half of it — *the test can fail when the implementation is absent* — with an exit
code.

Deliberate consequence: **no new vocabulary.** The census is an evolution of `tests-fail-on-stubs`, the
manifest is `covers-key-behaviors`' manifest, and the wording of the requirement is doc 18's. A reader
who knows doc 18 needs one sentence to learn this: *the assertion requirement, checked per test against
the null implementation.*

### 2.7 Where the determinism lives (doc 18 §3.2's table, for this defect)

| layer | mechanism | deterministic? |
|---|---|---|
| Authoring — `plan-breakdown` binds each manifested behaviour to a test name | prompt | **no** (as with every guardrail the skill emits) |
| Review — Probe B operator 21 against a hand-written hollow sample | prompt + **execution** | judgement + a mechanical probe |
| **Run time — the per-test red census on the stub tree** | script guardrail over the runner's result file, exit-coded | **yes** |
| Run time — the (W) *wrong-assertion* residual | — | **never**; §5's halt |
| `validate` — author-time lint | GR2064 only (marker inertness) | nothing about test content, deliberately |

*A prompt may propose, only a deterministic gate may certify.* The prompt proposes **which name pins
which behaviour**; the gate certifies that the named test can actually fail.

---

## 3. Option 1 — rejection-shaped assertions. Rejected as a guardrail; retained as a probe.

The issue's option 1 is: demand *rejection-shaped* assertions in the authored test source —
`Assert.Throws`, `Assert.False`, `NotEqual("consumed")`, a re-escalate — rather than bare token names.
It was used as the interim mitigation on the dogfood wave, under human code review.

**Apply the standing test: can a correct implementation be written that this rejects?** Yes, trivially,
and the rejected form is the *better* one:

```csharp
var result = sut.Consume(staleAnswer);
Assert.Equal(AnswerOutcome.RejectedStale, result.Outcome);
```

That is a correct, specific, discriminating security test. It contains no `Assert.Throws`, no
`Assert.False`, no `NotEqual`. A rejection-shape regex reds it. Worse than a false red: because the
guardrail is the grader, the retry feedback teaches the agent to **rewrite a typed-outcome API as a
throwing one** to satisfy the pattern. **The guardrail becomes a style mandate that degrades the design
it is grading** — which is strictly worse than absent.

And the false-green side is one line, per the #468 taxonomy the pattern walks straight into:

- **taxonomy 9 (vacuous tokens)** — `Assert.Throws` appears in the file, in a *different* test.
- **taxonomy 1 (declaration, not call)** — a helper named `AssertThrowsOnStale` satisfies it.
- **taxonomy 18 / operator 18** — one line satisfies every clause at once.
- and the reductio: `Assert.Throws<NotImplementedException>(() => sut.Consume(x))` is
  perfectly rejection-shaped and perfectly tautological.

So option 1 is a heuristic that produces **false reds on correct security tests** and is **gamed in one
line**. It was defensible as an interim measure on one wave with a human code review behind it. It must
not become doctrine, and it must not be the answer to #375.

**What survives.** The *intent* behind option 1 — "make somebody look at whether these assertions can
fail" — is right. Its error is promoting a **finder** into a **certifier**. The correct home for a finder
is the Probe B operator table, where its output is a reviewer's attention rather than a verdict:

> **Operator 21** — *satisfy a `covers-*` token floor or a name manifest with a test that is NAMED for
> the behaviour and asserts a tautology (`Assert.True(true)`, `Assert.NotNull` on a value the test
> itself constructed, an assertion that never invokes the subject).* **Countermeasure:** the per-test
> red census — each manifested behaviour's test observed **FAILED** on the stub tree, never merely
> discovered by name (#375). *Never* a rejection-shaped source regex: a correct
> `Assert.Equal(RejectedStale, r.Outcome)` has none of those tokens (#468 taxonomy 1/9/18).

This is runnable at review time even though the tests are not: the reviewer **writes** the hollow sample
and runs the task's `covers-*` guardrail against it — exactly the execution that produced #375's
evidence. That is Probe B's shape (#302's two-sided pair, #468 proposal 3), applied to a run-authored
artifact by manufacturing the mutant instead of waiting for it.

---

## 4. Is `sensitivity` a real marker or a euphemism?

**A euphemism, as a graded enum. Rejected.** The single useful atom inside it earns a field, renamed to
its effect.

### 4.1 The test a marker must pass

A schema field must name a thing the **harness does**. Run the enum through it: what does the harness do
differently at `sensitivity: "high"` versus `"normal"`?

| claimed effect | who actually does it | needs a harness field? |
|---|---|---|
| "the breakdown emits stronger gates" | the `plan-breakdown` **skill**, reading the plan `.md` | **no** — the plan prose already says it; the skill already reads prose |
| "the review demands a checkpoint" | the `guardrails-review` **skill** | **no** — same |
| "the run halts for authored-test review" | the **harness** | **yes** — but this is one boolean, not a scale |

There is no consumer that **compares** two sensitivity levels. Contrast `escalationThreshold`, which
looks like a motive-dial and is not: it has a total order and a comparison that a specific line of code
performs (`escalate ⟺ assessedCriticality ≥ escalationThreshold`). That comparison is what earns it a
schema. `sensitivity` has no such consumer, so its levels would be decoration around one boolean.

> **Ruling: name the effect, not the motive.** A field named for its effect can be verified — *did the
> halt fire?* A field named for a motive can only be argued about. Everything a `sensitivity` enum would
> buy beyond the boolean is doctrine wearing a schema.

### 4.2 So the field is

```jsonc
"reviewAuthoredTests": true   // optional, default false
```

on `task.json` — **task-level, not wave-level**, for four reasons:

1. It works identically in flat and waved plans; wave-level would force authors to restructure a plan to
   get a review.
2. It names the precise artifact. The halt message can list the exact files from the task's own
   `writeScope`, which is what the human needs in order to act.
3. It does not depend on the JIT-stub accident. A wave boundary only halts for a human when the *next*
   wave is an unauthored stub (SSOT §14.4 step 2); a fully-authored waved plan sails through.
4. The defect is *"before implementing"*, and the implement task is frequently in the same wave as its
   test-author task. Wave granularity is too coarse to sit between them.

### 4.3 "What stops an author from simply not setting it?"

**Nothing. That is the failure mode of every voluntary marker and this design does not pretend
otherwise.** What it does instead is make the omission cheap:

> **The unconditional mechanism carries the floor; the voluntary marker carries only the increment.**

The census (§2) is **not** gated on the marker. It applies to every test-author task with a manifest and
a stub tree, security-sensitive or not, because a vacuous test is a defect everywhere. So an author who
forgets the marker still gets the vacuity floor; they lose only the (W) wrong-assertion review. The
degradation is from *two* defences to *one*, not from one to zero.

Three ways to make the marker non-voluntary were considered and all three are worse for v1:

- **Derive it from plan text** (keyword: auth, token, secret, replay, attestation, forge). A prompt
  classifier gating a halt — a wolf, and #468's carve-out for prose-keyed lints applies verbatim.
  Available later as an **advisory** nudge in the breakdown report, the same posture as GR2025.
- **Require it on every task** (no default). Converts an omission into a recorded claim, which is the
  #485 posture and genuinely better — but it taxes every plan in the world for a field almost all of
  them set to `false`. Revisit only if §8's under-set falsifier fires.
- **Default it to `true`** for tasks whose `writeScope` looks test-shaped. Halts every TDD plan by
  default; nobody would ship with it on. Rejected.

---

## 5. Option 2 — the authored-test review halt. Accepted, narrowed, and placed.

### 5.1 What it is

When a task carrying `reviewAuthoredTests: true` **succeeds**, the harness completes the task's normal
settle — guardrails, merge to the plan branch, `Guardrails-Task:` trailer, journal — and **then halts the
run** before scheduling any dependent.

The post-settle ordering is load-bearing and not obvious: the halt must fire **after** the merge, because
the human reads the authored tests **on the plan branch**. A halt before settle would stop the run and
leave the artifact under review inside a worktree the human is not looking at.

In-flight siblings drain first. This reuses the shipped stop-the-drain path (SSOT §14.4 step 5 — *any
needs-human/blocked/failed halts the run at this wave*) with one genuinely new entry condition: **entry
from success**. Every existing entry to that path is a failure or an escalation; this one is a policy
checkpoint on a green task. That is the whole scheduler delta.

### 5.2 Where it sits in the contract — the answer to "is it a new gate in `AnswerableGates`?"

**No. It is a new value in the `decisions[]` gate vocabulary and it is deliberately absent from
`AnswerableGates`.** Placed against the three neighbours:

| | who initiates | what it means | answerable |
|---|---|---|---|
| `needsHuman` (§9) | **the agent**, from its fragment | *"I cannot resolve this"* | yes (`needs-human` ∈ `AnswerableGates`) |
| `needsHumanKind` (#485) | **the agent**, as a claim | classifies the ask: `blocked-work` \| `defective-guardrail` | orthogonal |
| `wave-checkpoint` (§14.4) | **the harness**, on plan structure | the next wave is unauthored | yes |
| `review-gate` (§7.2/#366) | **the harness**, on policy | the plan folder is unreviewed | **no** — there is no `review-attested` answer kind |
| **`authored-tests-review`** (this design) | **the harness**, on policy | *these run-authored tests have not been read* | **no**, by construction |

It is **not** a `needsHuman`: no agent asked anything, nothing failed, and the task is green. It
therefore carries **no `needsHumanKind`** — #485 is explicit that the kind is *"the AGENT's claim, never
the harness's judgement"*, and a harness-initiated halt has no claimant. Renderers must not synthesize
one; an `authored-tests-review` halt renders as its own thing.

Its true sibling is `review-gate`: harness-initiated, policy-driven, about an artifact a human has not
read.

### 5.3 Non-answerable **by construction**, and dial-proof

Answer-file injection (§7.2, #366) binds a reply to an **escalation record** in `logs/<runId>/escalations/`
under the identity-echo / dual-hash / CAS / answerable-gate contract. This halt **writes no escalation
record**. Consequences, all intended:

1. **There is nothing for an answer file to bind to.** Non-answerability is structural, not a policy that
   a later "let's generalise answer injection" refactor can erode. This is the same defence §7.2 uses to
   guarantee an answer can never forge a review marker.
2. The run exits on the existing honest-halt path (**2**), not `4 = EscalationsPending` — correct,
   because nothing is pending a reply.
3. The halt is recorded as a **`decisions[]` entry** (`boundary: "task"`, `gate: "authored-tests-review"`,
   `decision: "halted"`) — the same shape the shipped wave checkpoint records for every outcome.

**Dial-proof.** The halt does **not** consult `autonomyPolicy` and does **not** consult the `autonomy`
criticality dial. It is a **floor**, in the precise sense §2.1 uses the word for `review-gate`. No value
of `escalationThreshold`, no `gateThresholds` entry, and no `--autonomous` flag satisfies it. This
discharges the standing ruling directly: the forbidden `dial:critical` + `proceed-unreviewed` combination
is forbidden because *a run must not be able to dial away its own review*, and #375's halt is the same
review one level down. **The escape hatch is not a runtime flag — it is deleting the marker from a plan a
human reviews.** A declaration a reviewer can see beats a flag buried in a config.

### 5.4 The one substitution that must be pre-refused

The obvious cheap alternative: have a second agent read the authored tests and emit a verdict file, as a
prompt guardrail on the test-author task. Then an unattended run keeps running.

**Rejected for v1, and for any design that lets it CLEAR the gate.** It is a prompt-judge certifying a
security invariant alone — invariant 1. And #467 sharpens it: when the reviewing agent shares the
authoring context, self-review inherits the author's assumptions; the five regressions #468 measured are
that issue's evidence base.

The composition that **would** be sound, named now so a future proposal starts from it:

> **A one-way judge.** An adversarial test-reviewer may **raise** a halt that would not otherwise fire.
> It may **never clear** one. A judge that can only tighten cannot certify anything, so invariant 1 is
> intact, and it is genuinely useful on the *unmarked* tasks the author forgot.

That is a v2 bet (§7), not a v1 substitution for the halt.

### 5.5 What it buys — stated honestly

**Attention, not proof.** It guarantees a human is *given the chance* to read the tests. It does not
guarantee they read them; nothing writes an attestation and nothing verifies one. Resume is the
acknowledgement.

This is deliberate, and it is the **same enforcement strength as the shipped `BreakdownComplete` halt**,
which also resolves by a human resuming and whose SSOT text records that *"making unreviewed a per-wave
hard gate is a deferred refinement."* Going further would require a human-authored attestation file —
and #366 measured that marker as write-forgeable and deliberately refused to promote it into a runtime
boundary. **The design's weakest point is the posture the maintainer already ruled correct**, which is
the argument for accepting it rather than inventing a stronger-looking mechanism that is not stronger.

Two consequences follow, both recorded rather than prevented:

- An operator wrapper that auto-resumes on exit 2 blows straight through the halt. Unpreventable from
  inside the harness. The `decisions[]` entry makes it **visible after the fact** — attribution over
  prevention, the same posture #485 takes toward a kind the harness cannot verify.
- The "human" may be an AI (#467). The halt text must therefore name the delegation requirement — *the
  reviewer must not be the agent that authored these tests* — and hand over the census output as the
  starting point (§5.6). The harness cannot enforce it; saying so in the halt is the honest maximum.

### 5.6 Why the census makes the halt affordable

Without §2, the halt hands a human 400 lines of unfamiliar test code and the instruction "check these".
That review is expensive, unbounded, and — measured across this repo's history — the kind that gets
rubber-stamped.

With §2, every vacuous test is **already red before the halt can fire**, because the census runs as a
guardrail on the test-author task and a red guardrail means the task never reaches success. So the halt
is only ever reached with a manifest whose every entry is bound to a test that provably failed against
the null implementation. The human is handed a short, sharp question:

> Five behaviours, five tests, each observed FAILED on the stub tree. For each: **does the assertion pin
> the right invariant?**

That is question (W) and only question (W) — five bounded judgements instead of an open-ended read. **The
deterministic half does not merely add coverage; it is what converts the human half from a chore into a
checklist.** This composition is the design's actual contribution, and it is why the two halves ship in
this order.

### 5.7 When it should NOT fire

1. **The tests already existed** before the run and were reviewed with the plan. No run-authored artifact,
   no gap.
2. **No security- or safety-load-bearing invariant** is pinned by these tests. The halt is expensive; an
   ordinary wave's `covers-*` + census floor is the right level.
3. **A data-model invariant expressible as a negative assertion** (archetype #11). A deterministic gate
   beats a halt whenever one exists — invariant 1, and it costs no wall-clock.
4. **Twice for the same tests.** v1 does this the simple way: the halt fires on the task's transition to
   succeeded, and a resume skips completed tasks, so it cannot re-fire within the plan's life. No new
   state, no hash, no marker file. The consequence — *tests edited by a later retry are not re-reviewed* —
   is real and is §7's deferred `AuthoredTestsHash`.

### 5.8 The cost, named

A marked task's success stops the run. On an attended run: minutes. On an unattended overnight run: **the
rest of the night** — the run's remaining tasks do not execute, and the plan-branch state is durable but
idle. That is not a defect of the mechanism; on an overnight security wave it is the *entire value*
(#375's whole complaint is that such a run currently proceeds). But it is why the marker must be **rare,
per-task, and off by default**, and why §5.7 is normative rather than advisory.

---

## 6. Schema changes — exact SSOT deltas (specified, NOT applied in this pass)

Per invariant 4 a contract change lands in `02-schemas-and-contracts.md` in the same change that
motivates it. This document is a **draft** under #106 and another agent is mid-edit in that file, so the
deltas are specified verbatim here and applied by the implementing milestone.

### 6.1 §3 `tasks/<id>/task.json` — add to the JSONC block, after `stagingOutputs`

```jsonc
  "reviewAuthoredTests": false,  // optional, default false (§3.6, #375). true ⇒ after this task SUCCEEDS
                                 //   and settles (guardrails + merge + trailer + journal), the run HALTS
                                 //   before any dependent is scheduled, so a human can read the test files
                                 //   the task authored. A FLOOR: no autonomyPolicy / autonomy dial value
                                 //   satisfies it. Non-answerable BY CONSTRUCTION — no escalation record is
                                 //   written, so no answer file can bind to it. GR2064 when writeScope is [].
```

### 6.2 §3 — new subsection `### 3.6 Authored-test review halt (reviewAuthoredTests, issue #375)`

> A task that **authors tests at run time** produces the artifact its own downstream gate will trust. In
> the JIT staged-breakdown flow (§14.4) `/guardrails-review` runs against the plan folder *before* the
> run, so it cannot see those tests; the `covers-key-behaviors` floor (#75) verifies they *name* the
> behaviours, not that they *assert* them. For a security-sensitive task that gap is load-bearing.
>
> `reviewAuthoredTests: true` makes the harness **halt the run after the task succeeds and settles**,
> before scheduling any dependent. Semantics:
>
> - **After settle, not before.** Guardrails, merge to the plan branch, `Guardrails-Task:` trailer and
>   journal all complete first, so the tests under review are on the branch the human reads. In-flight
>   siblings drain via the existing stop-the-drain path (§14.4 step 5); the only new thing is entry to
>   that path **from success**.
> - **It is NOT a `needsHuman`.** No agent asked; nothing failed. The halt therefore carries **no**
>   `needsHumanKind` — #485's kind is the AGENT's claim and a harness-initiated halt has no claimant.
>   Surfaces must not synthesize one.
> - **Non-answerable BY CONSTRUCTION.** The halt writes **no** `escalations/` record, so there is nothing
>   for an `….answer.json` reply to bind to under §7.2's identity-echo / dual-hash / CAS contract. It is
>   not added to `AnswerableGates`. Structural, so a later widening of answer injection cannot erode it —
>   the same defence that keeps an answer from forging a review marker (§7.5, #366). The run exits on the
>   honest-halt path (**2**), never `4 = EscalationsPending`.
> - **A FLOOR, not a dial.** The halt consults neither `autonomyPolicy` nor the `autonomy` criticality
>   dial (§2.1), in the same sense `review-gate` is a floor rather than a criticality level. No
>   `escalationThreshold`, no `gateThresholds` entry and no `--autonomous` flag satisfies it. The escape
>   hatch is removing the marker from a plan a human reviews, never a runtime setting.
> - **Recorded, once.** The halt writes a `decisions[]` entry (§7) with `boundary: "task"`,
>   `gate: "authored-tests-review"`, `decision: "halted"`. It fires on the task's transition to
>   *succeeded*; a resume skips completed tasks, so it cannot re-fire. Tests rewritten by a later retry
>   are therefore **not** re-reviewed — a known v1 limitation (`docs/plans/22-run-authored-test-trust.md`
>   §7).
> - **It buys attention, not proof.** Nothing attests that a human read anything; resume is the
>   acknowledgement. This is the same enforcement strength as the `BreakdownComplete` halt (§14.4) and for
>   the same reason: the harness never writes a review marker on a human's behalf, and #366 refused to
>   promote the (write-forgeable) marker into a runtime boundary.
> - **GR2064 (error)** — `reviewAuthoredTests: true` on a task whose `writeScope` is `[]`. A task that
>   writes nothing authored no tests, so the marker can never have a subject.
>
> Design of record: `docs/plans/22-run-authored-test-trust.md`.

### 6.3 §7 — `DecisionEntry` `gate` row

Replace:

| `gate` | string | the specific gate — `needs-human` \| `wave-checkpoint` \| `review-gate` \| `blocker` |

with:

| `gate` | string | the specific gate — `needs-human` \| `wave-checkpoint` \| `review-gate` \| `blocker` \| `authored-tests-review` (§3.6, #375 — harness-initiated, non-answerable, `boundary:"task"`) |

### 6.4 §7.2 — append to the "Targets an ANSWERABLE gate" bullet

> The `authored-tests-review` halt (§3.6, #375) is likewise **never** answerable — and unlike the gates
> above it needs no rule to say so: it writes **no escalation record**, so no answer file has anything to
> bind to. Non-answerability by construction rather than by predicate.

### 6.5 §8 — append to the `escalations/` note

> The `authored-tests-review` halt (§3.6) writes **no** record here at all. It is a policy checkpoint on a
> **green** task, not an escalation, and its absence from this directory is what makes it structurally
> unanswerable.

### 6.6 §14.4 — append to step 2's review-gate paragraph

> A task inside a wave may additionally carry `reviewAuthoredTests: true` (§3.6, #375), halting the run
> mid-wave after that task settles so a human can read the tests it authored. That is a **task-boundary**
> gate and is independent of this wave checkpoint: it fires in flat plans too, and it is not satisfied by
> the between-wave review.

### 6.7 GR code

**GR2064** — `reviewAuthoredTests: true` on a task with `writeScope: []`. **Error.** Next free after
GR2059 (#459), GR2060/GR2062 (doc 19), GR2061 (reserved, doc 18), GR2063 (doc 20).

Deliberately **no** code is reserved for the heuristic sibling (*"the marker is inert because the task's
`writeScope` names no test-looking path"*) — that check is a warning at best and, per doc 18 §3.4's rule,
**a number reserved for deferred work must never block a code that is shipping now.** It takes the next
free code on the day it ships.

---

## 7. Phasing — v1, deferred, rejected

### v1, in this order

| # | milestone | placement | why this order |
|---|---|---|---|
| **M1** | **Probe B operator 21** (hollow assertion) + the taxonomy entry | `guardrails-review` SKILL.md, `guardrail-catalogue.md` | cheapest, zero harness, and it makes the reviewer *look* for the shape immediately |
| **M2** | **The per-test red census** — archetype, TRX recipe, `plan-breakdown` emission; **and correct the #75 honest-limit paragraph** (§1 — its stated residual mitigation is false at suite granularity) | `guardrail-catalogue.md`, `stacks/dotnet.md`, `plan-breakdown` SKILL.md | the unconditional floor. No harness, no schema, no new vocabulary |
| **M3** | **`reviewAuthoredTests`** field, the halt, the `decisions[]` gate token, GR2064, surfaces, SSOT §6 deltas | harness + schema | the increment. Depends on M2 for §5.6 to hold |

M1 and M2 raise the floor **unconditionally**. If M3 slips or is rejected in review, the floor has still
risen and #375's measured defect — a green wave on `Assert.True(true)` — is closed deterministically. That
is the phasing argument, and it is the same argument as §4.3.

### Deferred, with evidence gates

| deferred | gate that would promote it |
|---|---|
| **Real mutation testing over authored tests** (Stryker.NET or equivalent) — the only thing that closes §2.3's invoking-hollow gap | evidence that invoking-hollow is the dominant residual, i.e. §8's falsifier 2 fires more than once |
| **`AuthoredTestsHash` + re-fire on change** — the halt re-fires when a retry rewrites reviewed tests | one measured instance of a post-review retry rewriting a marked task's tests |
| **The one-way (fail-only) adversarial test-reviewer** (§5.4) — may raise a halt, may never clear one | M3 shipped and §8's falsifier 4 (nobody sets the marker) firing |
| **Derived or required `sensitivity` declaration** | §8's falsifier 4 |
| **Inertness warning** — marker on a task authoring no test-looking path | after M3 has real usage; next free code at that time |

### Rejected

- **`sensitivity` as a graded enum** (§4). No consumer compares two levels; it is one boolean wearing a
  scale.
- **A rejection-shaped-assertion regex as a certifying guardrail** (§3). False-reds correct tests, is
  gamed in one line, and teaches worse API design. Retained only as operator 21.
- **Any answer-file resolution of the halt** (§5.3). Structurally excluded, permanently.
- **Any `autonomyPolicy` / `autonomy` value that satisfies the halt** (§5.3). The standing ruling.
- **An AI reviewer that can CLEAR the halt** (§5.4). Invariant 1 plus #467.

---

## 8. What evidence would tell us v1 was wrong

1. **The census false-reds correct security tests.** Measure: manifest entries deleted or loosened during
   a run's retries. More than one per plan and the census is a wolf; the response is to narrow it to the
   tasks that declare a manifest of ≥3 behaviours.
2. **The census goes green on a wave whose tests are later found hollow** by human review or by
   production. Measures §2.3's invoking-hollow gap as dominant; promotes real mutation testing.
3. **The halt is rubber-stamped.** Measure: wall-clock between the halt and the resume. Under ~60 seconds
   for a multi-hundred-line test file means the halt buys nothing, and the answer is #467-style delegation
   with a named reviewer or nothing at all.
4. **Nobody sets the marker.** Measure: marked tasks per plan across the next N plans. Zero confirms the
   voluntary-marker failure and promotes the required-declaration or one-way-judge options.
5. **The halt fires on tasks §5.7 says it should not** (data-model waves, pre-authored tests). Means the
   doctrine did not land and the emission rule in `plan-breakdown` is too eager.

---

## 9. Devil's-advocate self-critique

**The strongest counter-argument: the census is just another gameable floor, one level down — you have
moved the naming problem to a per-test outcome problem and declared victory.**

Partly conceded, and §2.3 concedes it in the document rather than in a footnote: an invoking-then-hollow
test defeats the census. What makes this different from the floors it replaces is the **polarity of the
residual gaming**. To defeat a token floor, the agent writes a comment — zero coupling to the code, zero
cost. To defeat the census, the agent must write a test that **actually invokes the subject** and
therefore compiles against the real API, fails against the null implementation, and passes against the
real one. It is still a bad test, but it is a bad test that is *wired to the thing*. The cheapest wrong
move got materially more expensive and materially more visible to a reader — and making the residual
visible to a reader is precisely what §5.6 hands the human. A floor that cannot be raised further without
mutation testing is not the same as a floor that a comment satisfies.

**Second counter: you rejected option 1 for being a heuristic over agent-authored code, but the census is
also a gate the author must aim, and #468 measured aiming as where the defects live.** Conceded that the
census has an aiming surface — a `--filter` and a list of names, which is the known #455 hazard with a
shipped countermeasure. But the **failure polarity is inverted**, and that is decisive. A mis-aimed
census entry matches no test and goes **RED** — it costs an attempt and names the missing binding. A
mis-aimed source regex goes **GREEN** on a comment (taxonomy 9) *and* red on correct code (taxonomy 8).
A gate whose mis-aiming can only cost time is in a different safety class from one whose mis-aiming can
certify a lie.

**Third counter: M3 adds harness machinery for a halt that a sentence in the breakdown report would also
produce.** Refused. Every advisory this repo has shipped — GR2025's nudge, the breakdown report's
caveats, the #75 honest-limit paragraph that §1 shows is *both* advisory *and* wrong — scrolls past. A
process exiting 2 with the file paths listed is categorically different: the run does not continue
without a human keystroke. That difference is the whole of M3's value, and it is the same difference the
shipped `NextWaveUnauthored` halt already buys at a coarser grain.

**Fourth counter: this whole design is one dogfood wave's evidence generalized into a contract.** True,
and it is the weakest part of the case. The mitigation is the phasing: M1 and M2 are skill-only and
reversible, and M3 — the part that touches the contract — is the part gated behind the #106 draft-PR
review this document is going out for.

---

## 10. Implementation handoff

Sequenced. **No milestone starts until this document has been reviewed as a draft PR (#106).**

| # | agent | filesTouched | contract |
|---|---|---|---|
| **M1** | `guardrails-skill-author` | `.claude/skills/guardrails-review/SKILL.md` (operator table → row 21); `.claude/skills/plan-breakdown/references/guardrail-catalogue.md` (taxonomy anti-pattern list) | Operator 21 verbatim from §3. Countermeasure column must name the census and must **not** name a source regex. |
| **M2** | `guardrails-skill-author` | `guardrail-catalogue.md` (new census section beside stub-based TDD; **amend** the #75 honest-limit paragraph per §1); `references/stacks/dotnet.md` (TRX per-test recipe); `plan-breakdown/SKILL.md` Step 4 (emission rule) | The census reads the runner's own per-test result file, never stdout scraping (#248). Zero-match guard on the filter (#455). Failure output names each unbound behaviour on its own line (#179, one message per clause). |
| **M3a** | `guardrails-harness-developer` | `src/Guardrails.Core` (task model, `PlanValidator` GR2064, `Scheduler` halt-from-success, journal `decisions[]` gate token); `docs/plans/02-schemas-and-contracts.md` (§6.1–§6.7 verbatim, **same change**, invariant 4) | Halt fires **after** settle/merge. **No** `escalations/` record. **No** `needsHumanKind`. Reads neither `autonomyPolicy` nor `autonomy`. Exit 2. |
| **M3b** | `guardrails-ux` → `guardrails-harness-developer` | live table, `--no-ui`, run summary, `guardrails status`, log site | The halt renders as its own thing, lists the authored files from `writeScope`, names the #467 delegation requirement, and states in words that resume is the only acknowledgement — it must never read as "reviewed". |
| **M3c** | `guardrails-test-author` | `tests/**` | Per §6.2, one test per bullet. Specifically: an answer file for an `authored-tests-review` halt is inert (no record to bind to); `autonomyPolicy: auto` + `escalationThreshold: critical` still halts; the halt fires after the merge (assert the test file is on the plan branch at halt time); GR2064 on `writeScope: []`; a resume does not re-fire it. |
| **M2-x** | `guardrails-test-author` | golden-folder round-trip fixtures | A hollow-body sample and a genuine sample committed as the census's two-sided pair (#468 proposal 3 / #302). |

---

## 11. Proposed plan-document edits

1. **`docs/plans/README.md`** — **no edit.** Verified: the index covers `00`–`03` only; docs `07`–`21`
   are unlisted. Adding `22` alone would be inconsistent. (Indexing `07`–`22` is worth doing as its own
   mechanical change — trivial per #106, so it goes straight in rather than through a draft PR.)
2. **`docs/plans/18-integration-proof-proximity.md` §5** — add a cross-reference: *the assertion
   requirement's mechanically-decidable half is enforced per test by the red census
   (`22-run-authored-test-trust.md` §2.6); operator 21 is to operator 20 what a vacuous test is to a
   constructed fake.*
3. **`docs/plans/03-roadmap.md`** — add the v2 bet: *mutation testing over run-authored tests, to close
   the invoking-hollow gap the red census cannot reach (`22-run-authored-test-trust.md` §2.3), with the
   evidence gate in §8.*
4. **No edit to `docs/plans/02-schemas-and-contracts.md` in this pass** — §6 above is the verbatim delta
   and it lands with M3a, in the same change, per invariant 4.

*(#375 stays open; this document does not close it.)*
