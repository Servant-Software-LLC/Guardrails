# 33 — Unproducible requirements: producer coverage built, and the carrier gap that stays procedural (#474)

**Issue:** #474 — *A guardrail can demand an outcome the task's `writeScope` cannot reach: the datum's
path runs through a file the task may not write.* This document is the **mechanical half**. The prose
half shipped separately as #578 (`de4e17c`); §7 states how the two divide the work, and where neither of
them reaches.

**Status:** design of record, **revised after an independent adversarial pass** (2026-09-02). Delivered
as a draft PR for inline review (#106) before any implementation milestone starts.

**What the review changed, in one paragraph, because it changed the shape and not the details.** The
first draft shipped two codes: **GR2060**, doc 19's designed-and-unbuilt producer-coverage check, and
**GR2070**, a new derivation for the carrier shape measured on plan 30. The pass traced GR2070's
motivating clause through git and found that its two halves **never coexist at any commit** — at the
moment the scope was broken the clause carried no named argument, and by the time it did the scope had
been fixed for 36 minutes (§3.4). **GR2070 is declined and held by name** (§4.1, §6.3, §12.3). The same
pass found that GR2060 at ERROR would **revert a JIT partial prefix**, re-opening #501 one code over
(§5.3), and that the sweep proving GR2060 safe had walked 533 of 850 scripts — skipping the only plan
that fires (§5.4). It also **reproduced GR2060's positive control blind**. So Milestone A comes out of
review stronger and with a mitigation attached; the new lint comes out of it deleted.

**Why this is its own plan, and not a paragraph appended to `19-producer-coverage.md`.** Doc 19 is the
design of record for this family and it is *half shipped*: its skill milestone landed at `e78b9d`, its
`intendedWaves`/GR2062 milestone landed, and **GR2060 — the harness half, the actual mechanical answer to
#474 — was never built.** It has sat reserved-by-name in `DiagnosticCodes.cs` for twelve days. Building
it turns out to require a change to `Scheduler`'s veto path that doc 19 never contemplated (§5.3), and to
leave a documented gap that doc 19's D2 predicted and this plan can now evidence (§6.4). Editing a
half-shipped design in place to record all that is how the reasoning behind a contract change gets lost.
This gets its own design, its own review and its own run; doc 19 gains a status pointer and one corrected
sentence (§12.4).

---

## 1. What this costs, measured

A guardrail can require an outcome that **no legal implementation can produce**. Every attempt fails, the
agent has no move that is both in scope and honest, and the task dead-ends at `needs-human` — or worse,
the agent finds an in-scope move that satisfies the clause and carries nothing.

The three measured instances, all from real runs:

| where | the requirement | why nothing could satisfy it | cost |
|---|---|---|---|
| `model-tiering-stage-2` task 13 | `AttemptJournaler.cs` must reference `Usage` | the journaler reads `ActionRun`, which had no `Usage`; the severed link was `ActionRunner.cs`, a **merged sibling's** file | `needs-human`, attempt 1 |
| `model-tiering-stage-2` **terminal gate** | the SSOT must mention `tierSource` | **no task in any wave** declared that path in its `writeScope` | 20 tasks, **$115.32**, then a red terminal gate with no retry and delivery withheld |
| `30-telemetry-phase-1` task 16 | `bucket: pending.Bucket` in the call to `RecordSettleWithAttempt` | `Scheduler.cs` **was** in scope; the *carrier* `ISchedulerJournal.cs` was in **no task's** scope, so a sixth argument was CS1501 | caught by a reviewer pre-run; would have cost a halt, or a cast |

The third is the one this plan exists for, and its false green is the expensive part. The in-scope move
that compiles is:

```csharp
((Journal.RunJournal)_journal).RecordSettleWithAttempt(…, pending.Bucket)
```

That satisfies the clause, passes the task's own filter (which drives the journaller and never the
Scheduler), and detonates as an `InvalidCastException` under fake journals at the **terminal gate, 26
tasks later**, attributed to whoever happens to be standing there.

**The obvious check returns the wrong answer.** *"Does the task own the file it must edit?"* — yes. That
is what makes this class hard to see, and it is why every existing gate passed: `validate`, `graph
--check`, and — on the first two instances — a full `/guardrails-review`.

---

## 2. The premise, re-verified (#393)

#393 requires a design citing an issue to re-check the issue's load-bearing claims rather than inherit
them. Done against `09f223f`.

| claim | verdict |
|---|---|
| GR2060 is designed and **unbuilt** | **Holds.** `DiagnosticCodes.cs:1036` lists it among "THREE codes RESERVED BY NAME … must not be re-used". No constant, no check, no test. Doc 19's own status table says `NOT BUILT`, dated 2026-08-20. |
| `PlanValidator.PlanIsClosed` exists | **Holds, and is better than doc 19 assumed** — `PlanValidator.cs:3395`, written for GR2062 and documented in place as GR2060's suppressor. Milestone A inherits it built. |
| GR2057's de-regex witness extractor exists | **Holds** — `TryLiteralWitness` (`PlanValidator.cs:2707`) and `MatchesWitness` (`:2802`), both `private static`, both used only by `ValidateGuardrailRequiresForbiddenToken`. The refactor doc 19 §10 step 1 asks for has not happened. |
| `IGitTrackedFileProbe` exists | **Does not.** `src/Guardrails.Core/Loading/` holds `IScriptSyntaxProbe` + `InterpreterScriptSyntaxProbe` and nothing else probe-shaped. |
| SSOT §4.8 exists | **Does not.** §4.7 runs to line 1520 and §5 begins at 1521. Doc 19 §6 specified §4.8 verbatim and it was never applied. |
| `RunCommand` refuses a plan carrying any validation error | **Holds** — `RunCommand.cs:198-207`, `probe.HasErrors` → `ExitCodes.HarnessError`, *"Validation failed; nothing was run."* This is the fact that decides GR2060's severity (§5.5) and that §5.3's veto rides on. |
| `ISchedulerJournal.RecordSettleWithAttempt` was 5-arity when plan 30 was authored | **Holds** — at `10816fb`: `taskId, attempt, status, mergeSequence, definitionHash`. `RunJournal`'s public overload was 6-arity, but its sixth is `definitionHashAtSettle`. **No `bucket` parameter existed anywhere under `src/`.** |
| plan 30's task 16 did not own the carrier | **Holds** — at `10816fb` its `writeScope` was `["…/AttemptJournaler.cs", "…/Scheduler.cs"]`. Fixed later at `62d7314`. |

**Three corrections the re-verification and the adversarial pass forced.** All are load-bearing, and the
first two reverse claims the first draft made in this section.

1. **Doc 19 §2's decidability table classifies #474 as shape (b), reachability, and rules it out
   permanently (D2: *"not decidable … gets no lint, ever"*).** The first draft argued that plan 30's
   instance is shape **(a), coverage**, and therefore lintable. The reading was right; **the conclusion
   was not**. The shape is decidable in principle and has **never occurred in a form a lint could see**
   (§3.4). **D2 stands, and is now better evidenced than when doc 19 wrote it** — an attempt was made and
   the evidence refused it. §6.4 carries the corrected table.
2. **The maintainer's proposed predicate, read as written, would have been silent on the very instance it
   was proposed for.** *"Is `M`'s declaring file in some task's `writeScope`?"* — `RecordSettleWithAttempt`
   has two declaring files, and **one of them was owned** (task 06 held `RunJournal.cs`). The existential
   reading returns *yes* and says nothing (§3.3). This correction survives the decline: it is the first of
   the three findings §6.3 preserves for whoever reaches for this shape next.
3. **`PlanIsClosed` is not the soundness precondition doc 19 §3.3 took it for.** It detects an **empty
   stub wave**; it returns `true` for an authored **partial prefix**, which is the case where the scope
   union is incomplete and GR2060 would fire wrongly — and, at ERROR, revert the prefix. §5.3 is the
   mechanism and §5.2 is why the first draft's reading of this predicate was exactly backwards.

---

## 3. What is decidable — the measurement, before the design

The maintainer asked for this check to be stressed before adoption rather than after. Everything below
was hand-run over **443 committed guardrail scripts** across the six plan folders 26, 27, 28, 30, 31 and
32 (plan 29 has no folder), against the tree at `09f223f`. Probe scripts are throwaway; every number is
reproducible from the folder plus the tree.

### 3.1 Is the clause even extractable? Mostly not — and the fractions are specific

| population | count | share |
|---|---|---|
| guardrail scripts in plans 26–32 | 443 | — |
| …carrying a literal **call anchor** (`'\bM\s*\('` and kin) | **80** | 18% |
| …whose anchor resolves to a real declaration in the tree | 67 of 140 (anchor, script) pairs | 48% |
| …carrying a literal **named-argument requirement** (`'p\s*:…'`, case-sensitive) | **1** | **0.2%** |
| …carrying **both** | **1** | **0.2%** |

The one script carrying both is
`docs/plans/30-telemetry-phase-1/tasks/16-carry-phase1-facts-through-the-worktree-settle/guardrails/03-both-settle-records-set-every-phase1-member.ps1`
— the measured instance, and nothing else in six plans.

**What is recognisable.** The member name survives as a plain literal:
`'\bRecordSettleWithAttempt\s*\('`. So does the parameter name, even though the clause interpolates:
`"bucket\s*:\s*$c\s*\.\s*Bucket"` has `bucket` as its literal head, and everything after the colon —
which is where the PowerShell variable lives — is irrelevant to the question being asked.

**What defeats it, named honestly.**

1. **Association travels through PowerShell dataflow.** The two halves are joined by
   `$argList = $member.Substring($call.Index, …)`. No static extractor follows that. Association must
   therefore be **by co-occurrence within one script**, which is only safe when the script names
   **exactly one** call anchor — a restriction §6.2 imposes and the corpus has never yet tested, because
   n=1.
2. **An arity requirement expressed positionally** — `M\([^)]*,[^)]*,[^)]*,[^)]*,[^)]*,[^)]*\)` — carries
   no parameter name and is invisible. This is the largest uncovered shape and there is no proposal for it.
3. **Multi-line here-strings.** A pattern written in `@'…'@` is not a single-line quoted literal and is
   not extracted. Silence, correctly.
4. **`.sh` guardrails** are out, on GR2057's shipped precedent (portable guardrails ship as `.ps1`+`.sh`
   pairs, so the pair is still seen).

