# Architecture: producer coverage — a gate must not require what nothing in the plan can produce

> **Status: PARTIALLY IMPLEMENTED as of 2026-08-20.** Issues: **#474** (task altitude), **#477** (plan
> altitude).
>
> | piece | state |
> |---|---|
> | Milestone A — skill half (missing-insertion extended to gate folders; the reachability probe; the sibling-datum trace) | **SHIPPED** `e118b9d` |
> | Milestone B — `intendedWaves` + **GR2062** + the "intends N, declares M" line | **SHIPPED** |
> | Milestone A — harness half (**GR2060**) | **NOT BUILT** |
>
> The maintainer waived #106's draft-PR gate for this document and directed straight-to-implementation.
> **#474 is NOT closed** — its headline (reachability) is permanently review-only by §2.2, and GR2060,
> which covers the narrower coverage shape, is still unbuilt. **#477's mechanism is shipped.**
>
> One correction the implementation forced, recorded because the reasoning generalises: Milestone B's
> false-positive zero is **STRUCTURAL, not empirical.** No committed plan folder carries `intendedWaves`
> yet, so the corpus sweep proves only that the check is silent where the field is absent — which is the
> skip condition, not the check. GR2062's real conservatism evidence is the `planIsClosed` matrix in its
> tests, not the sweep. GR2055/2056/2057 earned an empirical zero against 500+ real scripts; this has not,
> and the distinction should not be blurred when the next lint cites the precedent.

---

## What's being asked

Two high-priority bugs, filed a run apart, both from `model-tiering-stage-2`:

- **#474** — task 13's guardrail required `AttemptJournaler.cs` to reference `Usage`. The journaler
  builds `AttemptRecord` from an `ActionRun`, and `ActionRun` carried no `Usage`; the severed link lived
  in `ActionRunner.cs`, inside an **already-merged sibling task's** `writeScope`. The agent's only
  in-scope moves were an honest halt or a false green. It halted. `validate`, `graph --check` and a full
  `/guardrails-review` all passed.
