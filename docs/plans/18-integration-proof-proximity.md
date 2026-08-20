# 18 — Integration-proof proximity: the seam ledger and the one-real-level rule (#382) — design of record

**Status:** DRAFT design of record, awaiting the #106 inline review. No implementation milestone starts
until this doc has been reviewed on a draft PR and its comments are addressed.

**One-line statement.** A per-task guardrail that proves a component against a **fake of the in-process
seam the real run drives** certifies nothing about the run; the fix is not another lint but a **placement
rule** — each component is proven **one real level down**, at the task that builds it, so an integration
bug surfaces **in a scope that can fix it**, and the terminal composition proof degrades from a first
exercise to a **join-check**.

**The headline decision, stated up front because it is the one a reader will want to argue with:**
this design adds **no new `guardrails validate` code**. The defect's carrier — the test's substitution of
a fake for a production type — **does not exist at validate time**; it is written by the run. The only
pre-run evidence is prose in an `action.prompt.md`, and the legitimate and illegitimate forms of that
prose are textually identical. What ships instead is an **authoring rule that emits an ordinary
deterministic guardrail** (a rung-1 contract test on the component's own implement task), a **review
audit** of where that guardrail was placed, and one new **execution probe operator**. The deterministic
gate for #382 exists — it just runs at task time, which is the first moment the evidence exists.

---

## What's being asked

Issue #382 records a dogfood halt (`autonomous-mode-impl`, wave 3) in which **two** distinct integration
bugs surfaced together at one terminal wiring task, both of them **certified GREEN by their own
guardrails**:

- **Bug A** — `CriticalityJudge.BuildInvocation` handed the prompt runner an empty `StreamLogPath`. The
  real `ClaudePromptRunner` throws on it; the judge's `catch` safe-defaults to `Escalate`, so it
  escalated **100% of the time**. The task's unit tests injected a **fake `IPromptRunner`** that never
  touches the path.
- **Bug B** — a class-(b) transient resolving inside the executor's pause budget was handled silently and
  never recorded the `blocker-retried` decision the design requires. The task's tests never drove the
  executor's real `TransientBackoff`.

One root cause: **the guardrail faked the seam the real run exercises.** One structural consequence: the
only thing exercising the real wiring was a single end-of-wave #120 "drive the real factory" task, which
therefore surfaced **every** masked bug at once, late, in a task that is over-scoped by construction
(#378) and **cannot fix** what it finds — each bug lived in a different upstream task's file, outside its
`writeScope`. Result: `needsHuman`.

The issue addresses its proposed direction to this role and says the mechanisms are *to be designed, not
prescriptive*. Four questions to decide: **(1)** how a blind unit test is detected, and whether that is
deterministic; **(2)** what "distribute composition-root proof" means as a rule an LLM author can apply
and a reviewer can falsify; **(3)** whether a reusable "drive-the-real-seam" archetype belongs in the
catalogue and its exact shape; **(4)** how all of that composes with #468/#479, which landed after the
issue was filed.

### Ambiguity named, and the narrowing

The issue's phrase **"where feasible"** is the whole difficulty. As written it is unfalsifiable: any
author can declare any real-seam proof infeasible and no reviewer can contradict them. §1 below replaces
it with a **closed four-bucket classification** of faked seams in which exactly one bucket is exempt, a
**one-level-and-no-further** construction bound, and an **earliest-proving-task** placement test that is
computable from the emitted DAG. Everything else in this design hangs off that narrowing. If the review
rejects the narrowing, the rest does not survive.

---

## What already shipped for #382, and what is left

Commit `08aad40` (2026-07-24, "#378/#382 — over-scope lint (GR2042) + fake-masks-integration
authoring/review discipline") already landed a first pass, **skills only, no validate code**:

| already shipped | where |
|---|---|
| The "Faked-seam ⇒ paired real-seam proof" doctrine bullet | `plan-breakdown/SKILL.md` (Step 4 analysis, beside the #120 routing) |
| The "Drive-the-real-seam" catalogue archetype + its `# catches:` template + the fake-the-process boundary rule | `references/guardrail-catalogue.md` |
| The .NET realization | `references/stacks/dotnet.md §10e` |
| The "Passing-but-blind faked seam (#382)" review probe (BLOCKER on a composition-root path, WEAK on a thin terminal join-check) + a checklist item | `guardrails-review/SKILL.md` |
| GR2042 `StructuralOverScope` (the **#378** half) | `PlanValidator`, SSOT §3.4 |

So #382 is **not unstarted**; it is **under-specified**. What is left, precisely:

1. **`"where feasible"` is still a placeholder.** The shipped doctrine says "prove each component through
   the real factory AT the task that builds it, **where feasible**" and never defines feasible. An LLM
   author reading that has no procedure and a reviewer has no test. This design's §1 is the fix.
2. **No decision was ever recorded on the deterministic question.** The commit chose skills-only by
   default, not by argument. §3 records the decision *and its falsifier*.
3. **The archetype predates #468 and #479 by a month** and does not compose with them: it is not stated in
   #468's rung ordering, it is not disambiguated from #468's AGREEMENT property test (they are adjacent
   and a reviewer can substitute one for the other), it carries no `scope` ruling (the #250 mistake is
   available to repeat), and #479's Probe B has no operator that can game it. §5 is the fix.
4. **The #378 boundary was asserted, not enforced.** Both issues now have doctrine in the same two files
   and one of them has a lint. Nothing stops the next agent growing a second, half-overlapping lint. §6
   draws the line as a rule future agents inherit.

---

## Placement

| concern | placement |
|---|---|
| The seam ledger, the four buckets, the one-real-level rule, the placement test | **skill** — `plan-breakdown/SKILL.md` (Step 4 analysis + Step 5 emission), authoring-time |
| The archetype's exact shape, rung ordering, `scope` ruling, AGREEMENT disambiguation | **skill** — `references/guardrail-catalogue.md` + `references/stacks/dotnet.md §10e` |
| The ledger audit, placement check, terminal-gate redundancy check, Probe B operator 20 | **skill** — `guardrails-review/SKILL.md` |
| The deterministic gate itself | **the emitted guardrail** — an ordinary `specific-tests-pass` on the component's implement task. No new harness surface. |
| A new `validate` lint | **explicitly declined for v1** (§3), with a named falsifier and a deferred design (§3.4) |
| A `task.json` / sidecar schema field for the ledger | **deferred** (§3.4). No SSOT contract change in v1. |
| GR2042's remedy pointer | **harness (one message string) + SSOT §3.4 (one sentence)**, landing together — the only code touched by this design |
| A parameterized library entry for the archetype | **#350's mechanism, not this issue's** (§8) |

**Not v1, said plainly:** no schema field, no GR code, no harness behaviour change, no new guardrail kind,
no new phase. If this design cannot be carried by authoring doctrine plus one execution-probe operator,
it is the wrong design — and §11 argues with that.

---

## Invariants in play

**1 — Deterministic guardrails over prompt-judges; judges never alone.** This is the invariant the design
is easiest to get wrong on, because the honest answer looks like a retreat. It is not. The rule's
*output* is a deterministic gate: a real-seam contract test, executed, exit-code-verdicted, on the
component's own task. What is prompt-driven is only the **authoring decision to emit it** — which is true
of every guardrail Guardrails has ever generated. The design does **not** add a prompt-judge that rules
"is this test blind?"; it adds a rule that produces a deterministic test, and a review audit of whether
the rule was applied. Strain: the audit itself is judgement, and nothing deterministic backstops its
omission at author time. §3.4 names that hole and its remedy rather than papering it.

**5 — Honest halts; nothing is marked done unverified.** This is the invariant #382 reports **violated**.
Two components were marked done, by a deterministic gate, while broken through the real run path. The
halt was honest; the *greens upstream of it* were not. Distributing the proof does not make more halts —
it makes each halt land in a task whose `writeScope` contains the defect, so the retry budget can spend
itself on a fix instead of on a `needsHuman`.

**2 — Harness is the single writer of merged state; children get snapshots, write fragments.** Constrains
the placement rule directly, and is the reason "just make the sink's `writeScope` wider" is not the fix.
The terminal sink could not repair Bug A or Bug B because their files were outside its scope, and
widening the scope to admit them would make the sink a plan-wide writer — precisely the shape #378 flags
and the shape the merge policy is built to prevent. **The proof must move to the writer, because the
writer cannot move to the proof.** That single sentence is the structural argument for this whole design.

**4 — `02-schemas-and-contracts.md` is the schema SSOT; a contract change lands in the same change.**
Respected trivially in v1: **no contract changes.** The one proposed SSOT sentence (§10) is documentation
of an existing GR2042 warning's remedy and must land with the harness string it describes.

**6 — Plain files, light setup.** Respected. The ledger is a table in a markdown report; the proof is a
test file.

---

## 1. The core rule — one real level down, and the induction it buys

### 1.1 What counts as a seam

A **seam** is a `(component, declared dependency)` pair: the component under test and one dependency it
declares — a constructor parameter, a DI-resolved interface, an injected delegate, an overridable member.
The ledger has one row per pair, not one per component and not one per interface in the repo.

A seam is **in-process** when the substitute is resolved inside the same process. A **process seam** — a
child process, a CLI, a socket, an HTTP endpoint, a database, the filesystem — is out of scope for this
rule, and faking it is expected, correct, and unchanged by anything here.

### 1.2 The four buckets — the closed classification that replaces "where feasible"

Every faked in-process seam lands in exactly one bucket. **Only bucket N is exempt.**

**N — a non-determinism primitive. EXEMPT.** The seam exists to remove non-determinism from the test, and
driving the real one makes the test slow or flaky. This is a **closed enumeration**, not a category — a
seam is N only if it is one of:

- **N1** a clock / time source;
- **N2** a randomness source — an RNG, a GUID factory;
- **N3** an ambient environment reader — env vars, machine name, current directory, an OS probe;
- **N4** a **wait primitive** — a sleep/delay/timer substituted so the test does not spend real time.

Anything not on that list is **not N**, and a reviewer rejects an N classification for anything off it. A
category invites rationalization; a closed list can be checked.

> **The N4 trap — fake the WAIT, never the WAITER.** Bug B is exactly this boundary, and it is the single
> most likely place this taxonomy gets abused. Substituting the *sleep* so a backoff test finishes in
> milliseconds is N4 and exempt. Substituting the **backoff policy component** that decides whether to
> retry and records `blocker-retried` is **C**, and owes proof. The exemption covers the primitive that
> consumes time, never the policy that decides to consume it. If the substitute has a *decision* in it,
> it is not N4.

**E — an external-resource adapter. PROOF OWED, and feasible.** The seam's production implementation
crosses a process / network / disk boundary. Bug A is this bucket: `IPromptRunner` → `ClaudePromptRunner`
→ the `claude` child process. **Drive the real adapter and fake the boundary underneath it** (a stub
binary, a fake `HttpMessageHandler`, a temp directory). The boundary below the real seam is a process
seam, so §1.1 already permits faking it. This bucket is *always* feasible; that is what distinguishes it
from a seam you cannot construct.

**C — an in-repo collaborator with a contract the run depends on. PROOF OWED, and feasible.** The
production implementation lives in this repo and does real work the component depends on — a scheduler, a
factory, an executor's backoff, a policy object. Bug B is this bucket. **Construct the real
implementation.** Its own dependencies are covered by their own ledger rows at their own tasks (§1.3), so
you never build the universe.

**U — an unbuilt collaborator. PROOF OWED, but RELOCATED.** The seam's production implementation does not
exist yet at this point in the DAG; a later task or wave builds it. The row is **not** exempt — it names
the **receiving task** (§1.4), and the proof is owed there. A U row whose receiving task is the terminal
sink is only legitimate when the production type genuinely first exists there; otherwise the row is
mis-placed and the finding is the placement, not the bucket.

### 1.3 The one-level-and-no-further clause

> **The component under test is constructed with the REAL implementation of the seam under test.** That
> implementation's own declared dependencies may be substituted — because each of those substitutions is
> its own ledger row, owed at its own task.

This is the crisp form of "fake the process, never the in-process seam", and it is strictly stronger,
because it says exactly *how far down* real goes: **one level, and no further.**

**The induction this buys, which is the point of the whole design.** If every task proves its component
one real level down, then by induction over the dependency graph every level of the composition has been
exercised for real *somewhere*, in a scope that could fix it. What remains unproven is only the
**assembly** — that the production assembler constructs these particular objects, in this order, and
hands them on. That residue is small, is genuinely composition-level, and is exactly what the terminal
join-check should assert (§1.5). Big-bang integration stops being structurally necessary.

**The construction bound (the honest limit).** If constructing the production seam requires you to build
a *second* real level — the real `Scheduler` needs a real journal which needs a real repository which
needs… — then "one real level" has been exceeded and the proof **degrades**, along the ladder #120
already established:

1. drive the real seam and assert an observable effect (the default);
2. **#120(b)** — construct the real collaborator, assert by reflection that the component holds it, **with
   a contrast case** proving the wiring is conditional and real;
3. a source grep — **not available here**, see §5.1.

A degradation to (2) is **named in the breakdown report** with the constructor chain that forced it. This
is the same discipline #468 already imposes ("state why no test could carry it") and #120 already imposes
(its own three-rung ladder), reused rather than reinvented. An unnamed degradation is a review finding.

A high construction cost is also a signal in its own right: a production type that cannot be constructed
without three more of them is badly factored, and surfacing that is better than hiding it behind a fake.

### 1.4 Placement — the earliest-proving task, computed from the DAG

> For each **E** and **C** row, the proof is owed at **T\*** — the **earliest task in the DAG** at which
> both (i) the component's production type and (ii) the seam's production type exist. For a **U** row,
> T\* is the earliest task satisfying (ii), and the row names it.
>
> **A proof placed later than T\* is a finding.** The report must name T\* and state why the proof could
> not live there.

Both existence facts are readable from the emitted graph — a type exists at a task when that task's
`writeScope` contains the file that declares it, or an ancestor's does. So T\* is **computable by a
reviewer without running anything**, which is what makes the rule falsifiable. "Where feasible" asked for
a judgement nobody could contradict; "which task is T\*, and is the proof there?" has an answer.

In the common case the component's implement task **is** T\*, and the rule reads as the issue's own
sentence: *prove each component through the real seam at the task that builds it.*

### 1.5 The terminal composition proof is a JOIN-CHECK

The terminal proof is whichever object carries the composition assertion: a #120 wiring **task**, and/or
the plan-level `<plan>/guardrails/` **folder** (SSOT §3.3). The rule is the same for both.

> The terminal composition proof may assert only what the union of the upstream real-seam proofs does
> **not**: that the collaborators are **assembled** — constructed, injected, ordered — by the production
> assembler. Its `# catches:` must name a defect that **survives every upstream proof passing**. If it
> cannot name one, it is redundant. If the only defect it can name is *"this seam is exercised for the
> first time here"*, then a ledger row is mis-placed, and the fix is upstream — not a wider `writeScope`
> here.

That last clause is the anti-regression clause. Without it, an author satisfies this design by writing a
ledger and then leaving all the proof in the sink anyway.

---

## 2. The seam ledger — the authoring artifact

The ledger is a **table in the breakdown report**, produced during plan-breakdown Step 4 analysis and
carried into the Step 7.4 report. It is markdown, not schema (§3.4 records why, and what would change that).

| seam (component → declared dependency) | bucket | production type | faked underneath | T\* | proof |
|---|---|---|---|---|---|
| `CriticalityJudge` → `IPromptRunner` | **E** | `ClaudePromptRunner` | the `claude` CLI child process (stub binary) | `09-implement-criticality-judge` | `guardrails/03-real-seam-tests-pass.ps1` |
| `RetryLoop` → `ITransientBackoff` | **C** | `TransientBackoff` | — (no boundary below) | `11-implement-transient-recording` | `guardrails/03-real-seam-tests-pass.ps1` |
| `RetryLoop` → `IDelay` | **N4** | — | — | exempt | — (the wait, not the waiter) |
| `Scheduler` → `IOverwatcher` | **U** | `Overwatcher` (built in task 13) | — | `13-implement-overwatcher` | deferred to T\*, named |

**Rows the ledger does NOT carry:** process seams (§1.1) and any dependency the tests do not substitute.
The ledger is a list of *substitutions made*, not a dependency inventory — otherwise it becomes a wall of
noise and stops being read, which is the same failure mode a false-positive lint has.

**Where it goes.** Step 4 (analysis) builds it; Step 5 (emission) uses the T\* column to place guardrails;
Step 6 (report) prints it. `/guardrails-review` reads it as the primary evidence for its probe, and its
**absence is itself a finding** — a breakdown that emitted no ledger did not run the analysis.

---

## 3. Detection: deterministic vs review-only — the honest split

### 3.1 Why `validate` cannot see it

Three reasons, in order of decisiveness.

**(a) The evidence does not exist yet.** `validate` runs on the plan folder before the run. The
substitution that constitutes the defect lives in a **test file the run has not written**. There is no
text for a lint to read. This is not a hard problem; it is a timing impossibility.

**(b) The only pre-run signal is prose, and its correct and incorrect forms are identical.** A lint could
key on an `action.prompt.md` that says *"inject a fake `IFoo`"*. Apply the standing test — **can a correct
implementation be written that this rejects?** — and the answer is *yes, trivially and constantly*: every
bucket-N seam is a prompt saying exactly that, correctly. A clock, an RNG, an env reader. §4.7's design
constraint is explicit: *a validator that cries wolf gets ignored, and its true positives are lost with
it.* A prose-keyed fake-detector would be the loudest wolf the family has shipped.

**(c) The second half of the predicate is not textual at all.** Even granting detection of the fake, the
finding requires *"and no task provides a paired real-seam test"* — which requires knowing what a
guardrail **proves**, not what it **says**. A `dotnet test --filter X` guardrail is opaque to a text lint;
whether the tests behind `X` drive a real seam is decidable only by reading source that, again, does not
exist yet.

A note on the adjacent temptation: keying the lint on the guardrail's `# catches:` comment. Rejected —
that makes a **comment** load-bearing for a verdict, which is gameable in one line and inverts the
comment's purpose from documentation to certification.

### 3.2 Where the determinism actually lives

The answer to *"is #382 deterministic?"* is **yes, at task time** — and the confusion comes from asking
the question one layer too high.

| layer | mechanism | deterministic? |
|---|---|---|
| Authoring — plan-breakdown builds the ledger and places the proof at T\* | prompt | **no** (as with every guardrail the skill emits) |
| Review — audit the ledger, recompute T\*, run Probe B op 20 | prompt + **execution** | judgement + a mechanical probe |
| **Run time — the emitted real-seam contract test** | `specific-tests-pass`, exit-coded | **yes** |
| `validate` — author-time lint | — | **nothing new, deliberately** |

This is the product's own thesis applied correctly: *a prompt may propose, only a deterministic gate may
certify.* The prompt proposes **where the gate goes**; the gate does the certifying. #382's failure was
never that a prompt certified something — it was that the deterministic gate was **pointed at a fake**.
The fix is to aim it, not to add a second gate in front of it.

### 3.3 What is review-only, and why that is acceptable here

Review-only, and staying that way in v1: *did the author build a ledger; is each row's bucket right; is
each E/C proof at T\*; does the terminal proof name a defect that survives the upstream proofs.*

Acceptable because the review pass's failure mode here is **benign and visible**. A missed row leaves a
component proven only against a fake — the status quo ante, no regression. And unlike a lint, review can
be *sharpened by execution*: Probe B operator 20 (§5.3) gives the audit teeth that reading cannot.

Not acceptable forever, which is what §3.4 is for.

### 3.4 The deferred lint (GR2061) and its evidence gate

> **Reservation moved, 2026-08-20.** This lint was first reserved as `GR2059`. That code went to shipping
> work instead (#459's wave-root `scope:"integration"` inertness warning), and `GR2060` to the #474/#477
> family. A number reserved for DEFERRED work must never block a code that is shipping now, so this lint
> reserves the next free code and will take whatever is next free on the day its evidence gate opens.

**Deferred, designed, not built.** If the ledger were a machine-readable artifact rather than a report
table, one genuinely deterministic check becomes available — and it is a *referential-integrity* check,
the only shape that is false-positive-free by construction because it relates declarations to
declarations and infers nothing:

> **GR2061 (deferred)** — every seam a task declares faked with bucket **E**, **C** or **U** is named by
> some task's declared real-seam proof, at or before T\*, within the same wave.

The precedent is exact: this is the `stateOut`/`stateIn` key-matching check, applied to seams. It needs a
`task.json` (or guardrail-sidecar) field, i.e. an SSOT contract change.

**Why not v1, in one line each:** the declaring agent is the agent the declaration grades (#468's own
measured lesson); an undeclared fake is invisible, so the lint's floor is the honesty of the author it
polices; and any waiver value becomes the escape hatch every author takes. Contract surface in exchange
for a check whose hole is in the same place as the problem is a bad trade **on current evidence**.

**The evidence gate that flips it.** Ship GR2061 when review reports show the dominant failure is the
ledger being **absent** rather than **wrong**. Absence is a declaration failure and a lint fixes it;
mis-classification is a judgement failure and a lint cannot. If three consecutive breakdowns emit no
ledger at all, build GR2061. `GR2061` is reserved for it (next-free at time of writing;
`DiagnosticCodes.cs` line ~709 must be advanced by whoever takes it).

---

## 4. The archetype — keep it, restructured

The "Drive-the-real-seam" archetype (#382) **stays in the catalogue** and does not move to a vetted
library in v1 (§8). Its shape, stated exactly:

**Rung.** **Rung 1** under #468's ordering — a **test**, always. The real-seam proof is a behavioural
claim ("the component works through the production seam") and there is no rung-3 fallback for it: a regex
over a test file that greps `new ClaudePromptRunner(` certifies vocabulary, which is #468's headline
failure verbatim. The only permitted degradation is the **#120(b) reflection + contrast** form (§1.3), and
that is a different assertion, not a weaker spelling of the same one.

**Authored as a TDD pair, on the component's own tasks.**

- On the **author-tests** task: the real-seam test is written alongside the fake-based unit tests, listed
  in the task's `covers-key-behaviors` manifest (#75), and included in the `tests-fail-on-current-code`
  filter — so it is proven **RED** and cannot be a tautology.
- On the **implement** task (usually T\*): a `specific-tests-pass` guardrail whose `--filter` selects the
  pair's own test class (#455 scoping, zero-match guard, #179 failure-detail re-emit).

**The assertion requirement — an effect only the production implementation emits.** The test must assert
something the fake could not produce without reimplementing the real behaviour: the stream log **file**
appears on disk; the journal contains a `blocker-retried` **decision**; the verdict's `Source` is **not**
the catch-and-safe-default. *"The seam was called"* is not an assertion — the fake would satisfy it, which
is how we got here. This clause is imported from #120(a)'s "observable output only the wired feature
produces" and is what makes the archetype resistant to Probe B operator 20 (§5.3).

**`scope`: `"local"`, i.e. omit the key.** A real-seam proof asserts *"this component works through the
real seam"*, which cannot pass before its implement task's action has run — so it **fails the #125
union-safe test** and must not be tagged `scope: "integration"`. Getting this backwards on a
composition-root guardrail already cost two unrelated parallel siblings a rollback-and-retry (#250). The
shipped archetype is silent on `scope`; #120's neighbouring section is not. **This is a real gap and the
silence is the bug** — the two archetypes sit adjacent in the same file and a reader will carry #120's
`scope` discussion across without carrying its conclusion.

**Boundary rule, in its final form** (replacing the shipped rule of thumb): *the component under test is
constructed with the real implementation of its declared dependency; that implementation's own
dependencies may be substituted, because each is its own ledger row.* One real level, and no further.

---

## 5. Interaction with #468 and #479

### 5.1 #468's rung ordering — an EXTENSION, and a hard floor

The archetype is restated as a rung-1 instance and gains an explicit **no rung-3 form** floor (§4). This
is new text; the shipped archetype predates the ordering and never places itself in it. Concretely, the
catalogue's rung list gains the real-seam case as a named rung-1 example, and the archetype gains one
sentence forbidding the source-grep degradation.

### 5.2 AGREEMENT vs real-seam — a NEW disambiguation, and a real gap today

#468's **AGREEMENT property test** and #382's **real-seam contract test** landed independently, are
adjacent in the catalogue, and are both "the answer when a regex won't do." A reviewer can substitute one
for the other and believe they have complied. They answer different questions:

| | AGREEMENT (#468) | Real-seam (#382) |
|---|---|---|
| the question | *does X **agree with** Y?* | *does X **work through** the real Y?* |
| the defect | **drift** between two implementations of one policy | a **contract the fake silently satisfies** and the real one does not |
| the shape | enumerate the domain, evaluate **both** sides, assert equality | construct **one** side for real, assert an effect only it emits |
| passes when | an inlined copy is equivalent **today** | never, if the real seam rejects the input the fake accepted |
| the motivating case | a resolver required to consume a shared predicate | `CriticalityJudge` over the real `ClaudePromptRunner` |

**Neither substitutes for the other.** An AGREEMENT test between a fake and a real implementation is
*worse than nothing* — it certifies that a fake you wrote matches a real thing you never ran. One
paragraph in the catalogue, cross-linked both ways.

### 5.3 #479 Probe B — NEW operator 20

Probe B applies *the cheapest edit that satisfies the guardrail's literal text without delivering the
capability*, then re-runs. It has 19 enumerated operators and none of them can game a real-seam guardrail,
because none of them is about **which object the test constructs**. Add:

| # | operator | dies against |
|---|---|---|
| **20** | satisfy a real-seam / composition-root filter with a test that **constructs the fake** — same class, same name, a substituted seam | the test asserting an effect **only the production implementation emits** (a written stream log, a journal decision record), never merely that the collaborator was called |

This is the highest-value single item in the design and the cheapest to add: it is the *only* mechanical
check anywhere in the pipeline that can distinguish a real-seam test from one that is real-seam in name
only. It is also the operator that keeps §4's assertion requirement honest, since without it that
requirement is prose.

**A caveat to write into the operator, not to hide.** Probe B mutates the target tree at review time,
before the run has authored anything, so operator 20 applies to a **plan being re-reviewed against an
existing implementation** (a resumed or regenerated plan, an amendment to a landed wave). On a greenfield
first review the test does not exist and the operator is inapplicable — record it as *not run* rather than
as *passed*. #479's own reporting rule already requires naming skipped probes; this inherits it.

### 5.4 #479 Probe A — no change needed

A real-seam guardrail on the implement task must be RED at baseline, and Probe A already asserts exactly
that for every guardrail. Nothing to add. Worth stating so nobody adds it twice.

### 5.5 Summary — extends vs. new

| | |
|---|---|
| **Extends** | #468 rung ordering (a named rung-1 instance + a no-rung-3 floor); #479 Probe B (operator 20); #120's degradation ladder (reused, not re-invented); #120's `scope: "local"` ruling (carried across); #455 filter scoping, #179 re-emit, #75 coverage manifest (all reused unchanged) |
| **New machinery** | the seam ledger; the four-bucket closed classification incl. the N4 trap; the one-level-and-no-further clause; the T\* placement test; the terminal join-check redundancy rule; the AGREEMENT/real-seam disambiguation |
| **Deliberately not built** | any `validate` code; any schema field; any harness behaviour; any new guardrail kind |

---

## 6. The #378 boundary — one root, two mechanisms, no overlap

Both issues fire on the same terminal sink, from opposite sides. The division, written as a rule future
agents inherit:

| | #378 | #382 |
|---|---|---|
| owns | the **size and shape** of a task | the **placement of proof** |
| reads | `writeScope` cardinality, `action.maxTurns`, `dependsOn` fan-in | which seam a test substitutes, and where the real-seam proof lives |
| mechanism | **GR2042** (deterministic WARN) + split-trigger (e) | the ledger + placement rule + the archetype (review-audited) |
| verdict shape | "this task is too big" | "this proof is in the wrong task" |

**The non-overlap rule, to be written into both skills:**

- **#382 never adds a lint that reads `writeScope`, `action.maxTurns`, or `dependsOn`.** Those three
  fields are GR2042's, exclusively.
- **#378 never adds a rule about what a guardrail proves.** That is #382's.

> **CORRECTED 2026-08-20 (doc 19, #474/#477).** The first clause as written is **already false**: GR2041
> `MissingWriteScope` reads `writeScope` and is not GR2042. The boundary was never about which FIELD a lint
> touches — it is about which VERDICT it derives. Restated, and this is the binding form:
>
> **GR2042 owns `writeScope` CARDINALITY and SHAPE as evidence about a TASK'S SIZE.** Another lint may read
> `writeScope` as a **coverage set** — *"does any task claim this path?"* — exactly as GR2041 already does.
> A coverage lint never comments on task size, never suggests splitting or narrowing, and never reads
> `action.maxTurns` or `dependsOn`.
>
> Honouring this **improved** doc 19's check rather than constraining it: #474's own proposal wanted "or in
> any ancestor's output", which needs `dependsOn`. Taking the union of ALL tasks' `writeScope` instead is
> both strictly more conservative and boundary-clean.

**Where they meet, and it is worth wiring:** GR2042's *detection* is right but its implied *remedy* is
incomplete. Told "this task is over-scoped", an author's reflex is to chop the `writeScope` — which for a
fan-in sink produces N small tasks that still contain the first exercise of every real path. The
concentration survives the split. The remedy is #382's: **relocate the proof to T\***, and *then* the sink
is small because there is little left in it. So GR2042's message gains a pointer:

> …over-scoped. If this task is a composition-root / fan-in sink, check first whether it is large because
> it **concentrates real-seam proof** that belongs at each collaborator's own task (#382, seam ledger) —
> relocating the proof is the fix; narrowing `writeScope` alone leaves the concentration in place.

One string in `PlanValidator`, one sentence in SSOT §3.4 (§10), landing together per invariant 4. **This
is the only production code this design touches**, and it belongs to whoever is currently working #378 —
not to a second agent editing the same validator in parallel.

---

## 7. Worked example — the two dogfood bugs, re-authored

The test of the design is whether it catches its own motivating evidence, in scope, early.

**Bug A.** Ledger row `CriticalityJudge → IPromptRunner`, bucket **E** (production `ClaudePromptRunner`
launches the `claude` child process). E owes proof and E is always feasible. T\* = task 09 (the judge's
implement task; `ClaudePromptRunner` already exists). Task 08 authors, RED, a test constructing the judge
with a real `ClaudePromptRunner` pointed at a stub CLI; task 09's guardrail runs it. The judge's empty
`StreamLogPath` throws on construction of the invocation → **RED at task 09**, in a task whose
`writeScope` contains `CriticalityJudge.cs`. **Fixable in-scope, by the retry budget, at the point of
authorship.** The assertion requirement is met: `Assert.NotEqual(EscalateOnError, v.Source)` is an effect
only the real runner can produce a verdict about.

**Bug B.** Two rows. `RetryLoop → IDelay` is **N4** — exempt, the wait. `RetryLoop → ITransientBackoff` is
**C** — the waiter, and the substitute contains a decision, so N4 does not reach it. C owes proof; the
real `TransientBackoff` is constructible with process-boundary stubs beneath it, so no degradation.
T\* = task 11. The real backoff runs a class-(b) transient inside the pause budget and the test asserts
the **journal contains a `blocker-retried` decision** — an artifact only the real path writes. Silent
handling → **RED at task 11.**

**Task 16, re-scoped.** With both proofs upstream, the terminal wiring task asserts only assembly:
`SchedulerFactory.Create` constructs and injects judge, backoff and scheduler, with a contrast case. Its
`# catches:` now names a defect that survives both upstream proofs — *the factory never hands the judge to
the scheduler* — which is a real, distinct, composition-level defect. Its `maxTurns` and `writeScope` fall,
and GR2042 stops firing **because the underlying concentration is gone**, not because a threshold was
tuned. Both issues resolve from one change, which is the evidence that they share a root.

---

## 8. Phasing — v1 and deferred

**v1 — skills and docs only. One string + one SSOT sentence in the harness, owned by #378.**

| # | change | file |
|---|---|---|
| V1 | Replace the "where feasible" sentence with §1: buckets, N4 trap, one-level clause, T\* placement, join-check | `plan-breakdown/SKILL.md` (Step 4) |
| V2 | Emit the ledger in Step 4; place proofs by T\* in Step 5; print the ledger in the Step 7.4 report | `plan-breakdown/SKILL.md` |
| V3 | Restructure the archetype: rung-1 statement, no-rung-3 floor, `scope:"local"` ruling, assertion requirement, final boundary rule, induction paragraph | `references/guardrail-catalogue.md` |
| V4 | The AGREEMENT ⟷ real-seam disambiguation, cross-linked both ways | `references/guardrail-catalogue.md` |
| V5 | Bucket-worked .NET examples (E: real runner over stub CLI; C: real backoff, journal assertion) + the assertion requirement | `references/stacks/dotnet.md §10e` |
| V6 | Probe upgrade: read the ledger, recompute T\*, check the join-check's `# catches:`; ledger absence = finding | `guardrails-review/SKILL.md` |
| V7 | **Probe B operator 20** + its not-applicable-on-greenfield caveat | `guardrails-review/SKILL.md` §2b |
| V8 | The non-overlap rule (§6), one paragraph, in both skills | both `SKILL.md`s |
| V9 | GR2042 remedy pointer — message string + SSOT §3.4 sentence, **together** | `PlanValidator.cs`, `02-schemas-and-contracts.md` |
| V10 | Self-update: the two-disciplines note gains the ledger and the boundary rule | `guardrails-domain-knowledge` |

**Deferred, each with its trigger:**

- **GR2061 + a declared ledger field** — trigger: three consecutive breakdowns emit no ledger (§3.4).
- **The #350 vetted-library entry** — the archetype is a strong parameterization candidate
  (`component`, `seam interface`, `production type`, `boundary stub`, `observable effect`), but it belongs
  to #350's mechanism. Building a one-off parameterization here would be the second half-overlapping
  mechanism this design exists to avoid.
- **Cross-wave U rows.** Under JIT wave breakdown (#360/#385) a later wave's tasks do not exist when an
  earlier wave is authored, so a U row cannot always name a receiving *task*. v1: name the receiving
  **wave**, and the wave's own breakdown resolves it to a task. v2: enforce at the wave-entry gate
  (#254) — the natural home, since the wave gate is the only place both waves' graphs are visible.
- **Ledger carry across a regeneration** — a regenerated breakdown re-derives the ledger from scratch and
  can silently drop a row. Out of scope; the `guardrails.baseline` merge (SSOT §11) is where it would live.

---

## 9. What evidence would tell us v1 was wrong

Stated before implementation, so the verdict is not written after the fact:

1. **A wave with a complete, correctly-placed ledger still surfaces an integration bug only at the
   terminal gate.** Then one real level is not enough — the bug lived two levels down, or it lived in the
   assembly order the join-check was supposed to own and did not. *Response:* the join-check's `# catches:`
   requirement is under-specified, not the level rule.
2. **Rows classified N that were really E or C.** The closed list is being read as a category. *Response:*
   the list needs narrowing, or N needs to stop being self-service and become a reviewer-granted
   exemption.
3. **Ledgers absent rather than wrong.** → GR2061's gate has opened (§3.4).
4. **Real-seam tests that pass while the real path is broken.** The assertion requirement is not landing —
   authors are asserting "the collaborator was called". *Response:* Probe B operator 20 must become
   mandatory rather than best-effort, and the archetype needs a worked *negative* example.
5. **Total wave wall-clock rises materially without a fall in `needsHuman` halts.** The trade did not pay.
   *Response:* restrict the rule to E rows only (the bucket with the sharpest measured evidence) and let C
   rows fall back to the #120(b) form.

---

## 10. Proposed SSOT delta (specified, NOT applied in this pass)

`docs/plans/02-schemas-and-contracts.md` is `eol=lf`-pinned and byte-compared by `SchemaDriftTests`, and
may be under concurrent edit. **This design does not touch it.** The delta is specified here for the
maintainer to sequence.

**One sentence only, and only if V9 ships.** In **§3.4 (Write-scope check / GR2042 `StructuralOverScope`)**,
appended to the paragraph describing the warning:

> When the flagged task is a composition-root or fan-in sink, the first remedy to test is **relocation of
> real-seam proof**, not narrowing `writeScope`: such a sink is frequently over-scoped *because* it
> concentrates integration proof that belongs at each collaborator's own task (issue #382,
> `docs/plans/18-integration-proof-proximity.md`). Narrowing the scope alone leaves the concentration in
> place and moves the halt rather than removing it.

**No other SSOT change.** Specifically: **no new `§4.x` validate-check subsection** (there is no new
check), **no `task.json` field**, **no guardrail-sidecar key**, **no GR code allocated** (`GR2061` stays
next-free and reserved). Should the review direct GR2061 into v1, the delta becomes a new §4.8 plus a
`task.json` field, and that is a materially different design that should be re-reviewed, not amended in.

---

## 11. Devil's-advocate self-critique

**The strongest counter-argument: "you declined a lint in a product whose whole thesis is deterministic
gates, and dressed the retreat up as a layering insight."** It deserves the top slot because it is the
one a reader will reach for first.

*Response.* The decisive fact is timing, not taste: **at validate time the artifact carrying the defect
has not been written**. A lint over the prose that requests a fake fails the maintainer's own standing
test on its first correct input — every clock, every RNG, every env reader is a correct implementation the
lint rejects. §4.7 already draws this line explicitly, listing what "deliberately stayed OUT of validate"
because it requires execution. And the design does ship a deterministic gate; it ships it at the layer
where the evidence exists. **But the concession is real and I will not soften it:** v1 has **no
author-time deterministic backstop**, so a plan-breakdown that simply ignores this rule sails past
`validate` in silence. That is a genuine hole, its remedy is named (GR2061), and its gate is stated in
advance rather than left to be argued later.

**"One real level down multiplies test cost across every task."** Partly true and worth stating plainly.
Mitigations: the rule fires only on E and C rows, not on every dependency; the cost is **moved, not
added** — the terminal sink was already paying it, at the worst point in the run, with a retry budget that
could not fix anything; and the measured instance is two extra `[Fact]`s against a 75-turn sink. Residual:
wall-clock rises modestly. The trade is a fixable red versus a `needsHuman`, and §9.5 is its falsifier.

**"'One real level' collapses when the real seam drags in half the world."** The sharpest technical
objection. Constructing a real `Scheduler` needs a journal, a workspace, a repository — and the "real-seam
test" becomes an end-to-end test in a unit test's clothes, slow, flaky, and quietly downgraded by the next
author. §1.3's construction bound is the answer and it is deliberately literal: *if you must build a
second real level to build the first, you have left the rule* — degrade to #120(b) and name it. Residual:
the bound is judged by the author, who is motivated to find it exceeded. Probe B op 20 does not help here
(a #120(b) proof is a different assertion). This is the design's softest joint and I would rather label it
than hide it.

**"LLM authors will perform the ritual — a test named `*_RealSeam` that constructs the real type and
asserts something trivially true."** Almost certainly, sometimes. Three defences: the TDD-red pairing (a
trivial assertion rarely goes red against an unimplemented component), the assertion requirement (an
effect only the production implementation emits), and Probe B op 20. Residual: a real-seam test can be red
for the *wrong* reason — it does not compile. #155's stub-based TDD rule covers part of that and should be
cited from the archetype.

**"The four buckets are a taxonomy, and taxonomies get mis-applied — N is a silent self-service
exemption."** The strongest objection to the falsifiability claim, and the reason N is a **closed
enumeration of four items** rather than a category, and the reason the N4 trap is written out from the
motivating bug. A closed list is checkable; a category is a hiding place. Residual: N3 ("ambient
environment reader") is the loosest of the four and could be stretched to cover a configuration
*provider* that makes decisions. If §9.2 fires, N3 is where it will fire, and the fix is to split it.

**"The ledger is a document, and documents rot."** True, and it is why the ledger is deliberately a
*report* artifact rather than a plan-folder one in v1: a stale report is visibly a report, whereas a stale
declaration in `task.json` would be read as contract. When GR2061's gate opens, the rot problem arrives
with it and must be answered then.

**"This is a methodology change touching how every wave is decomposed — that is a lot of blast radius for
a two-bug sample."** Fair on sample size. The mitigating facts: the two bugs are structurally identical,
not two draws; the same root has now produced #120's three-in-one-plan recurrence and #378's sink; and v1
is entirely skills-side, so the blast radius of being wrong is *edited prose*, reversible in one commit.
If this needed a schema field or a harness phase, the sample would not justify it — which is a large part
of why it does not have one.

---

## 12. Decisions

- **D1.** #382's v1 adds **no `guardrails validate` code and no GR code.** The defect's carrier does not
  exist at validate time; the only pre-run signal is prose whose correct and incorrect forms are
  identical. `GR2061` stays next-free and reserved.
- **D2.** "Where feasible" is replaced by a **closed four-bucket classification** (N exempt, E/C owed,
  U relocated), with N as a **four-item enumeration** and the **N4 trap** (fake the wait, never the
  waiter) written out.
- **D3.** The rule is **one real level down, and no further** — the component is constructed with the real
  implementation of its declared dependency; that implementation's own dependencies are their own ledger
  rows. Composition is then proven by **induction**, leaving only assembly for the terminal check.
- **D4.** Placement is **T\***, the earliest task at which both production types exist, **computable from
  the emitted DAG**. Proof later than T\* is a finding that must name T\*.
- **D5.** The terminal composition proof is a **join-check**: its `# catches:` must name a defect that
  survives every upstream real-seam proof passing, or it is redundant / mis-placed.
- **D6.** The archetype **stays in the catalogue**, restated as **rung 1** with **no rung-3 form**,
  `scope: "local"`, an **assertion requirement** (an effect only the production implementation emits), and
  the degradation ladder reused from #120 rather than re-invented.
- **D7.** **Probe B gains operator 20** — the only mechanical check that separates a real-seam test from
  one that is real-seam in name only.
- **D8.** A **new AGREEMENT ⟷ real-seam disambiguation** ships; neither substitutes for the other, and an
  AGREEMENT test between a fake and a real implementation is worse than nothing.
- **D9.** **The #378 boundary is a rule, not an intention:** #382 never lints `writeScope` / `maxTurns` /
  `dependsOn`; #378 never rules on what a guardrail proves. GR2042's message gains a **relocation-first**
  remedy pointer, which is this design's only production-code touch and belongs to #378's workstream.
- **D10.** The ledger is a **report table**, not schema. Deferred to a declared field + GR2061 on a stated
  evidence gate.

### Rulings added during M1 (2026-08-20)

M1 applied this design and hit five under-specified points. Three it resolved as strict supersets of what
was written (the ledger's `proof` path is **plan-folder-relative**, so a row whose task segment disagrees
with its `T*` cell is self-evidently inconsistent; the ledger **heading is always emitted**, with a
no-rows sentence, so a clean plan and a skipped analysis are distinguishable — **M2 keys its finding on
the missing HEADING, not the missing table**; and the report clause lands at **Step 7.4**, beside #468's
source-shape ledger, because "Step 6" was a stale number — Step 6 writes the folder). Two needed a
decision and are ruled here:

- **D11 — the construction bound is bucket **C** only; an **E** row can never invoke it.** §1.2 says E is
  *always* feasible while §1.3 states the bound generally, and an author would use the gap to degrade an E
  row by claiming a second real level. Closed **by definition rather than by judgement**: what sits
  beneath an E seam *is* a process / network / disk boundary, and faking that is the one substitution the
  rule has always permitted — so constructing a real E adapter never forces a second real level. An E row
  claiming the bound is a review finding, not a degradation.

- **D12 — #120's forbidden "constructs `FooImpl` itself and injects it" and #382's requirement to do
  exactly that are the same verb in DIFFERENT SLOTS.** #120 forbids injecting the collaborator into the
  **assembler's** slot, which bypasses the production assembler so the *wiring* is never proven. #382
  requires injecting into the **component-under-test's own constructor**, which proves the component
  through its collaborator and claims nothing about the assembler. Operationally: a real-seam test never
  calls `SchedulerFactory.Create`, and a composition-root test never hand-injects. **If one test does
  both, it is two tests.** The two sections sit adjacent in the catalogue and §5.2 disambiguates a
  different pair, so without this ruling a reader meets flatly opposite instructions.

- **D13 — the ledger has NO HOME ON DISK in v1, and that is now the strongest live evidence for
  GR2061's gate.** M2 found it while implementing: M1 prints the ledger in the **Step 7.4 breakdown
  report**, which is conversation output, while `/guardrails-review` routinely runs in a *fresh session
  against a folder path*. So "absent ledger ⇒ the analysis never ran" has a **third** state this design
  never named — *not produced to this pass* — which is neither a clean plan nor a skipped analysis.
  M2 handled it correctly (ask for it → record an unchecked-gap line → fall back to re-deriving from the
  folder), and **reporting one state as the other would manufacture a BLOCKER out of a missing
  attachment**.
  This is NOT closed by inventing a file. A plan folder has no persisted-report convention today
  (`diagram.*`, `guardrails.json`, `state/`, task and wave folders — nothing else), and adding one is
  exactly the declared-field change §3.4 defers. **The consequence for M3 is explicit: its golden-folder
  round-trip asserts the FOLDER-OBSERVABLE half — a real-seam guardrail lands on T\*, not on the terminal
  task — and does NOT assert a ledger row, because a round-trip cannot read conversation output.** If that
  proves too weak in practice, that is precisely the evidence §3.4 asks for, and the answer is GR2061 plus
  a declared field, not a bespoke file invented here.

**One v1 item has no milestone owner: V10** (`guardrails-domain-knowledge` self-update). Its two-disciplines
note still teaches the retired rule of thumb — *"faking only the process/CLI boundary underneath, NEVER the
in-process seam itself"* — and knows nothing of the ledger, the buckets, or T\*. Superseded phrasing in a
knowledge skill every agent loads is how a retired rule outlives its replacement. **Assigned to M2.**

---

## 13. Implementation handoff (after the #106 review of this draft)

Sequenced; the skills edits are the whole of v1.

**M1 — `guardrails-skill-author` — the authoring rule.**
`filesTouched`: `.claude/skills/plan-breakdown/SKILL.md`,
`.claude/skills/plan-breakdown/references/guardrail-catalogue.md`,
`.claude/skills/plan-breakdown/references/stacks/dotnet.md`.
Delivers V1–V5 and the plan-breakdown half of V8. The archetype restructure (V3) must not fork the shipped
text — it edits the existing "Drive-the-real-seam" section in place, since a second section would be the
duplication this design exists to prevent.

**M2 — `guardrails-skill-author` — the review audit (after M1; it reads M1's ledger format).**
`filesTouched`: `.claude/skills/guardrails-review/SKILL.md`.
Delivers V6, V7 and the review half of V8.

**M3 — `guardrails-test-author` — the meta-test.**
`filesTouched`: `tests/**` only.
A golden-folder round-trip proving a breakdown over a fixture plan with a faked E seam emits (a) a ledger
row and (b) a real-seam guardrail on T\* rather than on the terminal task. This is the only durable
evidence the rule was applied, since none of it is `validate`-enforced — and its absence would make v1
unverifiable, which invariant 5 does not permit.

**M4 — `guardrails-harness-developer` — GR2042's remedy pointer (may run in parallel; coordinate with
#378).**
`filesTouched`: `src/Guardrails.Core/Loading/PlanValidator.cs`,
`docs/plans/02-schemas-and-contracts.md` (the §3.4 sentence, §10 above), plus the GR2042 message assertion
in `tests/**`. String and SSOT sentence land **together** (invariant 4). If #378's current workstream is
already editing this validator, M4 folds into it rather than racing it.

**Not handed off:** GR2061, the declared ledger field, the #350 library entry, cross-wave U enforcement.

---

## 14. Proposed plan-document edits

Proposed, not applied — for approval before this doc is committed.

1. **`docs/plans/README.md`** — index entry:
   `18-integration-proof-proximity.md — Integration-proof proximity: the seam ledger and the one-real-level rule (#382). Where a component's real-seam proof lives, why it is not a validate lint, and the #378 boundary.`
2. **`docs/plans/03-roadmap.md`** — under the deferred/v2 material, one line:
   `GR2061 + a declared seam ledger (deferred from #382 — see 18-integration-proof-proximity.md §3.4 for the evidence gate that opens it).`
3. **`docs/plans/09-preflight-first-class.md` §"Plan-level guardrails"** — one cross-reference sentence:
   `The terminal folder is a JOIN-CHECK over already-proven parts, never the first place a real path is exercised (#382 — 18-integration-proof-proximity.md §1.5).`
4. **`docs/plans/02-schemas-and-contracts.md` §3.4** — the single sentence in §10 above, sequenced by the
   maintainer, landing with M4's message string. **Not applied in this pass.**