Two extractor bugs bit during this measurement and are worth pinning into the implementation, because
both produce **silence** — the failure direction that looks fine (`silent-failure-is-the-recurring-defect`):

- **`\b` defeats a naive lookbehind.** `(?<![A-Za-z0-9_])M\(` never matches `\bM\s*\(`, because the
  character before `M` is the `b`. The literal must be normalised (strip `\b`, strip `\s*`) *before*
  matching. Undetected, this drops **every** anchor written in the idiomatic form — which is most of them.
- **Interface members carry no access modifier.** A declaration index anchored on
  `public|internal|private|…` finds `RunJournal.cs` and misses `ISchedulerJournal.cs` — *precisely* the
  carrier file this check exists to name. The index must be anchored on a **return type**, not a modifier.

### 3.2 The unqualified check is a wolf: 16 fires, 16 wrong

Before qualifying, I ran the check the maintainer's sentence describes at its widest — *any* call anchor
whose declaring files no task owns. It fires **16 times across the six plans, and all 16 are false
positives.** The whole list, because the shape of the noise is the argument:

- `Add`, `Parse`, `Equal`, `Count`, `Contains`, `Capture`, `WriteAllText` — BCL and xUnit names that
  collide with an unrelated repo declaration. `Assert.Equal(` resolves to `DefinitionDriftReporter.Equal`.
- `RunAsync` (66 declarations), `InvokeAsync` (27), `PlanLoader` (a constructor call in a test).
- `IsInScope` ×2, from `31-unattended-run-hardening/tasks/05-implement-handoff-coverage-check/guardrails/03-no-second-glob-matcher.ps1`.
  That guardrail asserts the new code **routes through** `WriteScope.IsInScope` and does **not** grow a
  second matcher. `WriteScope.cs` is correctly out of scope — the task must *call* the member, never
  change it. Firing there would be maximally wrong: it would tell an author to widen a scope in order to
  satisfy a guardrail whose entire purpose is that the scope stay narrow.

**A check that asks "is the declaring file owned?" without asking "does the requirement need the
declaration to change?" is noise on every reuse assertion in the corpus.** The qualifier is the check.

### 3.3 Existential vs universal — the correction that decides the whole design

`RecordSettleWithAttempt` is declared in **two** files. At `10816fb`, plan 30's scopes covered one of them.

| reading of "M's declaring file is in some task's `writeScope`" | verdict on the measured instance |
|---|---|
| **existential** — *some* declaring file is owned (`RunJournal.cs` was) | **silent** ✗ |
| **universal** — *every* declaring file must be owned (`ISchedulerJournal.cs` was not) | **fires** ✓ |

The universal reading is also the one that respects doc 19 §7's GR2042 boundary: it needs only the
**union** of every task's `writeScope`, never `dependsOn`. The ancestor-set variant that #474's own
proposal 3 asked for would compute the same verdict here and would collide with GR2042's remit; the
boundary constraint and the correctness constraint point the same way, exactly as doc 19 found.

**Universal quantification is the design.** It is stated here rather than buried in §6 because the
one-word difference is what separates a check that fires from a check that ships and never speaks.

### 3.4 The qualified check has a true-positive population of ZERO — the git trace

Adding the two qualifiers — a **named-argument** requirement, and **no declaration of `M` anywhere
declares that parameter** — over the same 443 scripts gives 2 candidate `(M, p)` pairs (`bucket`,
`definitionHash`, both from the one script), which the clause-form filter reduces to 1, and **0 findings
against today's tree**.

The first draft of this document read that zero as *"the folder has been fixed"* and asserted a positive
control: *"fires exactly 1 against the tree and folder as they stood at `10816fb`."* **That assertion is
false, and an adversarial pass caught it by opening the file at that commit instead of reasoning about
it.** The trace, verified in full:

| commit | time | the `Bucket` clause | scope holds `ISchedulerJournal.cs`? | GR2070 fires? |
|---|---|---|---|---|
| `10816fb` breakdown | — | `if ($argList -cnotmatch 'pending\s*\.\s*Bucket')` | **no** — the defect | **no** — no named-argument head |
| `62d7314` scope fix | 21:04 | `'pending\s*\.\s*Bucket'` (unchanged) | **yes** — repaired | no |
| `124a7d0` | 21:40 | `'bucket\s*:\s*pending\s*\.\s*Bucket'` — named argument appears, **single-quoted** | yes | no |
| `d87eea2` | 22:28 | `"bucket\s*:\s*$c\s*\.\s*Bucket"` — becomes double-quoted | yes | no |

**The two halves of the check never coexist.** At the only moment the scope is broken, the clause has no
named argument. By the time the named argument exists, the scope has been repaired for 36 minutes. There
is no commit in this repository's history at which GR2070 fires on a real defect.

Three things follow, and each one kills a piece of the first draft:

1. **§8.2's positive control cannot be recovered from git.** Its heading was *"recovered artifacts, not
   synthetic fixtures"*, and the artifact does not exist. Task 6's implementer would have had to
   hand-build it and label it recovered — in the plan whose thesis is #580.
2. **The double-quote relaxation had no cause.** §3.5's justification was that the guardrail is
   double-quoted *because* it needs a variable in the pattern. True, but the variable arrived at
   `d87eea2`, **48 minutes after** the named argument at `124a7d0`, where the clause was single-quoted and
   **readable by GR2057's shipped extractor unchanged**. The sibling clause reader, the head-only
   soundness rule, its negative control and its self-critique all rested on a cosmetic accident.
3. **The back-out trigger had already fired.** §15 risk 1 offered to withdraw the check if it had not
   fired on a real plan in six months. It had not fired on a real plan ever.

**This is the same defect the document is about, committed by the document.** A requirement was asserted
against a state of the tree that was never checked, it read plausibly, and every downstream artifact —
predicate, severity argument, test plan, risk register — was built on it. That §4.1 declines Milestone B
is the correct outcome; that it took an independent pass to get there is the finding worth keeping.

### 3.5 What the clause-form measurement is still good for

The measurement below stands — only the inference drawn from it in the first draft was wrong. Over the
443 scripts:

| clause form | count |
|---|---|
| `if ($v -match '…') { … }` — **single-quoted**, GR2057's shipped surface (`PlanValidator.cs:2515`) | **1,172** |
| `if ($v -match "…") { … }` — **double-quoted**, excluded by GR2057 by design | **6** |
| named-argument requirements among the 1,172 | **0** |
| named-argument requirements among the 6 | **1** — and it postdates its own defect by 36 minutes (§3.4) |

**5 of the 6 double-quoted clauses are in that one script**; the sixth is a `$member` interpolation in
plan 28. The form is not an emerging convention — it is what two authors reached for when they needed a
variable in a pattern, and in both cases the variable was introduced by a refactor unrelated to what the
clause asserts.

**The durable lesson, which outlives the declined check:** GR2057 restricts pattern operands to
single quotes because a `$` in a double-quoted regex is ambiguous between an anchor and an
interpolation. Any future lint tempted to relax that must first show a **defect** it catches, at a
**commit**, in the relaxed form — not merely a clause that happens to be written that way today.

### 3.6 Every attempt to widen it produced a wolf

Three widenings were tried, because a check with a population of one deserves the attempt.

**(a) `Type.Member` — require a member on a *named* type.** 193 candidates over the six plans, **3 fires,
3 wrong**: `SchedulerFactory.CreateExecutor` ×2 and `TaskDefinitionFiles.Enumerate`. Both members exist;
the extractor failed to see them. And that is not a bug to fix — enumerating a type's members textually
means handling properties, fields, positional record parameters, `partial` halves, inherited members and
extension methods. **100% of its fires were extractor error.** The line is sharp and worth stating: a
**parameter list is bounded by its own parentheses** and can be read; a **type's member set is not
bounded by anything** and cannot. Rejected.

**(b) "token `T` appears nowhere in the task's scope" — #474's own proposal 3.** Doc 19 §2.2 already
measured this and called it *"the loudest wolf this family could ship"*: it fires on every correct
test-authoring task, because authoring a RED test that names a not-yet-existing type is the archetype the
product is built on. Not re-litigated. Rejected, and the reason is recorded so the next design does not
re-propose it.

**(c) Dropping the named-argument qualifier.** That is §3.2. Rejected — and re-tested with the
declaration-count bound of §6.3 applied, since that bound is what kills `RunAsync` (66 declarations)
and `InvokeAsync` (27): **11 of the 16 false positives survive it**, because they are single-declaration
name collisions (`NoRoute`, `IsInScope` ×2, `Contains`, `Equal` ×2, `Enumerate`, `Count`,
`WriteAllText`, `Add` ×2). The qualifier is not replaceable by a cardinality bound.

### 3.7 What it costs to run

| mechanism | measured |
|---|---|
| indexing every declaration in `src/` + `tests/` (603 files, 9.2 MB) | **16.6 s** in the probe — unacceptable on a command people run constantly, even allowing that a .NET implementation is several times faster |
| one `git grep` for one member name | **61 ms** |
| enumerating the file set | 89 ms |

The design consequence is in §6.4: **nothing is indexed unless a candidate exists**, and a candidate
exists in 0.2% of scripts. On 99.8% of plans the cost is the extractor pass over text the validator
already reads.

---

## 4. Scope, ordered — and the order is decided

### 4.1 The verdict on the candidate check, plainly

**The candidate check is DECLINED. GR2070 is not built and is not allocated; it is held by name.**

The first draft of this document recommended building it as a rider on GR2060, and an independent
adversarial pass falsified the evidence that recommendation rested on. §3.4 has the git trace. The short
form: at the only moment plan 30's scope was actually broken, the motivating clause was
`'pending\s*\.\s*Bucket'` — **single-quoted, with no named argument in it at all**. The named argument
first appears at `124a7d0`, **36 minutes after the scope was repaired at `62d7314`**, and it was
single-quoted then too. So:

- **GR2070's true-positive population over this repository's entire history is zero.** There is no commit
  at which it fires on a real defect. Not one.
- **The double-quote relaxation had no justification.** The clause became double-quoted at `d87eea2`, 48
  minutes later still, when the receiver was refactored into `$c`. That is a cosmetic consequence of an
  unrelated edit, and the first draft built a sibling clause reader, a head-only soundness rule and a
  negative control on top of it.
- **§15 risk 1's own back-out trigger had already fired**, retroactively, before the check was specified.