- **#477** — the plan's terminal gate required a behaviour (§6.5/D29, the verifier route) that **no
  declared wave delivered**, because the wave-3 stub was lost when a JIT breakdown truncated (#385).
  `validate` clean, `graph --check` clean, two review passes clean. The run then drained **20 tasks to
  green, $115.32**, full suite passing, conformance 9/9 — and failed at the terminal gate on one clause,
  after every task had run.

The thesis to test: these are the same defect at two altitudes, and the unification is real rather than
a rhyme. **Verdict: yes, and in a sharper form than either issue proposed** — see §1. The unification is
not "two instances of one rule"; it is **one check and the soundness precondition it rests on**.

### Ambiguity named, and the narrowing

"The same defect" could mean three different things, and they have different mechanisms:

| reading | #474 instance | #477 instance | decidable? |
|---|---|---|---|
| **(a) coverage** — the required content lives in a file **no task may write** | the `tierSource`/SSOT clause (#474 comment, third instance) | — | **yes** |
| **(b) reachability** — the file IS writable but the *value* has no in-scope route to it | the headline `AttemptJournaler`/`ActionRun` case | — | **no** (dataflow) |
| **(c) closure** — the set of producers is not fully declared, and nothing says so | — | the headline lost-wave-3 case | **only with a new declaration** |

This document narrows to: **(a) is v1 deterministic; (b) is review + authoring doctrine, permanently;
(c) needs one integer of recorded intent and is v1's second, separately-approvable milestone.** Anyone
who reads "unify #474 and #477" as "one lint that catches both headlines" will build a wolf. It cannot
be done, and §2.3 says exactly why with the evidence in hand.

---

## Placement

| piece | placement |
|---|---|
| **GR2060** — a gate requires content nothing in the plan can produce | **harness** (`PlanValidator`) + **schema** (SSOT §4.8, new) |
| **GR2062** + `intendedWaves` — the one-ahead shortfall | **harness** + **schema** (SSOT §2, §14.1) |
| the missing-insertion check extended to **plan- and wave-level gate folders** | **skill** (`guardrails-review`) |
| the **sibling-datum trace** before writing a `writeScope` | **skill** (`plan-breakdown`) |
| a lint over "which wave OWES this behaviour" | **out of scope, permanently** (§2.3) |
| a dataflow-reachability lint for #474's headline | **out of scope for v1**; no credible v2 bet either (§2.2) |

`GR2061` is **not** taken here — it stays reserved for #382's deferred seam-ledger lint (§7).

---

## Invariants in play

**1 — deterministic guardrails over prompt-judges; judges never alone.** GR2060 is the deterministic
half. It is deliberately *small*: it decides one relational question and is silent everywhere else. The
larger half of both issues stays with the review pass and the authoring skill, and this document says so
rather than inflating the lint to look productive.

**4 — the SSOT is the schema SSOT; a contract change lands in the SAME change.** Two contract changes
here: a new §4.8 (the family statement + GR2060) and `intendedWaves` in §2/§14.1 with GR2062. Both are
specified verbatim in §6 and land in the implementing PR, not before it.

**5 — honest halts; nothing is marked done unverified.** Both bugs *were* honest halts. #474's agent
refused a false green and said so; #477's terminal gate withheld delivery. **The system worked; it
worked at the most expensive point available.** This design moves the same verdict earlier. It must not
soften either halt to do so — no new escape hatch, no waiver field.

**6 — plain files, light setup.** GR2060 adds one process spawn to `validate` (`git ls-files`, once per
run, behind an injected probe), on the exact precedent GR2056 set for interpreter probes. No new
dependency, no daemon, no cache.

**2 — the harness is the single writer of merged state.** This is why GR2060 **skips any path under the
plan folder**: `state/`, `logs/`, the journal and `diagram.md` are harness-written and appear in no
`writeScope` by design. A coverage check that did not skip them would fire on every plan that reads its
own state.

---

## 1. One family — the invariant, named once

> **Producer coverage.** Every outcome a gate requires must have a **declared producer inside the plan**.

A "gate" is any of the six folder instances (§14.3): task `guardrails/`/`preflights/`, wave
`guardrails/`/`preflights/`, plan `guardrails/`/`preflights/`. A "declared producer" is a task whose
`writeScope` covers the path the outcome lives in. `writeScope` became **required on every task** with
GR2041 (#389) — which is the fact that makes this invariant checkable at all. Before #389 the union of
`writeScope`s was not a complete statement of what a plan may write, and no coverage question had a
sound answer.

**Why #474 and #477 are one family and not two rules that rhyme.** The relationship is not symmetry, it
is **dependency**:

- Producer coverage is decidable only over a **closed** declaration set — one where every task that will
  ever exist in this plan has already declared its `writeScope`.
- A waved plan with an un-authored JIT wave is **open** by construction, and "no task can write X" is
  then unprovable: wave N+1 might own X.
- **#477 is the case where the declaration set is open and nothing says so.** A plan with two authored
  waves and a lost third stub is byte-indistinguishable from a finished two-wave plan. The plan cannot
  even be *asked* whether its declaration set is closed.

So #477 is not a second instance of #474's defect. It is the **precondition on which #474's check
depends**, gone missing. That is a real unification — it produces one trigger condition shared by both
mechanisms (§3.3) — and it is stronger than what either issue proposed, both of which framed #477 as
"#474 one level up."

**Corroborating evidence that this is one root, not two.** Both defects were found on the **same script**
— `docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1` — weeks apart. Its
`tierSource` clause was #474's third instance (remediated by hand-authoring task 14); its §6.5/D29
behaviour clause was #477's. One gate, two unsatisfiable clauses, two different reasons nothing could
satisfy them.

---

## 2. What is mechanically decidable, and what is not

### 2.1 Decidable — the coverage shape (v1, GR2060)

The measured instance, verbatim from the terminal gate:

```powershell
$ssot = if (Test-Path "docs/plans/02-schemas-and-contracts.md") { Get-Content -Raw "docs/plans/02-schemas-and-contracts.md" } else { "" }
if ($ssot -cnotmatch 'tierSource') {
    $failures += "[12.4/Invariant 4] docs/plans/02-schemas-and-contracts.md does not mention tierSource - ..."
}
```

Three facts, all available to `validate`, all statically known: the gate requires the exact literal
`tierSource` in a named tracked file; the file does not contain it; **no task in any wave declares that
path in `writeScope`**. No agent action inside this plan can turn the clause green.

Apply the standing test — **can a correct implementation be written that this rejects?** No. The
predicate does not infer intent, does not read prose, and relates only *declarations to bytes that exist
right now*. There is exactly one way to be a false positive: a producer that exists but is not declared —
and GR2041 makes an undeclared producer a validation error already.

The exact predicate, with the conservatism spent line by line, is §3.1.

### 2.2 Not decidable — the reachability shape (#474's headline)

`AttemptJournaler.cs` was **in** task 13's `writeScope`. The token `Usage` could have been typed into it
in one edit. What was missing was a *value* to read: `ActionRun` had no `Usage` member, because
`ActionRunner.cs` — a different task's file — never set one. Deciding that requires resolving
`AttemptJournaler`'s parameter type, walking to `ActionRun`'s declaration, enumerating its members, and
knowing which of them the required expression would have to source from. That is C# semantic analysis
over a tree the run has not finished writing. It is not a lint, in v1 or v2.

**A weaker version is available and is deliberately declined.** One could flag "a guardrail requires
token T in file F, and T appears nowhere in the task's own scope" — #474's own proposal 3. It fires on
every correct test-authoring task in the corpus: authoring a RED test that names a not-yet-existing type
is the archetype the whole product is built on. That is the loudest wolf this family could ship.

The remedy for the reachability shape is §4 — a review probe and an authoring rule — and it stays there.

### 2.3 Not decidable — "which wave owes this behaviour" (#477's headline)

This is the one worth being ruthless about, because it is the expensive half and the temptation is
strong. The clause that actually failed:

```powershell
@{ Id = "6.5/D29  the judge resolves through the SAME resolver (strength bump; pinned-costly actor)"
   Pattern = '(?i)judge|verifier|strengthbump|mintier|pinnedactor' }
```

The requirement is *"a discovered test in `Stage2ConformanceTests` whose NAME matches this alternation."*
GR2060 is silent on it, for **three independent reasons**, any one of which suffices:

1. The pattern is an **alternation** — it de-regexes to no single exact literal, so there is no witness.
   (This is GR2057's shipped rule, and it is right: `judge|verifier|…` names five acceptable outcomes.)
2. The "file" is not a literal path at all. The subject is the output of `dotnet test --list-tests`.
3. The conformance test file **was** in a wave-2 task's `writeScope`. Coverage held. What did not hold
   is that anybody was going to add *that particular test*.

**And the tempting shortcut must be named and refused.** The gate's own failure message says *"wave 2
owes the first five; wave 3 owes the judge clause."* The ownership is written down — in a string
literal, in English. Keying a verdict on it repeats exactly the temptation `18-integration-proof-proximity.md`
§3.1 rejected for `# catches:` comments: it makes a **comment load-bearing for a verdict**, gameable in
one line, and inverts documentation into certification. Refused, on the same grounds.

**So say it plainly: GR2060 would not have caught #477's headline instance.** It would have caught the
other unsatisfiable clause in the same file. #477's headline is addressed by §2.4 — not by reasoning
about the gate, but by making the missing wave visible.

### 2.4 Decidable only with one new declaration — closure (#477, GR2062)

Is the intended wave count recorded anywhere machine-readable today? **No.** Verified:

- `guardrails.json` for the affected plan carries no wave information of any kind — it is the shared run
  config (`maxParallelism`, runners, retries) and nothing else.
- Nothing in the SSOT records a wave count, wave manifest, or plan→charter link. `diagram.md` is
  described as an "optional plan-level wave map" but it is a rendering, not a declaration, and
  `graph --check` regenerates it *from* the folders — it can never disagree with them.
- The charter (`docs/plans/model-tiering-stage-2.charter.md`, whose `s2-waved` answer settled three
  waves) is a **sibling of the plan folder with no reference from inside it**. Nothing in the plan folder
  knows the charter exists.
- The wave-2 `brief.md` carries the #365 step in prose. Briefs are optional, excluded from
  `PlanDefinitionHash`, and prose-keyed.
- And per #477's own comment, **the plan hash did not change when the stub was restored**
  (`sha256:5275f70ca1d8` before and after) — correctly, since a stub carries no behavioural definition.
  Every hash-based check in the harness is blind to a lost or gained wave, so this cannot ride on
  definition-hash staleness.

**What would have to exist: one integer.** `intendedWaves` in the waved plan's `guardrails.json`,
written once at plan-folder creation. Then the check is a comparison of two declarations — the shape
`18-integration-proof-proximity.md` §3.4 identifies as *"the only shape that is false-positive-free by
construction, because it relates declarations to declarations and infers nothing."*

**Does the weaker coverage check stand on its own without it?** Yes. GR2060's soundness rests on
"no un-authored wave exists" (§3.3), which is computable from the folders alone. `intendedWaves` is not
required for GR2060 to be sound — it closes GR2060's one residual *mis-attribution*: on a truncated plan,
GR2060's finding is **correct as declared but carries the wrong headline** ("add this file to a scope"
when the real fix is "you lost a wave"). With `intendedWaves`, the two diagnostics fire together and the
operator sees the cause, not just the symptom. That is a real but secondary benefit, which is why §5
makes it a separately-approvable milestone.

---

## 3. Design

### 3.1 GR2060 — `UnproducibleGateRequirement` (ERROR)

> A script guardrail requires an exact literal in a tracked workspace file that does not contain it, and
> **no task in the plan declares that file in its `writeScope`**.

Fires only when **all** of the following hold. Every condition is a place conservatism is spent, in
§4.7's idiom:

1. **PowerShell script guardrail**, from any of the six folder instances
   (`PlanValidator.FourFolderScriptGuardrails` already enumerates all six, including
   `plan.PlanGuardrails` — the terminal gate is in reach for free). `.sh` is out for v1 on GR2057's
   precedent: portable guardrails ship as `.ps1`+`.sh` pairs, so the pair is still caught.
2. **A statically-known path operand.** A `Get-Content` (any parameter form) whose path argument is a
   single-quoted literal, **or a double-quoted literal containing no `$` and no backtick**. The
   double-quote relaxation is required by the measured instance and is trivially sound for a path: with
   no `$` and no backtick the string is its own literal content. It is *not* extended to pattern
   operands, which stay single-quote-only (GR2057's rule — a double-quoted regex makes `$` ambiguous
   between anchor and interpolation).
3. **A one-hop variable association.** `$v = … Get-Content … '<path>' …`, where `$v` is assigned
   **exactly once** in the script and that statement names **exactly one** statically-known literal path.
   More than one assignment, or more than one path, → skip. (The measured instance's
   `$ssot = if (Test-Path "…") { Get-Content -Raw "…" } else { "" }` satisfies this: one assignment, one
   distinct literal path.)
4. **A requirement clause with a witness.** `if ($v -cnotmatch '<pat>')` / `-notmatch`, single clause,
   single-quoted literal operand, in a branch whose **polarity is a requirement** — GR2057's shipped
   polarity reader: the block appends to a `$failures` accumulator, exits non-zero, throws, or
   `Write-Error`s. And `<pat>` must **de-regex to one exact literal witness** — GR2057's shipped
   extractor, including its re-test of the witness against its own pattern so a mis-extraction drops the
   clause. Any alternation, group, class, quantifier or `\w`-class → no witness → silence.
5. **The witness is absent from the file's current bytes.** Case-sensitive iff the operator was
   `-cnotmatch`. If the witness is present, the clause is satisfiable today and there is nothing to say.
6. **The file is tracked by git** — one `git ls-files -z` per validate run, behind an injected
   `IGitTrackedFileProbe` mirroring `IScriptSyntaxProbe`. **This condition, not a heuristic, is what
   eliminates the build-output false-positive class**: a gate grepping `TestResults/results.trx` or an
   `artifacts/` file names something no author would ever put in a `writeScope`, and an untracked
   generated artifact must never produce a finding. **Probe absent, git absent, or the call fails →
   silence, not failure** (GR2056's "silence is not proof of validity" — punishing the operator for a
   missing tool is wrong, and a machine with no git cannot run worktree mode anyway).
7. **The path is not under the plan folder.** `state/`, `logs/`, the journal and `diagram.md` are
   harness-written (invariant 2) and appear in no `writeScope` by construction.
8. **No task declares the path**, evaluated with **`WriteScope.IsInScope`** — the same predicate the
   harness enforces at write time, so a glob or directory-prefix entry counts as coverage and the lint
   cannot disagree with the runtime check. Evaluated over the **union of every task's `writeScope` in
   every wave**, plus each task's declared `stagingOutputs` `to` paths if those are not already required
   to be in `writeScope` (implementer: verify against §3.5 and drop the clause if redundant).
9. **Every task declares a `writeScope`** — if GR2041 fired anywhere, the union is incomplete and GR2060
   must be silent.
10. **No un-authored wave** — see §3.3.

**Message shape** (the remedy must be actionable and must never suggest deleting the requirement first):

> `GR2060` — this gate requires the literal `tierSource` in `docs/plans/02-schemas-and-contracts.md`.
> The file does not contain it, and **no task in this plan declares that path in its `writeScope`** — so
> no task can make this gate pass, and the run will spend its whole DAG before finding out. Either give
> some task that file in its `writeScope` (and the work of writing it), or the requirement does not
> belong in this plan.

**Implementation note.** Regex over comment-stripped text, same discipline as GR2055/GR2057 — **not
AST-based**. `IScriptSyntaxProbe` is out-of-process and returns verdicts, not trees, and taking an
in-process PowerShell parser dependency into `Guardrails.Core` for one lint is not a trade worth making.
If the implementer finds an in-process AST genuinely cheap, it strictly improves conditions 2–4 and is
welcome — but it is not required, and it must not become the reason this slips.

### 3.2 GR2062 — `IntendedWaveNotDeclared` (WARNING)

> A waved plan's `intendedWaves` exceeds the number of wave folders it declares, **and every declared
> wave is authored** — so the one-ahead invariant (#365) is not merely pending, it is gone.

The second conjunct is what stops this becoming a warning that fires on every healthy mid-plan run and
gets ignored. Traced against the real plan:

| plan state | intends | declares | un-authored wave? | GR2062 |
|---|---|---|---|---|
| healthy mid-plan (waves 1–2 authored, wave-03 stub present) | 3 | 3 | yes | silent |
| healthy, one-ahead pending (wave-02 stub present) | 3 | 2 | yes | silent |
| **stage-2 as broken** (waves 1–2 authored, stub lost) | 3 | 2 | **no** | **WARN** |
| finished 3-wave plan | 3 | 3 | no | silent |
| author collapsed 3 waves into 2 and updated the field | 2 | 2 | no | silent |

`intendedWaves < declared` also warns (the plan grew past its stated intent), same message, other
polarity. `intendedWaves` **absent → GR2062 is skipped entirely**; the field is optional and no existing
plan is forced to migrate.

**WARN, not ERROR**, exactly as #477 argues: a genuinely final wave has no successor, and an author may
legitimately collapse waves. The value here is not enforcement — it is that a missing wave becomes
**nameable**. Today nothing in the plan can be asked the question.

**The minimum ask, satisfied.** `guardrails validate` and `guardrails plan` gain one line on a waved
plan: `Waves: 3 intended, 2 declared (1 not yet created)` — or `3 declared` when they agree, or `2
declared (intent not recorded)` when the field is absent. That is #477's explicit floor.

### 3.3 The shared trigger — why this is one design

Both checks turn on the same computed fact:

> **`planIsClosed` = the plan has no declared wave folder with zero tasks** (and, for a non-waved plan,
> trivially true — there is no JIT).

- `planIsClosed == false` → **GR2060 silent** (a future wave may own the file) and **GR2062 silent** (a
  shortfall is expected — that *is* the one-ahead invariant working).
- `planIsClosed == true` → **GR2060 evaluates** (the declaration set is complete, so "nothing can write
  this" is provable) and **GR2062 evaluates** (if you intend more waves and there is no stub ahead, the
  invariant is broken).

One predicate, two verdicts. This is the concrete payoff of the §1 unification, and it is the reason to
build these together rather than as two unrelated lints.

### 3.4 Seams and contracts touched

| seam | change |
|---|---|
| `PlanValidator` | two new private checks, registered alongside the §4.7 three |
| GR2057's de-regex witness extractor + polarity reader | **extract to a shared private helper first**, then reuse. Refactor-then-add: no behaviour change to GR2057, proven by its existing tests staying green |
| `IGitTrackedFileProbe` / `GitLsFilesProbe` / `NullGitTrackedFileProbe` | **new**, `src/Guardrails.Core/Loading/`, mirroring `IScriptSyntaxProbe` exactly |
| `WriteScope.IsInScope` | **reused unchanged** — the lint and the runtime check must not diverge |
| `PlanValidator.FourFolderScriptGuardrails` | **reused unchanged** — already covers the terminal gate |
| `RunConfig` / `PlanJson` / `PlanLoader` | `intendedWaves` (`int?`, optional) |
| `DiagnosticCodes` | GR2060, GR2062; next-free advanced to GR2063 |
| **not touched** | `IPromptRunner`, `IProgressSink`, `IActionRunner`, the Scheduler, any run-time path. This is author-time only |

---

## 4. What stays with the skills — the larger half

Neither issue's headline is a lint. Both are addressed here, and this is not a consolation prize:
`18-integration-proof-proximity.md` reached the same structure for #382 and was right to.

**`guardrails-review` — two changes.**

1. **The §4 missing-insertion check explicitly covers the plan-level and wave-level gate folders**, not
   just `tasks/*/guardrails/`. #474's comment measured this precisely: the probe that would have caught
   the `tierSource` clause *already existed* and was simply never pointed at the terminal gate, because a
   gate reads as infrastructure rather than as a check with dependencies. This is the single
   highest-value change in the document and it costs one sentence.
2. **A new reachability probe for the #474 headline shape.** For every guardrail asserting *"file X
   contains/does Y"* where Y is a value produced elsewhere: identify the type that carries the value into
   X, and confirm the file declaring that type is in the same task's `writeScope`, or in an ancestor's
   already-merged output. The reviewer's question, phrased so it cannot be answered by reading the
   guardrail alone:

   > **If the agent edits only the files it is allowed to, is there anything for the target file to
   > read?**

   → **BLOCKER**, naming the severed link.

**`plan-breakdown` — the sibling-datum rule.** For a task whose deliverable is *"datum D reaches sink
S"*, find the **nearest existing datum that already makes the whole trip** and enumerate every file it
passes through. If the new datum's `writeScope` does not cover the same set, the scope is wrong — split
the unreachable hop into its own task. In #474 the sibling was `CostUsd`, sitting in the same two files,
and one grep would have shown `ActionRunner.cs` on the path. Mechanical, no cleverness required.

**`plan-breakdown` — emit `intendedWaves`** when authoring a waved plan whose source records a settled
wave count, and keep the #365 stub step.

---

## 5. Phasing — v1, deferred, and the evidence that would falsify it

**Milestone A (v1).** GR2060 + the two skill changes. Self-contained; no new schema field; catches the
`tierSource` class of terminal-gate defect. Approvable alone.

**Milestone B (v1, separately approvable).** `intendedWaves` + GR2062 + the `validate`/`plan` reporting
line. Approvable independently of A; A does not depend on it.

**Deferred, designed, not built.**

- `.sh` support for GR2060. Ships when a `.sh`-only guardrail corpus exists (today every portable
  guardrail ships as a pair).
- The multi-hop variable association (`$a = Get-Content 'x'; $b = $a -replace …; if ($b -notmatch …)`).
  One hop covers the measured instance; more hops is more surface for no measured need.
- AST-based clause extraction, if an in-process parser ever becomes free.

**Never.** A dataflow-reachability lint (§2.2). A lint that reads gate prose to decide which wave owes a
behaviour (§2.3).

**What evidence would tell us v1 was wrong.**

1. **GR2060 fires on any correct plan** in the committed corpus → back it out. §4.7's bar is a *measured*
   zero false-positive rate and this check must meet it, not approximate it.
2. **Authors lower `intendedWaves` to silence GR2062** — observed in three consecutive breakdowns → the
   field became the escape hatch every self-declaration becomes; make the value review-visible or drop
   the field and keep GR2060.
3. **A fourth #474-shaped incident whose severed link is reachability, not coverage** → the review probe
   is not working, and the honest response is a stronger authoring artifact, not a smarter lint.
4. **GR2060 never fires in six months while review keeps catching coverage gaps** → the lint was
   unnecessary; the review probe was the whole answer. This is a real possible outcome and it should be
   recorded as one.

---

## 6. Proposed SSOT delta (specified, NOT applied in this pass)

`docs/plans/02-schemas-and-contracts.md` is mid-edit by other agents; these land in the implementing PR
per invariant 4.

**(i) New §4.8, immediately after §4.7 and before §5.** Heading:
`### 4.8 Guardrails that CANNOT PASS given what this plan BUILDS (validated, GR2060 — error)`.
Opening paragraph to state: the §4.7 three are decidable from **one script's own text**; this one is
**relational** — it reads the script, the union of every task's `writeScope`, and the workspace's current
bytes. Same consequence (red before the task runs, red forever, review structurally misses it because it
hunts weakness and this guardrail is *strong*), different evidence base, hence a sibling section rather
than a fourth row in §4.7's table. Carry the §3.1 predicate table, the ten conservatism conditions, the
`planIsClosed` suppression, and the cross-reference to §14.1/GR2062 as its soundness precondition. §4.7
gains one closing sentence pointing forward to §4.8.

**(ii) §2, the `guardrails.json` block** — one new optional key, documented as waved-plans-only:

```jsonc
"intendedWaves": 3,   // OPTIONAL, waved plans only (§14.1). How many waves this plan INTENDS, recorded
                      //   at plan-folder creation from the reviewed source. Compared against the wave
                      //   folders on disk by GR2062 (WARN). Absent = intent not recorded; GR2062 skipped.
```

**(iii) §14.1, appended to the validation list** after GR2034:

> - **GR2062** (warning) — **wave shortfall**: `intendedWaves` (§2) exceeds the number of declared
>   `wave-*` folders **and every declared wave is authored**, so the #365 one-ahead invariant is not
>   pending but gone. The second conjunct is load-bearing: during normal JIT authoring a plan legitimately
>   declares fewer waves than it intends, and a warning that fired then would be ignored. Skipped when
>   `intendedWaves` is absent. See §4.8 — the same `planIsClosed` predicate suppresses GR2060.

**(iv) §14.10's GR-code paragraph** — advance next-free to **GR2063**, recording GR2059 (#459), GR2060
(this document), GR2061 (RESERVED, #382's deferred seam-ledger lint), GR2062 (this document). Per that
paragraph's own standing instruction, `DiagnosticCodes.cs` wins — re-verify immediately before
allocating.

**Delta to `docs/plans/18-integration-proof-proximity.md` — stated, NOT applied** (that document is not
edited by this pass; the maintainer applies it):

- §3.4: the deferred seam-ledger lint's reserved code moves **GR2059 → GR2061** (GR2059 taken by #459).
- §6: the non-overlap rule is restated — see §7 below.

---

## 7. The GR2042 boundary

`18-integration-proof-proximity.md` §6 states: *"#382 never adds a lint that reads `writeScope`,
`action.maxTurns`, or `dependsOn`. Those three fields are GR2042's, exclusively."* GR2060 reads
`writeScope`, so the boundary must be settled rather than stepped around.

**Read literally, the rule is already false**, and that is the tell. **GR2041 `MissingWriteScope`
(#389)** reads `writeScope` and is not GR2042. The rule was never about which field a lint *touches* —
it is about which **verdict** a lint *derives*.

**Restatement (proposed delta to `18-integration-proof-proximity.md` §6):**

> GR2042 owns `writeScope` **cardinality and shape** as evidence about a **task's size** — the verdict
> *"this task is too big."* No other lint may derive a size verdict from those fields. A lint may read
> `writeScope` as a **coverage set** — the membership question *"does any task claim this path?"* — as
> GR2041 already does one level down. A coverage lint never comments on any task's size, never suggests
> splitting or narrowing a scope, and never reads `action.maxTurns` or `dependsOn`.

Concretely non-overlapping, and the two cannot fire on the same evidence:

| | GR2042 (#378) | GR2060 (this document) |
|---|---|---|
| input | `|writeScope|` of **one** task | `⋃ writeScope` over **all** tasks |
| unit | per task | per gate clause |
| verdict | "this task is too big" | "no task can produce this" |
| severity | WARN | ERROR |
| remedy | relocate proof / narrow scope | give some task the file, or drop the clause |
| reads `maxTurns`/`dependsOn` | yes | **no** |

**Honoring the boundary made the check better.** #474's proposal 3 asked for *"T appears nowhere in the
task's scope **or in any ancestor's output**"* — which needs `dependsOn` to compute the ancestor set, and
would have collided. Using the **union of all tasks' `writeScope`** instead is strictly more
conservative (it can only produce fewer findings) *and* avoids `dependsOn` entirely. The boundary
constraint and the false-positive constraint pointed the same way.

---

## 8. Devil's-advocate self-critique

**The strongest counter: "GR2060 does not catch #477, which is the expensive one. You have built a lint
for the cheap half and dressed it as a unification."**

Half-conceded, and the concession is in §2.3 in the plainest language available. The response has three
parts. (a) GR2060 *would* have fired on the `tierSource` clause of **that same terminal gate script**,
which was an equally-terminal failure — the cheapness is chronological accident, not a property of the
class. (b) #477's headline has no lint that is not a wolf; the only textual signal is the gate's own
English prose about which wave owes what, and doc 18 §3.1 already settled that making a comment
load-bearing for a verdict is worse than no check. (c) GR2062 does not attack #477's *symptom* (the
unsatisfiable clause) but its *cause* (the lost wave), which is the better target: had the stub existed,
the run would have honest-halted at the wave-3 barrier before spending a dollar of the $115.32.

**"`intendedWaves` is a number the author can lower, and the author is the one who lost the wave."**
Conceded in kind but not in force, and the answer is *temporal*. Doc 18 declined GR2059 partly because
*"the declaring agent is the agent the declaration grades."* That objection does not transfer here:
`intendedWaves` is written at **plan-folder creation** (wave-1 authoring), and it grades a **later,
separate** JIT-breakdown invocation — the one that truncated in #385. The declaration survives the event
it guards. And lowering it is a one-line diff in a reviewed config file, not a silent absence. That is
the whole ask: today the count is **nowhere**.

**"Two new codes and a schema field for two incidents is scope creep."** Which is why Milestone B is
separately approvable and Milestone A does not depend on it. If the reviewer takes A only, the design
still stands and #477 keeps the review-pass answer.

**"`git ls-files` adds a spawn to a command people run constantly."** One spawn per validate run, not
per file — GR2056's discipline verbatim. Injected, so unit tests spawn nothing. Silent when unavailable.
If the measured cost is material on a large repo, the honest fallback is to drop condition 6 and accept a
narrower check (paths under a task-declared directory only), not to cache.

**"Condition 6's git-tracked requirement means a gate requiring content in a brand-new file is never
checked."** Correct, and intended. A brand-new file is either in some task's `writeScope` (condition 8
excludes it) or it is the *existence* variant of this defect — which was considered and **dropped**,
because separating "nobody will create this" from "the build creates it" requires guessing at
`bin`/`obj`/`artifacts`/TRX/ignore-rule conventions, and that guess is the wolf §4.7 exists to avoid.

**"Suppressing GR2060 whenever a JIT wave exists means fixing #477 disables the #474 check."** True, and
it is the correct behaviour, not a flaw: with an un-authored wave present, "nothing can write this" is
genuinely unprovable. The suppression is the honest reading of the evidence, and §3.3 makes the two
checks complementary across exactly that boundary rather than leaving a hole.

---

## 9. Decisions

| # | decision |
|---|---|
| D1 | **One family, and the relation is dependency, not symmetry**: producer coverage is decidable only over a closed declaration set; #477 is that precondition gone missing. |
| D2 | #474's headline (reachability) is **not decidable** and gets no lint, ever. |
| D3 | #477's headline (which wave owes a behaviour) is **not decidable** and gets no lint, ever — keying on gate prose is refused on doc-18 §3.1 grounds. |
| D4 | GR2060 is an **ERROR**, blocking `validate`, in a **new §4.8** — not a fourth row of §4.7. §4.7 is closed-world over one script's text; this is relational. |
| D5 | GR2062 is a **WARNING**, gated on `planIsClosed`, skipped when `intendedWaves` is absent. |
| D6 | `intendedWaves` (`int?`) in `guardrails.json` is the minimum machine-readable intent, written at plan-folder creation. |
| D7 | GR2060 uses the **union of all `writeScope`s**, never `dependsOn` — more conservative and boundary-clean. |
| D8 | GR2060 is **git-tracked-files only**, PowerShell only, one variable hop, single-clause requirements with a de-regexable witness. |
| D9 | GR2042's remit is restated as **size verdicts from `writeScope`**, not `writeScope` reads. Delta stated for doc 18 §6; not applied here. |
| D10 | Codes: **GR2060** (this), **GR2061 RESERVED** for #382's deferred ledger lint (moved from GR2059, taken by #459), **GR2062** (this), next-free **GR2063**. |

---

## 10. Implementation handoff

Nothing starts until the #106 draft-PR review of this document is addressed.

**Milestone A — `guardrails-harness-developer`**, in order:

1. **Refactor, no behaviour change.** Extract GR2057's de-regex witness extractor and requirement-polarity
   reader from `ValidateGuardrailRequiresForbiddenToken` into shared private helpers.
   *filesTouched:* `src/Guardrails.Core/Loading/PlanValidator.cs`. Gate: GR2057's existing tests green,
   untouched.
2. **The probe.** `IGitTrackedFileProbe` + `GitLsFilesProbe` + `NullGitTrackedFileProbe`, mirroring
   `IScriptSyntaxProbe` including its "silence is not proof" contract; wire through the validator's
   construction the same way the syntax probe is.
   *filesTouched:* `src/Guardrails.Core/Loading/IGitTrackedFileProbe.cs` (new),
   `src/Guardrails.Core/Loading/GitLsFilesProbe.cs` (new), the validator's call site, the CLI composition
   root.
3. **GR2060.** `DiagnosticCodes.cs` (+ advance the next-free comment to GR2063), the new check in
   `PlanValidator.cs`, the `planIsClosed` helper, and the **SSOT §4.8 + §14.10 code-paragraph edits in the
   same commit** (invariant 4).
   *filesTouched:* `src/Guardrails.Core/Loading/DiagnosticCodes.cs`,
   `src/Guardrails.Core/Loading/PlanValidator.cs`, `docs/plans/02-schemas-and-contracts.md`.

**Milestone A — `guardrails-test-author`**, after step 3:

4. Unit tests per condition, each proving **silence** as well as firing. Then the two evidence gates,
   both mandatory before merge, both reported as numbers in the PR:
   - **the false-positive sweep** — GR2060 run over every committed `.ps1` under `docs/plans/` and
     `examples/`; the bar is **zero findings**, matching GR2057's landing evidence;
   - **the recovered artifact** — the byte-exact pre-remediation
     `docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1` recovered from
     git, against a tree with a `tierSource`-free SSOT and the wave's task set as it then stood, asserting
     GR2060 fires **exactly once** and names `tierSource` and the SSOT path.
   *filesTouched:* `tests/Guardrails.Core.Tests/**` only.

**Milestone A — `guardrails-skill-author`**, parallel with 3–4:

5. `guardrails-review`: extend the §4 missing-insertion check to plan- and wave-level gate folders; add
   the reachability probe and its question.
   `plan-breakdown`: the sibling-datum rule.
   *filesTouched:* `.claude/skills/guardrails-review/**`, `.claude/skills/plan-breakdown/**`. ~~Bump both
   `SKILL.md` frontmatter versions (#152/#156/#169).~~

> **CORRECTED 2026-08-20, during Milestone A.** There is no frontmatter version to bump. **#169** moved
> skill-version stamping to **INSTALL time** — the #156 build-time stamp targeted `$(OutDir)` while
> `PackAsTool` packs the *publish* output, so every published nupkg shipped unstamped skills. Neither
> tracked `SKILL.md` carries a `version:` field, and adding one by hand would diverge from
> `SkillsInstaller`, which is the thing that writes it.
>
> **Also derived during Milestone A, and NOT in §8: the producer set is per FOLDER, not per plan.**
> Extending the missing-insertion check to gate folders needed an answer to "produced by whom?", and
> "every task in the plan" is wrong for four of the six:
>
> | folder | when it runs | producer set |
> |---|---|---|
> | `tasks/<T>/preflights/` | before T's action | T's ancestors — **not T** |
> | `tasks/<T>/guardrails/` | after T's action | T's ancestors **+ T** |
> | `<plan>/<wave>/preflights/` | before that wave's tasks | **earlier waves only** |
> | `<plan>/<wave>/guardrails/` | after that wave's tasks | that wave + earlier waves |
> | `<plan>/preflights/` | ONCE before the DAG, on starting bytes | **nobody — the EMPTY SET** |
> | `<plan>/guardrails/` | run end, merged HEAD | every task, all waves |
>
> The empty-set row is the sharp one: a `<plan>/preflights/` clause requiring anything **the plan itself
> will build** is red at t=0 and halts before scheduling, so its remedy is a **PHASE move, not a wider
> scope** — a distinction the flat "does any task claim this path?" question cannot express.
>
> Closure interacts with this and is carved out: an un-authored wave stub makes the plan OPEN, so a gate
> verdict is **WITHHELD** and recorded as an inherited obligation on that wave rather than raised as a
> BLOCKER — but closure does **not** reach the two `preflights/` rows, because a later producer cannot
> help a check that has already run.

**Milestone B — `guardrails-harness-developer` then `guardrails-test-author`**, only after A merges:

6. `intendedWaves` through `RunConfig`/`PlanJson`/`PlanLoader`; GR2062; the `validate`/`plan` reporting
   line; SSOT §2 + §14.1 edits in the same commit. Then `plan-breakdown` emits the field.
   *filesTouched:* `src/Guardrails.Core/Model/RunConfig.cs`, `src/Guardrails.Core/Loading/PlanJson.cs`,
   `src/Guardrails.Core/Loading/PlanLoader.cs`, `src/Guardrails.Core/Loading/PlanValidator.cs`,
   `src/Guardrails.Core/Loading/DiagnosticCodes.cs`, the `plan`/`validate` CLI output,
   `docs/plans/02-schemas-and-contracts.md`, `.claude/skills/plan-breakdown/**`.

> **LANDED 2026-08-20 — Milestone B, harness half.** `intendedWaves` (`int?`) rides
> `RawRunConfig` → `PlanLoader` → `RunConfig` (nullable end to end, so "not recorded" stays distinguishable
> from any count); `PlanValidator.PlanIsClosed` is the §3.3 predicate, written once and documented as
> GR2060's suppressor too; `PlanValidator.ValidateIntendedWaves` emits GR2062; and
> `Core.Model.WaveIntentSummary.Describe` renders the §3.2 line for BOTH `validate` and `plan` from one
> implementation — two spellings of the same answer would reintroduce, in miniature, the disagreement the
> field exists to make impossible.
>
> Three notes for whoever picks up the rest:
> - **There is no `PlanJson.cs`.** The raw deserialization target is `Loading/RawManifests.cs`
>   (`RawRunConfig`); the handoff above names a file that does not exist.
> - **A FLAT plan carrying the key does warn.** `planIsClosed` is trivially true with no waves, so
>   `intendedWaves: 3` against zero wave folders satisfies both conjuncts. It fires with flat-specific
>   wording rather than an arithmetic "declares 0", and it can only fire where an author explicitly wrote a
>   waved-plans-only key into a plan that has no waves — which is worth saying, not swallowing.
> - **No non-positive-value check, deliberately.** `intendedWaves: 0` on a waved plan already lands as the
>   other-polarity GR2062 ("the plan grew past its stated intent"), which is honest and actionable; a
>   GR2012-style error would spend a code to say something the existing warning already says.
>
> **NOT done here:** `plan-breakdown` emitting the field at plan-folder creation (a skill change), and
> GR2060 itself, which remains reserved-by-name and unbuilt.

**Sequencing constraint.** Steps 1–3 all edit `PlanValidator.cs`; they are one agent, in order, not
parallel. Step 5 touches no C#. Milestone B must not start before A merges — both touch
`DiagnosticCodes.cs` and the SSOT.

---

## 11. Proposed plan-document edits

1. **This document** — `docs/plans/19-producer-coverage.md`, new. Delivered as a **draft PR** per #106
   before Milestone A starts.
2. **`docs/plans/README.md`** — add the 19 entry.
3. **`docs/plans/18-integration-proof-proximity.md`** — two edits, **stated here and applied by the
   maintainer**, not by this pass: §3.4's reserved code `GR2059 → GR2061`; §6's non-overlap rule restated
   per §7 above.
4. **`docs/plans/03-roadmap.md`** — no change. Neither milestone is a v2 bet; both are v1 authoring-time
   validation.
5. **`.claude/skills/guardrails-domain-knowledge`** — after Milestone A merges, one line in the
   validation summary naming GR2060 and the producer-coverage invariant.