A check whose motivating instance it cannot reproduce is not a narrow check; it is an unfalsified one.
Shipping it in the plan whose thesis is #580 — *a check is not authored, it is proven to fire* — would
have required task 6's implementer to hand-build a fixture and label it recovered. §14 records the
decline; §6 records what was learned, because the reasoning is worth keeping even though the code is not.

**What survives, and it is the larger half by every measure.**

- **A removes a review step.** GR2060 is the mechanical answer to #474 that doc 19 specified and nobody
  built, and it catches the **$115.32 terminal-gate instance** — the most expensive one on record. The
  adversarial pass reproduced its positive control **blind**, so its case is stronger after review than
  before.
- **C is now load-bearing rather than a rounding error.** With B declined it is the **only** thing
  covering the plan-30 shape — and §6 shows the shipped review probe does **not** cover it either, which
  the first draft got wrong in two places (§4.1 and §7 both credited the probe with a catch its written
  procedure does not produce).

**The ranking is A ≫ C, and B is gone.**

### 4.2 Two milestones, and the code that is held rather than spent

| # | milestone | ships | approvable alone? |
|---|---|---|---|
| **A** | **GR2060 — `UnproducibleGateRequirement`** (doc 19 §3.1, built) + the #501 veto mitigation (§5.4) | ERROR | **yes** |
| **C** | **the callee's parameter list** — the step the shipped review probe stops one short of (§6) | — | yes, and it is worth more with B declined |
| ~~B~~ | ~~GR2070~~ — **declined**, §4.1 and §6.3. Held by name in `DiagnosticCodes.cs`, not allocated | — | — |

**Milestone A does not depend on C, and C does not depend on A.** They can be approved and sequenced
independently; §13 runs them in one plan only because they close one issue and share a reviewer.

### 4.3 Placement

| piece | placement |
|---|---|
| GR2060 | **harness** — `Guardrails.Core/Loading`, author-time only |
| SSOT §4.8 + §14.10's code paragraph | **schema** — lands in the same commit as the code (invariant 4) |
| the **callee's-parameter-list** step (Milestone C) | **skill** — `guardrails-review` + `plan-breakdown`, ADDED beside the shipped datum trace (§6.2) |
| the sibling-datum trace, the missing-insertion extension | **already shipped** — `e78b9d`; Milestone C extends the probe, it does not rewrite these |
| a dataflow-reachability lint (#474's *first* headline) | **out of scope, permanently** — doc 19 D2 stands (§6.4) |
| **GR2070** — the named-argument derivation | **DECLINED**, held by name (§4.1, §6.3) |
| a lint over positional arity | **out of scope** (§14) |

---

## 5. Milestone A — GR2060, the designed centerpiece

Doc 19 §3.1 specified this completely and correctly. This section does not re-derive it; it records what
changed underneath in twelve days and what this plan adds.

### 5.1 The predicate, unchanged

> A script guardrail requires an exact literal in a **tracked workspace file** that does not contain it,
> and **no task in the plan declares that file in its `writeScope`**.

All ten of doc 19 §3.1's conditions are adopted verbatim: PowerShell only; a statically-known path
operand; a one-hop variable association; a requirement clause with a de-regexable witness and a
requirement polarity; the witness absent from current bytes; the file git-tracked; the path not under
the plan folder; coverage decided by `WriteScope.IsInScope` over the **union**; GR2041 clean;
`planIsClosed`.

### 5.2 What doc 19 assumed — and the one assumption that is a TRAP

- **`PlanJson.cs` does not hold the raw config.** Doc 19 §10 step 6 named a file that does not exist for
  that purpose; the raw deserialization target is `RawManifests.cs`. Milestone A touches neither, but the
  handoff table must not repeat the wrong name.
- **`PlanValidator` has a four-overload constructor chain** ending at
  `(IExecutableProbe, BannedPatternRegistry, IScriptSyntaxProbe)`. The tracked-file probe arrives as a
  **fifth overload with a real default**, exactly as the syntax probe did — see §13 task 2 for the
  **73 call sites** that default silently changes.
- **`PlanIsClosed` is built** (`PlanValidator.cs:3395`) — and the first draft called that *"better than
  doc 19 assumed."* **It is worse.** It is the trap in §5.3, and reading it as a soundness guarantee is
  how GR2060 at ERROR reproduces a defect the harness already fixed.

### 5.3 The #501 veto — GR2060 at ERROR can revert a JIT prefix, and must not

This is the finding that changes Milestone A's shape, and every line of it is in the tree today.

**The mechanism, in four facts.**

1. `PlanIsClosed(plan) => plan.Waves.All(w => w.Tasks.Count > 0)` (`PlanValidator.cs:3395`). It detects an
   **empty stub wave**. It returns **`true`** for a wave authored as a **partial prefix** — 5 task folders
   of an intended 12 — because 5 > 0. Doc 19 §3.3 leaned on this predicate as GR2060's soundness
   precondition; for the JIT-prefix case it is not one.
2. A partial prefix therefore has an **incomplete `writeScope` union**: the tasks that will own the
   remaining files do not exist yet. A wave gate requiring content one of them will produce looks, to
   GR2060, exactly like a gate nothing can produce.
3. `ValidatePlanAfterBreakdown` (`Scheduler.cs:2205`) runs `new PlanValidator().Validate(...)` on that
   prefix and computes `excused = wavePrefixIsIncomplete ? errors.Where(UnsatisfiableWhileIncomplete)`,
   then `blocking = errors.Except(excused)`.
4. `UnsatisfiableWhileIncomplete` (`Scheduler.cs:2325`) is a **single-code comparison** against
   `PlanGuardrailsMissingIntegrationReRun`. GR2060 is not in it.

**So an ERROR-severity GR2060 is not excused, casts a veto, and the prefix is reverted wholesale** — which
is verbatim the defect #501 fixed, described in that code's own comment: *"a wave cut off after 5 of 12
task folders had, by construction, no wave-root `guardrails/` exit gate yet … so GR2028 fired, `valid`
went false, and the prefix the manifest existed to preserve was reverted wholesale."* Shipping GR2060 at
ERROR without a mitigation re-opens it one code over, and the cost is paid in reverted JIT work — the
most expensive thing this harness can throw away.

**The mitigation: allow-list on `wavePrefixIsIncomplete`, NOT on `PlanIsClosed`.**

`UnsatisfiableWhileIncomplete` grows a second code. That is a one-line change and it is the right seam,
because `wavePrefixIsIncomplete` is **actual knowledge of incompleteness** — it is set from a usable
`breakdown-intent.json` that still owes folders — whereas `PlanIsClosed` merely observes that no wave
folder is empty. The two are not interchangeable, and the trap in §5.2 is exactly the belief that they
are.

Three properties the implementer must preserve, all already true of the #501 code:

- **Excused errors stay in the report.** They stop casting a veto they cannot fairly cast; they do not
  vanish. An operator reading the gate decision still sees the GR2060 finding.
- **The suppression is scoped to the JIT breakdown gate**, not to `validate`. A human running
  `guardrails validate` on a partial prefix still sees the error, which is correct: they are asking a
  different question.
- **`PlanIsClosed` stays as GR2060's condition-10 suppressor** for the *empty-stub* case it does detect.
  The two suppressions are complementary, not alternatives, and §12.1's SSOT text must say so — otherwise
  the next reader repeats the first draft's mistake.

**Task 5 authors the regression test before task 6 implements**: a plan with a JIT partial prefix whose
wave gate trips GR2060 must **not** be reverted, and the finding must still appear in the gate-decision
report. Red first, on the #501 shape.

### 5.4 The corpus sweep, run in advance — and the population it MISSED

> **The denominator in this section was itself wrong, and an adversarial pass caught it.** The first
> draft said *"1,271 committed scripts"*. That figure is an **on-disk** count: 1,271 `.ps1` exist under
> `docs/plans/` in a working tree, but **364 of them are gitignored generated `containment-hook.ps1`
> copies** and only 850 are committed. The distinction is not pedantry — §8.5 tells task 9 to evaluate
> each plan **at its own pre-run commit**, and you cannot `git show <commit>:<path>` a gitignored file.
> Stated as "committed", the old number gave the task a population unreachable by the method it was
> told to use. Every count below is now `git ls-files`, and task 9 enumerates from git, not the disk.

Doc 19 §10 step 4 makes a zero-finding sweep a merge gate. Hand-run during this design:

| | |
|---|---|
| `-notmatch` requirement clauses seen | 147 |
| …with a one-hop literal path operand | 41 |
| …with a de-regexable exact witness | 13 |
| …whose witness is **absent** from the named file | **0** |
| **GR2060 findings** | **0** |

**Two limits, and the second is a defect in the sweep rather than a caveat about it.**

**(a) Structural silence, on doc 19's own precedent about GR2062.** Today's tree is post-merge, so the
witnesses these plans required are present *because the plans ran*. The zero proves the check is silent on
**satisfied** requirements; it does not prove it is silent on a correct plan whose work is not yet done.

**(b) The sweep walked 533 of 850 scripts, and skipped the only plan that fires.** It enumerated plan
folders carrying a top-level `tasks/` directory. **Five plan folders are waved**, nesting their tasks
under `wave-NN-*/tasks/`, and were silently excluded:

| folder | scripts | in the sweep? |
|---|---|---|
| `autonomous-mode-impl` | 100 | **no** — waved |
| `model-tiering-stage-2` | **89** | **no** — waved, and it carries §8.2's positive control |
| `model-tiering-stage-3` | 78 | **no** — waved |
| `salvage-advice-provisioning` | 39 | **no** — waved |
| `09-preflight-first-class` | 11 | **no** — neither layout |
| the 14 walked folders | 533 | yes |
| **total under `docs/plans/`** | **850** | |

So the headline *"0 findings over 14 plan folders"* was computed over a population that structurally
**excluded the one plan known to fire**. The number is not wrong, but it measured less than it claimed —
the same species of error as §3.4, one level up. §8.5's sweep **must walk waved folders**, and its
expected counts are per plan and per commit rather than a blanket zero.

### 5.5 Severity: ERROR — conditional on §5.3, and on nothing else

`RunCommand.cs:198-207` refuses to run a plan carrying any validation error, so an ERROR is a
run-blocking gate on every plan forever, including on **resume**. GR2068/GR2069 ship as WARNING for
exactly that reason: a correct shipped plan can carry a stale handoff cell.

GR2060 stays at ERROR, per doc 19 D4:

- Its verdict is a **provable impossibility about the run about to start**, not a judgement about a
  document. The clause is red now, red at the end, and no agent action inside the plan can change it.
- The alternative is not "the run succeeds". It is "the run spends its whole DAG and fails at the gate" —
  measured at $115.32.
- Its false-positive surface is a **path**, which is unambiguous — unlike a member **name**, the surface
  that helped sink GR2070 (§6.3).
- It has a **recovered positive control verified condition-by-condition against its commit** (§8.2) —
  all ten, by a pass that did not author it. That is the bar this document holds everything to, and
  GR2060 is the only thing in it that clears it. The earlier phrasing rested on an independent *blind*
  reproduction; §8.2 withdraws that claim, and this bullet must not outlive it.

**And the one condition on that severity: §5.3 ships in the same milestone.** ERROR without the
`wavePrefixIsIncomplete` allow-list entry is not a stricter version of this design — it is a different
design, one that reverts JIT prefixes. If task 6 cannot land, GR2060 ships at WARNING instead and §16
gains a row.

---

## 6. Milestone C — the callee's parameter list, and the code that is held

### 6.1 The gap: the shipped probe stops one step short of the defect

The first draft described Milestone C as *"the sibling-datum authoring rule that doc 19 §4 specified and
never shipped."* **That is wrong on both halves, and the adversarial pass verified it.** The rule shipped
at `e78b9d` — *"feat(skills): #474/#477 Milestone A — point the existing probe at the gate, and trace
the datum"* — and it is live at `plan-breakdown/SKILL.md:381` under the heading *"Before you write a
`writeScope`, TRACE THE DATUM — follow the sibling that already works (#474)."* C as first written would
have re-authored existing text.

**The real gap is one step further on, and it is why plan 30's instance survived a review pass that ran
the probe correctly.** The shipped Unreachable-outcome probe (`guardrails-review/SKILL.md:948`) is four
steps, and its step 3 is a stopping condition:

> 1. Open X … name the expression it must read **from**. That is the **carrier**.
> 2. Resolve the carrier's declaring type and the file that declares it.
> 3. **Does the carrier already expose what Y needs, on that tree? If yes → reachable; stop.**
> 4. If not, the member must be ADDED to the carrier. Is the file declaring the carrier in scope? …

Run it on the plan-30 clause. The required text is `bucket: pending.Bucket`; the carrier is `pending`, a
`PendingAttempt`; `PendingAttempt.Bucket` **does** exist on that tree, added by an in-scope ancestor. Step
3 answers *yes*, the probe returns **reachable**, and step 4 never runs. The file that actually made the
task unsatisfiable — `ISchedulerJournal.cs`, which declares the **callee** — is never opened, because the
probe traces **upstream to the value's source** and the defect was **downstream in the receiver's
signature**.

So §7 and §4.1 of the first draft were both wrong to say *"the probe caught it."* A **reviewer** caught
it, by tracing further than the written procedure asks. That is precisely the discretion this plan exists
to remove, and with GR2070 declined, **C is the only thing that removes it.**

### 6.2 The step Milestone C adds

One new step, in both skills, phrased so it cannot be satisfied by the check that already passed:

> **5. If the required text is an ARGUMENT IN A CALL, the carrier is not the answer — the CALLEE is.**
> Name the member being called and open **its declaration**. Does its parameter list already accept what
> the clause requires? If not, the requirement is *"widen this signature,"* and the file declaring that
> member must be in this task's `writeScope` or an ancestor's. **Not the file the call is written in —
> the file the member is declared in.** For a call dispatched through an interface, that is the
> **interface**, not the concrete type: a cast to the concrete type compiles, satisfies the clause, and
> journals nothing.

And the authoring-side twin in `plan-breakdown`, beside the shipped datum trace:

> When a task's deliverable is *"pass D to M"*, `M`'s **declaring** file goes in the `writeScope` — the
> interface if the call dispatches through one — unless `M` already accepts D today. Grep the declaration,
> not the call site.

**Both are one paragraph, and both name the false green**, because in this class the false green is the
outcome that ships: the cast that compiles, passes the task's own filter, and detonates 26 tasks later
under a fake.

### 6.3 GR2070 — reserved by name, and the record of why

**GR2070 is held, not spent.** §12.3 adds it to `DiagnosticCodes.cs`'s reservation block with a one-line
reason and a pointer to this section, on the same footing as GR2061 and GR2054. The design is recorded
here so the next person to reach for this shape starts from the evidence rather than from the idea.

**What was specified, in one line.** *A guardrail requires a named argument `p:` in a call to member `M`;
no declaration of `M` accepts a parameter named `p`; at least one file declaring `M` is in no task's
`writeScope`.* Twelve conservatism conditions, universal quantification over declaration sites (§3.3), a
tracked-file probe reused from Milestone A, WARNING severity.

**Why it is declined, in one line.** It has **never fired on a real defect at any commit in this
repository** (§3.4), and the double-quote relaxation its extractor required rested on a cosmetic edit
made 48 minutes after the defect was already fixed.

**The three findings worth keeping, because they will recur:**

1. **Universal, not existential.** *"Is `M`'s declaring file in some task's `writeScope`?"* is silent when
   any one declaring file is owned — and in the motivating case `RunJournal.cs` was owned while
   `ISchedulerJournal.cs` was not. Any future version must quantify over **every** declaration site. This
   also keeps it inside doc 19 §7's GR2042 boundary, since it needs only the union of scopes and never
   `dependsOn`.
2. **The qualifier is the check.** Asking *"is the declaring file owned?"* without asking *"does the
   requirement need the declaration to change?"* fires 16 times on six correct plans and is wrong all 16
   (§3.2) — including twice on a guardrail whose entire purpose is that a scope stay **narrow**.
3. **A parameter list is readable; a type's member set is not.** The `Type.Member` widening produced 3
   fires and 3 extractor errors (§3.5). A parameter list is bounded by its own parentheses; a type's
   members are spread across properties, fields, positional records, partials, base types and extension
   methods.

**What would justify revisiting it.** Not a clause that happens to be written in the right shape — a
**defect**, at a **commit**, where the named-argument requirement and the unowned declaring file coexist.
One such instance turns GR2070 from an unfalsified idea into a check with a positive control, and that is
the bar §8.2 holds everything else to.

### 6.4 Why doc 19 D2 stands, and what that leaves uncovered

D2 — *"#474's headline (reachability) is not decidable and gets no lint, ever"* — stands, unamended, and
the decline of GR2070 strengthens rather than tests it.

| | doc 19's instance (shape **b**) | plan 30's instance |
|---|---|---|
| the file that must change | `ActionRunner.cs` — **owned**, by a merged sibling task | `ISchedulerJournal.cs` — **owned by nobody** |
| what is missing | a *value* on a type: `ActionRun` has no `Usage` | a *parameter* on a member declaration |
| to decide it you must | resolve a parameter's type, walk to its declaration, enumerate its members, infer which the required expression sources from | read one parameter list — **but only if you know to look at the callee at all** |
| covered by | the review probe, permanently | **Milestone C, procedurally. No lint.** |

The first draft used this table to argue that plan 30's instance is a decidable coverage shape and
therefore lintable. The reading was right and the conclusion did not survive contact with git: the shape
is decidable **in principle** and has **never occurred in a form a lint could have seen**. Decidability is
not the bar; a positive control is.

---
## 7. Relationship to #578, and to the review probe — complements, and a gap neither of them closes

#578 shipped yesterday (`de4e17c`): a `/guardrails-review` probe that **executes** the structural claims
a prompt makes about the code. It explicitly declined a mechanical `validate` check, on the grounds that
prose claims have nothing statically decidable to gate on. That reasoning is right and this plan does not
disturb it: GR2060 gates on **guardrail scripts** — machine-readable by construction — never on prompt
prose.

| | #578 probe (shipped) | #474 reachability probe (shipped, `e78b9d`) | GR2060 (this plan) |
|---|---|---|---|
| subject | a prompt's claim about the tree | a guardrail's datum and its **carrier** | a guardrail's requirement and the union of scopes |
| the defect it names | the **map is false** | the **value has no route** | the **requirement has no producer** |
| when | review, once | review, once | every `validate`, every run start, every **resume** |
| who | a non-authoring agent, 15–20 min | a non-authoring agent | nobody |
| its failure mode | **nobody ran it** | **nobody ran it**, *and* it stops at step 3 (§6.1) | **the shape was not extractable** |

**What the probes catch that no diagnostic can:** any claim expressible only in prose (*"A funnels
through B"*, *"there are nine sites"*), the semantic reachability shape (§6.4), a claim about a file the
plan will **create**, and the case where the guardrail is strong and correct while the *instructions*
around it are wrong — which #570's table says is six of seven recent defects.

**What GR2060 catches that no probe can:** the case where nobody thought to look. It fires on the plan
edited after review and on the resume six days later, and it costs nobody fifteen minutes.

**And the correction the first draft needs most, because it inverted the credit.** This section
originally read: *"the plan-30 instance was caught by the reachability probe — by a reviewer who chose to
trace."* **The probe did not catch it, and could not have.** §6.1 walks the shipped four-step procedure
against that clause: the carrier is `pending`, `PendingAttempt.Bucket` exists on that tree, **step 3
returns "reachable; stop"**, and the callee's declaration is never opened. A reviewer caught it by going
past the written procedure.

So the honest map of this shape is:

| covers the plan-30 carrier shape | status |
|---|---|
| GR2070 | **declined** — never fires on a real defect at any commit (§3.4) |
| the shipped `#474` reachability probe | **does not reach it** — stops at step 3 (§6.1) |
| the shipped `#578` structural-claim probe | **out of subject** — the prompt's claim was true; the guardrail was unsatisfiable |
| **Milestone C's step 5** (§6.2) | **the only coverage this plan delivers, and it is procedural** |

That is a weaker answer than the first draft claimed, and it is the true one. It is also why C's value
went **up** when B was declined: C is no longer a tidy-up beside a lint, it is the entire mitigation.

---

## 8. How this is tested — a check is not authored, it is proven to fire (#580)

Six verifications came back green while doing nothing in one session. Every pin below therefore carries
its own evidence that it bites.

### 8.1 Per-condition unit tests, both polarities

Each of GR2060's ten conditions gets a test that it **fires** and a test that it is
**silent** when that condition alone is flipped. A condition with only a firing test has not been shown
to be load-bearing; a condition with only a silence test has not been shown to exist.

### 8.2 The positive control — one, recovered, and verified condition-by-condition

**There is exactly one, and it is GR2060's.** The first draft claimed two; the second could not be
recovered from git and its check is declined (§3.4). One real control is what this plan has, and the
document says so rather than padding the count.

- **GR2060:** `docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1` at
  `544f7d5`, against a tree whose SSOT is `tierSource`-free and that wave's task set as it then stood.
  Verified: **0 of that plan's task manifests name the SSOT in `writeScope`**. Assert: fires **exactly
  once**, naming `tierSource` and the SSOT path. Assert also that the **same script against `09f223f` is
  silent**, so the test proves the check tracks the tree rather than the string.

  **The commit moved, and WHY it moved is the most instructive thing in this document.** The control was
  first pinned at `1b8e681`. GR2060 **cannot fire there**, and the reason is its own condition 10: at
  that commit `model-tiering-stage-2`'s `wave-02-attempt-launch-wiring` holds only `brief.md` and two
  diagrams — **zero task manifests** — so `PlanIsClosed` is FALSE and the check is suppressed. The two
  pinned tests were mutually unsatisfiable, and any implementation that made test 1 pass would have done
  it by deleting condition 10 (§11 prohibition 4).

  **And the suppression is CORRECT**, which is what makes this worth recording rather than merely fixing.
  Doc 19 §3.3's stated reason for condition 10 is *"a future wave may own the file"* — and at `1b8e681`
  that is literally what then happened: wave 2 was later authored and gained
  `14-land-ssot-schema-deltas`, whose `writeScope` is exactly `["docs/plans/02-schemas-and-contracts.md"]`.
  GR2060 was right to stay quiet. `544f7d5` is the same artifact, same witness, same path, at a commit
  where the plan is CLOSED (19 manifests across both waves) and no manifest names the SSOT.

  **The earlier claim of independent reproduction has been withdrawn.** An adversarial pass derived this
  control blind and reached the same artifact, commit and count, and that agreement was reported as the
  strongest evidence in the document. Both derivations omitted condition 10 — the same blind spot, twice,
  which is precisely why the agreement felt conclusive. **Two independent reproductions are not proof
  when they share an omission**, and only building the tests against real bytes exposed it. That is the
  same lesson as §3.4 one level up: there, a control could not be recovered; here, a control was
  recovered from a commit at which the check is silent by design.

### 8.3 Negative controls — including one that must be CONSTRUCTED, and why that is honest here

Two ways GR2060 can ship mute were found while designing it. Each gets a test that fails if it regresses,
and each must be shown **red against the pre-implementation tree** and green after — a green-on-both test
proves nothing.

- **The one-hop association.** A fixture written
  `$v = if (Test-Path 'X') { Get-Content -Raw 'X' } else { "" }` — the measured instance's own shape —
  must be extracted. A reader that only handles `$v = Get-Content 'X'` misses it and the check ships mute
  on the artifact it was built from.
- **The double-quoted path operand.** Doc 19 §3.1 condition 2 relaxes *paths* (not patterns) to
  double-quoted literals containing no `$` and no backtick, because the measured instance needs it. A
  fixture in that form must be extracted.

**Condition 8's control — and the claim here was FALSIFIED by the run that implemented it.** This
section first asserted that condition 8 — *"no task declares the path"* — had **zero exercises in the
corpus**, and concluded that its silence control therefore had to be **constructed**. That was wrong, and
the same git trace that moved the positive control found the exercise:

> At **`5bd29da`** the witness is still absent (`docs/plans/02-schemas-and-contracts.md` carries 0
> occurrences of `tierSource`) — but `14-land-ssot-schema-deltas` now declares
> `["docs/plans/02-schemas-and-contracts.md"]` in its `writeScope`, and the plan is closed at 20
> manifests. **The path IS covered and the witness IS absent: that is condition 8, exercised, on a real
> artifact.**

So `544f7d5` → `5bd29da` is a **recovered fires/silent pair on one artifact**: same script, same witness,
same path, with the *only* difference being whether a task owns the file. That is strictly better
evidence than anything synthetic could be, and it is exactly the discrimination the check exists to make.

**Condition 8's control is therefore RECOVERED, not constructed**, and the constructed fixture is
withdrawn. The reasoning that justified constructing it was sound — a silence control needs a state the
corpus may not contain, and manufacturing it is legitimate where a *firing* control's manufacture is not
— but its premise was false, and a recovered control beats a legitimate synthetic one every time. The
general rule survives unchanged for a condition whose exercise genuinely does not exist; it simply does
not apply here.

**What this cost, stated plainly:** the corpus was asserted to lack an exercise without the trace being
run. §3.4 killed Milestone B for an unverified claim about what git contained; this section made an
unverified claim about what git contained and got a weaker test out of it. Same error, opposite
direction, same remedy — look.

**Why constructed is legitimate here and was not legitimate for the declined check.** A *silence* control
asserts that a condition **suppresses** a finding; it needs a state the corpus does not contain, and
manufacturing that state is the only way to exercise the suppression at all. A *positive* control asserts
the check **fires on a real defect** — and manufacturing that is precisely the thing #580 forbids, because
a hand-built firing fixture proves the code matches the fixture and nothing about the world. The
distinction is the direction of the claim, and the test names must carry it.

### 8.4 The anti-tautology pin

`ProducerCoverage` must never be tested only through fixtures it also generates. At least one test drives
the real `PlanValidator.Validate` over a real on-disk plan folder, through the same composition root
`PlanProbe.cs` uses — the #382 lesson, which is that fake-masked unit guardrails certify green while the
composition-root path is broken.

### 8.5 The corpus sweep as a terminal gate — with an expectation that is NOT a blanket zero

The first draft wrote *"expected findings: 0 for GR2060 on every plan"* two sections after §8.2 asserted
that GR2060's positive control **fires once** on `model-tiering-stage-2` at `544f7d5`. **Those two
statements contradict each other**, and a correct implementation would have gone red at the terminal
gate — where §11 forbids every cheap escape, so the run would have halted with delivery withheld and no
legal move. The adversarial pass caught it; it is written out here in the form the implementer needs.

**Population.** Every `.ps1` under `docs/plans/` — **850 files**, waved folders **included** (§5.4(b));
plus `examples/`. A sweep that enumerates only folders with a top-level `tasks/` sees 533 of them and
misses the plan that fires.

**Expectation, per plan and per commit.** Not one number:

| subject | commit | expected GR2060 findings |
|---|---|---|
| `model-tiering-stage-2` — `guardrails/03-dor-section-6-contract-landed.ps1` | `544f7d5` | **exactly 1**, naming `tierSource` and the SSOT path |
| the same script | `5bd29da` | **0** — the witness is STILL absent, but `14-land-ssot-schema-deltas` now owns the path. The recovered condition-8 silence row; paired with the row above it is the only place the sweep can fail in BOTH directions |
| `model-tiering-stage-2` — the same script | `09f223f` (today) | **0** — the requirement is satisfied now |
| every other plan folder | its own pre-run commit, where one exists | **0** |
| every plan folder | `09f223f` | **0** |

**The two ways this gate must be able to fail**, and they are different failures:

- **a finding where the table says 0** → the extractor learned to fire on a correct plan. Back it out;
  this is doc 19 §5's falsification trigger, not a fixture to adjust.
- **no finding where the table says 1** → the check is mute. This is the failure §3.4 and §5.4 were both
  instances of, and it is the one that looks like success.

Encoded as a data-driven test whose expectations are a **table in the test file**, not a single assertion
— so adding a plan folder adds a row, and a row that disagrees with reality fails loudly rather than
being averaged away. It runs at the **terminal gate**, so either failure withholds delivery.

---

## 9. Done when

1. `guardrails validate` emits **GR2060 (ERROR)** on a plan whose gate requires an absent literal in a
   tracked file no task declares.
2. **The one positive control (§8.2) fires exactly once**, on
   `model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1` at `544f7d5`, naming
   `tierSource` and the SSOT path — and was **shown red** before the implementation landed. The same
   script against `09f223f` is silent.
3. The §8.3 negative controls pass, and the condition-8 silence control is the **RECOVERED** pair —
   named `Recovered…`, reading `5bd29da` from git, and paired with `544f7d5` (§8.3).
4. The §8.5 sweep walks **all 850** `.ps1` under `docs/plans/`, waved folders included, and is a
   terminal gate. Its expected counts are **per plan and per commit** (§8.5) — not a blanket zero.
5. **The #501 regression test (§5.4) is red before task 6 and green after**: a JIT partial prefix whose
   wave gate trips GR2060 is **not** reverted, and the finding still appears in the report.
6. `docs/plans/02-schemas-and-contracts.md` carries **§4.8**; §14.10's code paragraph records GR2060 as
   shipped, holds **GR2070 by name**, and advances next-free to **GR2071** — in the same commits as the
   code (invariant 4).
7. `DiagnosticCodes.cs`'s reservation block lists **GR2070** with a one-line reason and a pointer here
   (§12.3), and GR2060 has been **removed** from that block because it is now allocated.
8. `guardrails-review` and `plan-breakdown` carry the **callee's-parameter-list** step (§6);
   `guardrails-domain-knowledge` names GR2060 and the producer-coverage invariant.
9. `docs/plans/19-producer-coverage.md` carries a status line pointing here (§12.4).
10. Neither `PlanValidator` composition root changed signature, and the **73** `new PlanValidator(` call
    sites (§13 task 2) still compile with their existing arguments.
11. Core + Integration suites green; no existing GR2057 test edited.

**#474 does not close on this plan, and less of it closes than the first draft claimed.** Milestone A
closes the **named-path** coverage shape at both altitudes. The **plan-30 carrier shape closes
mechanically nowhere** — GR2070 is declined (§4.1) and §6 shows the shipped review probe stops one step
short of it. Milestone C covers it **procedurally**, which is weaker than a lint and is the honest state
of the art for this shape. The issue gets a comment saying exactly that and stays open.

---

## 10. Invariants in play

**1 — deterministic guardrails over prompt-judges; judges never alone.** GR2060 is the deterministic
half of a class whose larger half is permanently a review probe. The discipline this document had to
learn is that it **says which half is which** and does not inflate the lint to look productive: §3.6
records three widenings tried and rejected with numbers, and §4.1 records a fourth — a whole lint,
declined for want of a single instance where it fires. **The invariant cuts both ways**, and the first
draft only read it one way: preferring a deterministic gate does not license shipping one that has never
gated anything.

**4 — the SSOT is the schema SSOT; a contract change lands in the SAME change.** One new section and one
code-paragraph edit, specified verbatim in §12, landing in the implementing commits. §5.2 records that
doc 19 specified §4.8 and never applied it — the exact failure this invariant exists to prevent, and the
reason §12's edits are pinned to specific tasks in §13 rather than left as an intention.

**5 — honest halts; nothing is marked done unverified.** All three measured instances **were** honest
halts or withheld deliveries. The system worked; it worked at the most expensive point available. This
design moves the verdict earlier and adds **no escape hatch and no waiver field** — there is no way to
suppress GR2060 from inside a plan, by design. The one suppression that exists (§5.3's JIT-prefix excuse)
lives in the **harness**, is keyed on the harness's own knowledge that folders are still owed, and leaves
the finding **visible in the report** — it withholds a veto, not a verdict.

**2 — the harness is the single writer of merged state.** GR2060 skips every path under the plan
folder. `state/`, `logs/`, the journal and `diagram.md` are harness-written and appear in no `writeScope`
by construction; a coverage check that did not skip them would fire on every plan that reads its own state.

**6 — plain files, light setup.** One `git ls-files` per validate run, injected, silent when unavailable.
No index, no daemon, no cache, no new dependency. §3.7 measured the alternative that GR2070 would have
forced — a full declaration index over `src/` + `tests/`, **16.6 s** — and declining that check removed
the only part of this design that ever threatened this invariant.

---

## 11. Running this plan unattended

`mergeOnSuccess` defaults ON, so a green run **delivers**. What the run must not be allowed to do:

1. **Ship GR2060 at ERROR without the §5.3 allow-list entry.** This is the first prohibition because it
   is the one that costs someone else's work: an un-mitigated ERROR reverts a JIT partial prefix
   wholesale, re-opening #501 one code over. Tasks 5 and 6 are not optional polish on task 4; §5.5 makes
   the severity conditional on them.
2. **Allocate GR2070.** It is **held by name** (§6.3, §12.3). A run that reads the reservation block as a
   TODO and spends the code has shipped an unfalsified check into the ladder. Pinned by a forbidden-token
   guardrail: no `DiagnosticCodes` constant may be added whose value is `"GR2070"`.
3. **Raise GR2060's severity beyond ERROR, or lower it to silence a failing sweep.** Severity is a
   maintainer decision (§16) backed by a positive control and a sweep, not a knob for going green.
4. **Widen the extractor to make a test pass.** The one-hop association, the single-quote pattern rule
   and the git-tracked condition are each a place conservatism is spent (§5.1). Each is pinned by a
   **silence** test, and deleting a silence test is itself a finding.
5. **Weaken the sweep's expectation.** The expectation is **per plan and per commit** (§8.5) and includes
   a required **non-zero**: `model-tiering-stage-2` at `544f7d5` must produce exactly 1. A run that
   re-baselines the sweep to "≤ N findings", or that flattens it back to a blanket zero, has inverted the
   gate. It is a terminal gate, so this withholds delivery rather than merging.
6. **Relabel a control against the evidence — in EITHER direction.** The original prohibition read
   *"§8.3's condition-8 silence fixture is `Constructed`; a run that renames it `Recovered` has told the
   exact lie §3.4 caught."* The run found that condition 8 **is** exercised in the corpus at `5bd29da`,
   so the honest label there is now `Recovered` and §8.3 has been rewritten. The prohibition stands in
   its general form and is what matters: **a control's label states how its evidence was obtained, and
   may never be chosen to match its siblings, to satisfy a guardrail, or to make a section read
   tidily.** Calling a hand-built fixture `Recovered` is the lie §3.4 caught. Calling a genuinely
   recovered one `Constructed` — which this plan did until the run corrected it — understates real
   evidence, and is the same fault pointing the other way.
7. **Touch the run path beyond the one line §5.3 requires.** `RunCommand`, `TaskExecutor`,
   `IPromptRunner`, `IActionRunner` and `IProgressSink` are out of scope. Task 6 touches `Scheduler.cs`
   and touches **only** `UnsatisfiableWhileIncomplete`; no other behaviour in that file is in scope.
8. **Change a `PlanValidator` composition root's signature**, or leave the **73** existing
   `new PlanValidator(` call sites to absorb a new parameter silently (§13 task 2, N3).
9. **Self-consistency, which this plan must pass on its own terms.** Its SSOT clauses require content in
   `docs/plans/02-schemas-and-contracts.md`. Rows 7 and 8 of §13 own that file, so GR2060 is silent on
   this plan's own gate — as it must be. Any task added later that asserts SSOT content **must** be
   paired with a task owning the SSOT, or this plan trips the check it is building.
10. **The self-lock, stated precisely rather than dramatically — and the first draft got the mechanism
    wrong.** Task 4 ships an ERROR-severity check into `Guardrails.Core`. The run in flight executes via
    the **installed** CLI, so it does not pick the new code up mid-run; the lock is not immediate. From
    the next `dotnet tool update` onward, a **resume refusal** would come through
    `RunCommand.RunAsync`'s `PlanProbe.LoadAndValidate` (`RunCommand.cs:198-207`) — **not** through
    `Scheduler.cs:2213`, which the first draft cited. That line is inside `ValidatePlanAfterBreakdown`,
    the **JIT-breakdown gate**, and it is a *different* hazard with a *different* mitigation (§5.3).
    Naming the wrong seam would have sent the implementer to fix the wrong thing.

    Task 4's own guardrails must therefore include a `guardrails validate` of **this plan's folder** with
    the newly built binary, asserting zero GR2060 findings. A check that cannot validate the plan that
    built it has failed its first real test, and it is cheaper to learn that inside task 4 than at a
    resume three days later.

**Sized for cheap retries.** Every task is ≤ 3 files and one concern. Three tasks edit
`PlanValidator.cs`; they are strictly sequential by `dependsOn`, never parallel. Tests are authored
before the implementation they gate — including the #501 regression test, which is red before task 6 —
so a retry re-runs one narrow filter rather than a suite.

---

## 12. Exact SSOT and `DiagnosticCodes.cs` edits

### 12.1 New §4.8 in `docs/plans/02-schemas-and-contracts.md` — after §4.7 (which ends at line 1520) and before `## 5. Child-process contract`

Heading: `### 4.8 Guardrails that CANNOT PASS given what this plan BUILDS (validated, GR2060 — error)`.

Opening paragraph to state: the §4.7 three are decidable from **one script's own text**; this one is
**relational** — it reads the script, the union of every task's `writeScope`, and the workspace's current
bytes. Same consequence (red before the task runs, red forever, and `/guardrails-review` structurally
misses it because it hunts weakness while this guardrail is *strong*), different evidence base, hence a
sibling section rather than a fourth row in §4.7's table. Carry doc 19 §3.1's predicate and all ten
conservatism conditions verbatim, and the cross-reference to §14.1/GR2062. §4.7 gains one closing
sentence pointing forward.

**Two paragraphs this section must carry that doc 19 §6 did not anticipate:**

- **The two suppressions, and that they are not interchangeable** (§5.3). `PlanIsClosed` suppresses
  GR2060 for an **empty stub wave**. It does **not** cover an authored **partial prefix**, for which the
  suppression lives in `Scheduler.UnsatisfiableWhileIncomplete`, keyed on `wavePrefixIsIncomplete`. State
  plainly that `PlanIsClosed` returns `true` for a partial prefix and is therefore not a soundness
  guarantee for the JIT gate — the trap that cost this design a milestone's worth of rework.
- **The excused-not-vanished rule.** A GR2060 finding excused at the JIT gate still appears in the
  gate-decision report, and still errors under a plain `guardrails validate`. Suppression is about which
  verdict a finding may cast, never about whether an operator sees it.

### 12.2 §14.10's GR-code paragraph

- Record **GR2060** (`UnproducibleGateRequirement`) as **shipped**, and remove it from the
  reserved-by-name list.
- Add **GR2070** to the reserved-by-name list.
- Advance next-free to **GR2071**.
- Leave **GR2061** and **GR2054** reserved, unchanged.

Per that paragraph's own standing instruction, `DiagnosticCodes.cs` wins — re-verify immediately before
allocating, and beware `DiagnosticCodes.cs:395`, a **quoted historical** marker naming GR2047. The live
marker is at `:1026`.

### 12.3 `src/Guardrails.Core/Loading/DiagnosticCodes.cs` — the reservation block

The block currently at **`:1034`** (not `:1036`, which is GR2054) lists three reserved codes. After this
plan it lists three again, with a different membership: GR2060 leaves because it is allocated, GR2070
arrives because it is held.

- **Remove** the `GR2060 — docs/plans/19-producer-coverage.md §1 …` line; GR2060 is now a shipped
  constant above.
- **Add**, in the same idiom as its neighbours:

  > `GR2070 — docs/plans/33-unproducible-requirements.md §6.3 (a guardrail requiring a named argument`
  > `whose declaring member no task may widen). DESIGNED AND DECLINED: it has never fired on a real`
  > `defect at any commit in this repository — see §3.4. Do not allocate without a positive control.`

- **Advance** the `CURRENT next-free code` marker at `:1026` to **GR2071**.

The reason-line matters more than the reservation. A bare *"reserved"* invites the next author to spend
the code; a line that says *the design exists and the evidence did not* sends them to §6.3, which is
where the three durable findings are.

### 12.4 Two edits outside the SSOT

- **`docs/plans/19-producer-coverage.md`** — the status table's `Milestone A — harness half (GR2060)` row
  changes from `NOT BUILT` to a pointer at this plan. **D2 gains one sentence, and it is not the sentence
  the first draft proposed:** *"a later instance (#474, plan 30) looked like shape (a) with a derived
  path, and a lint for it was designed and declined — the shape has never occurred in a form a lint could
  see; see `33-unproducible-requirements.md` §3.4 and §6.3. D2 is unchanged and is now better evidenced."*
  No other edit; the document is not rewritten.
- **`docs/plans/03-roadmap.md`** — no change. GR2060 is not a v2 bet; it is v1 author-time validation.

---

## 13. Implementation handoff

Nothing starts until the #106 draft-PR review of this document is addressed. **This table is re-derived
for the post-review task set** — Milestone B's four tasks are gone, the #501 mitigation adds two, and
Milestone C is re-aimed. It is not the first draft's table with rows deleted.

Each row is deliverable by **one** task. The `writeScope` column is the **verbatim, concrete** array to
emit in that task's `task.json` — no globs, matching the convention that every `writeScope` in every plan
folder in this repo is concrete.

| # | Agent | Deliverable | `filesTouched` | pinned `writeScope` (verbatim) | depends on |
|---|---|---|---|---|---|
| 1 | `guardrails-harness-developer` | **Refactor, no behaviour change.** Lift GR2057's `PresenceClause`, `BranchFailsTheGuardrail`, `BlankCommentLines`, `TryLiteralWitness` and `MatchesWitness` out of `PlanValidator` into a shared internal helper, **unchanged** — no widening of the single-quote pattern rule (§3.5). Gate: every existing GR2057 test green and **unedited**. | `src/Guardrails.Core/Loading/GuardrailClauseText.cs`, `src/Guardrails.Core/Loading/PlanValidator.cs` | `["src/Guardrails.Core/Loading/GuardrailClauseText.cs", "src/Guardrails.Core/Loading/PlanValidator.cs"]` | — |
| 2 | `guardrails-harness-developer` | `IGitTrackedFileProbe` + `GitLsFilesProbe` + `NullGitTrackedFileProbe`, mirroring `IScriptSyntaxProbe` including its "silence is not proof" contract; a **fifth** `PlanValidator` constructor overload with a real default. **N3 gate: 73 `new PlanValidator(` call sites exist across `tests/` and `Guardrails.Cli`** — the task must assert the count, confirm every one still compiles unchanged, and state in its own commit message which default they now silently receive. | `src/Guardrails.Core/Loading/IGitTrackedFileProbe.cs`, `src/Guardrails.Core/Loading/GitLsFilesProbe.cs`, `src/Guardrails.Core/Loading/PlanValidator.cs` | `["src/Guardrails.Core/Loading/IGitTrackedFileProbe.cs", "src/Guardrails.Core/Loading/GitLsFilesProbe.cs", "src/Guardrails.Core/Loading/PlanValidator.cs"]` | 1 |
| 3 | `guardrails-test-author` | **Red** tests for GR2060: one firing + one silence test per §5.1 condition; §8.2's **recovered** positive control; §8.3's two negative controls; and §8.3's **RECOVERED** condition-8 silence control, named `Recovered…`, read from `5bd29da` and paired with `544f7d5`. | `tests/Guardrails.Core.Tests/ProducerCoverageTests.cs` | `["tests/Guardrails.Core.Tests/ProducerCoverageTests.cs"]` | 2 |
| 4 | `guardrails-harness-developer` | **GR2060** in a new `ProducerCoverage.cs` (the `HandoffScopeCoverage.cs` precedent — one check family, one file, one line in `PlanValidator`), the code constant, and the call site. Its own guardrails include a `guardrails validate` of **this plan's folder** with the newly built binary (§11 item 10). | `src/Guardrails.Core/Loading/ProducerCoverage.cs`, `src/Guardrails.Core/Loading/DiagnosticCodes.cs`, `src/Guardrails.Core/Loading/PlanValidator.cs` | `["src/Guardrails.Core/Loading/ProducerCoverage.cs", "src/Guardrails.Core/Loading/DiagnosticCodes.cs", "src/Guardrails.Core/Loading/PlanValidator.cs"]` | 3 |
| 5 | `guardrails-test-author` | **Red** #501 regression test (§5.3): a JIT partial prefix whose wave gate trips GR2060 must **not** be reverted, and the finding must still appear in the gate-decision report. Red before task 6, green after. | `tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs` | `["tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs"]` | 4 |
| 6 | `guardrails-harness-developer` | **The #501 mitigation**: add GR2060 to `UnsatisfiableWhileIncomplete`, keyed on `wavePrefixIsIncomplete` and **not** on `PlanIsClosed` (§5.3). One member of `Scheduler.cs` is in scope; nothing else in that file is. | `src/Guardrails.Core/Execution/Scheduler.cs` | `["src/Guardrails.Core/Execution/Scheduler.cs"]` | 5 |
| 7 | `guardrails-harness-developer` | **SSOT §4.8** (§12.1), including the two-suppressions paragraph and the excused-not-vanished rule, plus §4.7's forward-pointing sentence. | `docs/plans/02-schemas-and-contracts.md` | `["docs/plans/02-schemas-and-contracts.md"]` | 6 |
| 8 | `guardrails-harness-developer` | **The code ladder** (§12.2, §12.3): GR2060 removed from `DiagnosticCodes.cs`'s reservation block because it is now allocated; **GR2070 added, held by name**, with its reason line; next-free advanced to GR2071; SSOT §14.10 updated to match. | `src/Guardrails.Core/Loading/DiagnosticCodes.cs`, `docs/plans/02-schemas-and-contracts.md` | `["src/Guardrails.Core/Loading/DiagnosticCodes.cs", "docs/plans/02-schemas-and-contracts.md"]` | 7 |
| 9 | `guardrails-test-author` | **The §8.5 sweep**: all **850** `.ps1` under `docs/plans/` — waved folders included — plus `examples/`, each at its own pre-run commit where one exists, with the per-plan/per-commit expectation table **in the test file**, including the required **non-zero** on `model-tiering-stage-2` at `544f7d5`. Wired as a **terminal-gate** guardrail. | `tests/Guardrails.Core.Tests/ProducerCoverageCorpusTests.cs` | `["tests/Guardrails.Core.Tests/ProducerCoverageCorpusTests.cs"]` | 8 |
| 10 | `guardrails-skill-author` | **Milestone C** (§6.2): the callee's-parameter-list step 5 in `guardrails-review`'s Unreachable-outcome probe, and its authoring twin in `plan-breakdown` beside the **already-shipped** datum trace at `:381` — an addition, never a rewrite of that section. | `.claude/skills/guardrails-review/SKILL.md`, `.claude/skills/plan-breakdown/SKILL.md` | `[".claude/skills/guardrails-review/SKILL.md", ".claude/skills/plan-breakdown/SKILL.md"]` | 4 |
| 11 | `guardrails-skill-author` | One line in the knowledge skill naming GR2060 and the producer-coverage invariant, and recording GR2070 as held-not-allocated so the next design does not re-propose it. | `.claude/skills/guardrails-domain-knowledge/SKILL.md` | `[".claude/skills/guardrails-domain-knowledge/SKILL.md"]` | 8 |
| 12 | `guardrails-harness-developer` | Doc 19's status-table row and its **re-worded** D2 sentence (§12.4) — the decline, not the first draft's "shape (a) with a derived path". | `docs/plans/19-producer-coverage.md` | `["docs/plans/19-producer-coverage.md"]` | 8 |

**Sequencing.** 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → {9, 11, 12}, with **10 branching off 4** and running
beside 5–8. Milestone boundaries: **A** = 1–9 + 11 + 12; **C** = 10.

**Three constraints the order encodes rather than states.**

- Tasks 1, 2 and 4 all edit `PlanValidator.cs` and are strictly serial. Task 4 puts the check in a **new**
  file, so its edit there is one call-site line.
- **Task 6 must not lag task 4 by more than one task.** Between them the tree carries an ERROR-severity
  GR2060 with no `wavePrefixIsIncomplete` excuse. This plan is flat, so it cannot trip its own hazard —
  but a `--fresh` run of any *waved* plan against that intermediate commit could, and the window should
  be one task wide, not five.
- Task 10 touches no C# and no `docs/plans/`, so it is the only row that can run beside the chain.

### 13.1 Hand-run of GR2068 / GR2069 against this table

Run by hand against the pinned `writeScope` column, because this plan has **no task folder yet** —
`HandoffScopeCoverage` runs over a loaded `PlanDefinition` and is structurally silent until the breakdown
exists. The pinned column is what makes the hand-run possible, and it is the contract the breakdown must
emit.

| row | candidates | anchored? | covered by ONE task? | verdict |
|---|---|---|---|---|
| 1 | 2 | yes | yes — identical to row 1's scope | **silent** |
| 2 | 3 | yes | yes | **silent** |
| 3 | 1 | yes | yes | **silent** |
| 4 | 3 | yes | yes | **silent** |
| 5 | 1 | yes | yes | **silent** |
| 6 | 1 | yes | yes | **silent** |
| 7 | 1 | yes | yes | **silent** |
| 8 | 2 | yes | yes | **silent** |
| 9 | 1 | yes | yes | **silent** |
| 10 | 2 | yes | yes | **silent** |
| 11 | 1 | yes | yes | **silent** |
| 12 | 1 | yes | yes | **silent** |

**GR2068 × 0, GR2069 × 0.** Every row's `filesTouched` is exactly its own task's `writeScope`, so no row
splits and no path is unreachable.

**The reason the first draft gave for the "anchored?" column was wrong, and the correction matters.** It
said the candidates resolve because they are repo-rooted paths. `IsAnchored`
(`HandoffScopeCoverage.cs:303-318`) never touches the filesystem: it takes the candidate's **first
segment** and asks whether that string equals a **whole `/`-delimited segment of some `writeScope` entry
in this plan** — the plan's own path vocabulary, nothing else. Repo-rootedness is irrelevant.

**The consequence is a real property of this table, not a footnote.** Rows 10 and 11 are the only ones
whose candidates begin with `.claude`, and the only `writeScope` entries in the whole plan containing a
`.claude` segment are rows 10's and 11's own. So if those two rows' scopes were dropped or renamed, their
`filesTouched` candidates would become **unanchored and silently dropped** — the check would go quiet
rather than emit GR2068. Every other first segment (`src`, `tests`, `docs`) appears in several rows and
survives any single row's loss.

That is the anchor test working as designed — it declines to judge a cell written outside the plan's own
vocabulary — but it means **the `.claude` rows are the ones a reviewer must check by eye**, because they
are the rows this check protects least.

---

## 14. Out of scope

- **GR2070 itself** — designed, declined, held by name (§4.1, §6.3, §12.3). Not "deferred pending
  capacity": **declined for want of a single instance where it fires.** Revisiting it needs a defect at a
  commit, not a clause written in the right shape.
- **A dataflow-reachability lint** — doc 19 D2, §6.4. Permanent.
- **Positional arity requirements** — §3.1 item 2. The largest uncovered shape, and there is no proposal
  for it: a comma-counting regex over a call's argument list carries no name to compare against a
  declaration.
- **`Type.Member` derivation** — §3.6(a). Rejected on measurement: 3 fires, 3 wrong, and the failure is
  intrinsic rather than a bug to fix.
- **"Token `T` nowhere in the task's scope"** — §3.6(b), doc 19 §2.2. The loudest wolf in the family.
- **Relaxing GR2057's single-quote pattern rule** — §3.5. The one relaxation this design proposed died
  with GR2070; a future one must show a defect in the relaxed form first.
- **`.sh` guardrails** — GR2057's precedent; ships when a `.sh`-only corpus exists.
- **Multi-hop variable association** — doc 19 §5. One hop covers the measured instance.
- **AST-based clause extraction** — welcome if an in-process parser ever becomes free; not required, and
  must not be the reason this slips.
- **Any other change to `Scheduler.cs`** than the one member §5.3 names. The #501 seam is being touched
  precisely because it is delicate.
- **A `filesTouched` handoff table on this plan being *generated* by the breakdown.** Plan 30's breakdown
  declined to author one and was right: a table written from the author's own `writeScope`s is green by
  construction. §13's table is **declared by a human in this document**, ahead of the breakdown, and the
  breakdown's job is to match it.

---

## 15. Risks accepted

1. **Milestone C is procedural, and procedures get skipped.** With GR2070 declined, the plan-30 carrier
   shape is covered by **one paragraph in two skills** and by nothing mechanical (§7's second table).
   That is a genuine reduction in coverage against the first draft's claim, and it is accepted because
   the alternative — a lint with no positive control — is worse than an honest gap. **Falsification
   trigger:** a second carrier-shaped defect that reaches a run *after* Milestone C ships means the step
   is not being executed, and the answer is then a stronger authoring artifact, not a smarter lint (doc
   19 §5 item 3, verbatim).
2. **GR2060's sweep zero is structural in two ways, not one** (§5.4). Today's tree satisfies these plans'
   requirements *because the plans ran*, **and** the hand-run walked 533 of 850 scripts. §8.5's
   pre-run-commit sweep across the full population is the version that could fail, and it is the merge
   gate. Until it runs, GR2060's conservatism rests on one recovered positive control and doc 19's
   argument — which is more than GR2070 ever had, and less than GR2055/GR2039/GR2057 earned.
3. **The #501 mitigation widens an allow-list, and allow-lists grow.** `UnsatisfiableWhileIncomplete`
   goes from one code to two. Every future ERROR-severity relational check will face the same question,
   and the honest answer is that the list is a **register of codes a partial prefix cannot fairly
   satisfy** — not a place to park inconvenient findings. If it reaches four or five entries without each
   one carrying a #501-shaped regression test, that is the signal the design has drifted.
4. **GR2060 at ERROR can block a resume.** If it false-fires on a shipped plan, that plan cannot be
   resumed until the finding is fixed or the code is backed out. Accepted on doc 19 D4, on the recovered
   positive control, and on §11 item 10's self-validation gate. The escape hatch is deliberately absent,
   because a suppressible producer-coverage check is one an author silences instead of fixing.
5. **`PlanIsClosed` remains a name that invites the mistake this plan just made.** It reads as *"the plan
   is closed"* and means *"no wave folder is empty."* §12.1 documents the gap in the SSOT rather than
   renaming the predicate, because renaming it is a change to shipped GR2062 behaviour that this plan has
   no evidence to justify. The next reader is protected by prose, which is weaker than a better name.
6. **This plan does not close #474, and closes less of it than the first draft claimed** — §9's closing
   paragraph. Saying so plainly is the point; the alternative is a closed issue and an open defect.

---

## 16. Decisions this plan leaves to the maintainer

**Decided 2026-09-02 by the lead session under the standing autonomy mandate — the maintainer has not
yet seen any of this.** D-a was decided twice by that same session: first *"all three,"* then **reversed**
on evidence an independent adversarial pass produced and the lead session verified at the source. The
reversal is recorded rather than overwritten, because the reason it happened is the most useful thing in
this document. Every row below is a lead-session call and is open to the maintainer overturning it; D-a
and D-f are the two worth his attention first.

| # | decision | outcome and why |
|---|---|---|
| D-a | **Take Milestone B (GR2070)?** | **DECLINED — reversed 2026-09-02, by the lead session that made the original call.** It first decided *"all three,"* reasoning that B's marginal cost once A exists is three tasks and that *"cost is not the constraint; a check that certifies nothing is."* The second half of that sentence turned out to indict B rather than justify it. The adversarial pass traced the motivating clause through git: at `10816fb` and at the broken-scope moment it reads `'pending\s*\.\s*Bucket'` — **single-quoted, no named argument** — and the named-argument form first appears at `124a7d0`, **36 minutes after the scope was fixed**. **B's true-positive population across the repository's whole history is zero.** GR2070 is **held by name** in `DiagnosticCodes.cs`, not allocated (§12.3). |
| D-b | ~~GR2070 at WARNING or ERROR?~~ | **Moot.** B is declined; no severity to set, no §4.9 to write. |
| D-c | **Does #474 close?** | **No, and less of it closes than the first draft claimed.** Milestone A closes the *named-path* coverage shape at both altitudes. The **plan-30 carrier shape closes mechanically nowhere** — B is declined and §6 shows the shipped review probe stops one step short of it. Milestone C is what covers it, and it covers it procedurally. Comment on #474 saying exactly that; it stays open. |
| D-d | **Does the sweep run at each plan's pre-run commit, or only against `HEAD`?** | **Pre-run**, for the plans that have one — and the sweep must **walk waved folders**, which the first draft's did not (§5.5). It is the only version of the sweep that can fail, which is the whole of #580. |
| D-e | **Does §13's table get emitted into the breakdown, or stay declared here only?** | **Stays declared here.** A breakdown-authored handoff table is green by construction; §13.1's hand-run is evidence precisely because the breakdown did not write it. |
| D-f | **New — how is the #501 veto mitigated?** (§5.4) | **An allow-list entry keyed on `wavePrefixIsIncomplete`**, not on `PlanIsClosed`. `wavePrefixIsIncomplete` is *actual knowledge* that folders are still owed; `PlanIsClosed` only detects an **empty stub wave** and returns `true` for an authored partial prefix, which is the case that breaks. Task 6 of §13, with the #501-shaped regression test in task 5 ahead of it. |

---

## 17. Devil's-advocate self-critique

The first draft's self-critique is superseded. It defended a milestone that has since been declined, on a
premise that turned out to be false — which is itself the most useful entry in this section, so the
failure is recorded rather than deleted.

**What the first draft's self-critique got wrong, and why it is instructive.** It anticipated the
objection *"a check with a population of one"* and answered it with cost-of-failure reasoning. It never
asked the prior question — **does the check fire on the instance at all?** — because it had already
written the positive control from the issue's narrative rather than from `git show`. An adversarial pass
that opened four commits falsified in minutes what a devil's-advocate pass had defended at length. **The
lesson is not "argue harder"; it is that a self-critique which does not re-execute its own evidence is
prose.** §8.2's blind reproduction is the discipline that would have caught it.

---

**The strongest counter now: "You have declined the only new thing in this plan. What is left is
implementing someone else's twelve-day-old design and adding a paragraph to two skills."**

**Largely conceded, and it is the right outcome.** What is left is: a shipped GR2060 with a recovered,
condition-by-condition verified positive control; a mitigation for a **live** #501-class defect that shipping
GR2060 would otherwise cause; and the one procedural step that covers the plan-30 shape. The document's
own thesis — *a prompt may propose, only a deterministic gate may certify* — applies to designs too. A
design that survives review with less in it than it started with is the mechanism working.

The honest residue: **the issue that motivated plan 33 (#474) gets less mechanical coverage than the
first draft promised**, and §9 and §15 risk 1 both say so in those words.

**"You are shipping an ERROR-severity check into `Scheduler`'s veto path on the strength of one positive
control."**

**Real, and it is why tasks 5 and 6 exist and why §5.5 makes the severity conditional on them.** The
counter-counter is that the alternative is not safety: GR2060 at WARNING leaves the $115.32 terminal-gate
class uncaught, since a warning does not stop a run. If the reviewer wants WARNING, §16 gains a row and
the plan still ships coherently — it just does not close the instance it was written for.

**"The #501 mitigation makes GR2060 silent exactly when a plan is most likely to be wrong — mid-JIT,
scope incomplete."**

**Conceded, and it is correct behaviour rather than a compromise.** With folders still owed, *"no task
can produce this"* is not merely unproven, it is **false** — the producing task has not been authored
yet. A check that fired there would be reporting the absence of work that is scheduled to happen. The
finding still appears in the gate-decision report (§5.3), so nothing is hidden; only the veto is
withheld.

**"`PlanIsClosed` fooled you once. What else in doc 19 §3.1's ten conditions did you adopt without
re-verifying?"**

**The sharpest question in the review, and I cannot fully answer it.** §2 re-verified six of doc 19's
premises and found two wrong; the #501 interaction was found by someone else. Conditions 2, 3 and 4 (the
path operand, the one-hop association, the witness extractor) are exercised by §8.3's negative controls
and by the positive control, so they will be tested. Conditions 6, 7 and 9 (git-tracked, not under the
plan folder, GR2041 clean) have **no corpus exercise and no constructed fixture named for them** beyond
§8.1's per-condition rule. §8.1 is therefore not a formality: if the implementer cannot construct a
silence fixture for a condition, that condition is not known to do anything, and the honest response is to
say so in the PR rather than to assert ten conditions and test six.

**"Three tasks editing `PlanValidator.cs` in worktree mode is the merge-collision shape #175 exists for."**

**Real, and handled by shape rather than by hope.** Tasks 1, 2 and 4 are chained by `dependsOn`, so they
serialise; task 4 puts the check in a **new file** (`ProducerCoverage.cs`, on `HandoffScopeCoverage.cs`'s
precedent), so its edit there is one call-site line.

**"Doc 19 had a skill obligation that shipped and still missed the defect. Why will Milestone C be
different?"**

**The best objection to what remains, and it is only partly answerable.** Doc 19's datum trace **did**
ship (`e78b9d`) and the plan-30 instance still got through — because the procedure stops at step 3
(§6.1). C adds the step that continues past it, which is a real gap closed rather than a restatement. But
it is still a procedure, executed by an agent, at review time. §15 risk 1 prices that honestly and names
the trigger that would falsify it. Anyone who wants a stronger answer than a procedure needs to produce
the thing this plan could not: **a defect, at a commit, where a lint would have fired.**
