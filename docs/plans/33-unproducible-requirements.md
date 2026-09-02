# 33 — Unproducible requirements: producer coverage, and the file the guardrail never names (#474)

**Issue:** #474 — *A guardrail can demand an outcome the task's `writeScope` cannot reach: the datum's
path runs through a file the task may not write.* This document is the **mechanical half**. The prose
half shipped separately as #578 (`de4e17c`); §7 states how the two divide the work.

**Status:** design of record. Delivered as a draft PR for inline review (#106) before any implementation
milestone starts.

**Why this is its own plan, and not a paragraph appended to `19-producer-coverage.md`.** Doc 19 is the
design of record for this family and it is *half shipped*: its skill milestone landed at `e118b9d`, its
`intendedWaves`/GR2062 milestone landed, and **GR2060 — the harness half, the actual mechanical answer
to #474 — was never built.** It has sat reserved-by-name in `DiagnosticCodes.cs` for twelve days. A new
instance was measured on plan 30 yesterday that doc 19's design would *not* have caught, and closing that
gap changes doc 19's §2 decidability table. Editing a half-shipped design in place to record both facts
is how the reasoning behind a contract change gets lost. This gets its own design, its own review and
its own run; doc 19 gains a status pointer and nothing else (§12).

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

## 2. The premise, re-verified (#563)

#563 requires a design citing an issue to re-check the issue's load-bearing claims rather than inherit
them. Done against `09f223f`.

| claim | verdict |
|---|---|
| GR2060 is designed and **unbuilt** | **Holds.** `DiagnosticCodes.cs:1036` lists it among "THREE codes RESERVED BY NAME … must not be re-used". No constant, no check, no test. Doc 19's own status table says `NOT BUILT`, dated 2026-08-20. |
| `PlanValidator.PlanIsClosed` exists | **Holds, and is better than doc 19 assumed** — `PlanValidator.cs:3395`, written for GR2062 and documented in place as GR2060's suppressor. Milestone A inherits it built. |
| GR2057's de-regex witness extractor exists | **Holds** — `TryLiteralWitness` (`PlanValidator.cs:2707`) and `MatchesWitness` (`:2802`), both `private static`, both used only by `ValidateGuardrailRequiresForbiddenToken`. The refactor doc 19 §10 step 1 asks for has not happened. |
| `IGitTrackedFileProbe` exists | **Does not.** `src/Guardrails.Core/Loading/` holds `IScriptSyntaxProbe` + `InterpreterScriptSyntaxProbe` and nothing else probe-shaped. |
| SSOT §4.8 exists | **Does not.** §4.7 runs to line 1520 and §5 begins at 1521. Doc 19 §6 specified §4.8 verbatim and it was never applied. |
| `RunCommand` refuses a plan carrying any validation error | **Holds** — `RunCommand.cs:198-207`, `probe.HasErrors` → `ExitCodes.HarnessError`, *"Validation failed; nothing was run."* This is the fact that decides both severities (§5.4, §6.5). |
| `ISchedulerJournal.RecordSettleWithAttempt` was 5-arity when plan 30 was authored | **Holds** — at `10816fb`: `taskId, attempt, status, mergeSequence, definitionHash`. `RunJournal`'s public overload was 6-arity, but its sixth is `definitionHashAtSettle`. **No `bucket` parameter existed anywhere under `src/`.** |
| plan 30's task 16 did not own the carrier | **Holds** — at `10816fb` its `writeScope` was `["…/AttemptJournaler.cs", "…/Scheduler.cs"]`. Fixed later at `62d7314`. |

**Two corrections the re-verification forced.** Both are load-bearing and both are in §3.

1. **Doc 19 §2's decidability table classifies #474 as shape (b), reachability, and rules it out
   permanently (D2: *"not decidable … gets no lint, ever"*).** That verdict is correct **for the
   instance doc 19 measured** and wrong as a statement about the issue. Plan 30's instance is shape
   **(a), coverage** — the carrier file is one **no task may write**. Doc 19 could not have known: it
   was written seven weeks before that instance existed. §3.4 states the boundary precisely, and D2
   survives it intact.
2. **The maintainer's proposed predicate, read as written, would have been silent on the very instance
   it was proposed for.** *"Is `M`'s declaring file in some task's `writeScope`?"* — `RecordSettleWithAttempt`
   has two declaring files, and **one of them was owned** (task 06 held `RunJournal.cs`). The existential
   reading returns *yes* and says nothing. §3.3 has the measurement and the fix.

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

### 3.4 The qualified check: 0 false positives, and a population of one

Adding the two qualifiers — a **named-argument** requirement, and **no declaration of `M` anywhere
declares that parameter** — over the same 443 scripts:

| | count |
|---|---|
| candidate `(M, p)` pairs | **2** (`bucket`, `definitionHash`, both from the one script) |
| would fire against today's tree | **0** — the plan-30 folder is fixed and the interface now declares `bucket` |
| **false positives** | **0 / 443 (0.0%)** |
| would fire against the tree and folder **as they stood at `10816fb`** | **1**, naming `ISchedulerJournal.cs` |

The positive control is exact. At `10816fb`: `bucket` was declared on **no member anywhere under
`src/`**; `RecordSettleWithAttempt` was declared in `ISchedulerJournal.cs` and `RunJournal.cs`; the plan's
scope union covered the second and not the first; the plan is flat, so `planIsClosed` is trivially true.
Every condition holds and exactly one finding is produced.

**And the honest numbers beside it. The shape occurs in 1 script of 443, and 1 plan of 6 — and a zero
over a population of one is not evidence of conservatism.** Doc 19 drew exactly this distinction when
GR2062's sweep came back clean: *"Milestone B's false-positive zero is STRUCTURAL, not empirical … the
distinction should not be blurred when the next lint cites the precedent."* This is the next lint, and it
is citing that precedent, so: **GR2070 has not yet had an opportunity to be wrong.** GR2055/GR2056/GR2057
earned an empirical zero against 500+ real scripts. GR2070 has not, and §15 risk 1 is where that is
priced rather than papered over.

### 3.5 The clause form is rarer than the anchor, and reusing GR2057's reader ships the check MUTE

This was caught by executing the spec against the corpus rather than reading it, which is #580's whole
point. Doc 19 §3.1 condition 4 says to reuse **GR2057's shipped extractor**. GR2057's clause regex
(`PlanValidator.cs:2515`) accepts a **single-quoted pattern operand only** — deliberately, because a
double-quoted regex makes `$` ambiguous between an anchor and an interpolation. Measured over the 443
scripts:

| clause form | count |
|---|---|
| `if ($v -match '…') { … }` — **single-quoted**, GR2057's surface | **1,172** |
| `if ($v -match "…") { … }` — **double-quoted**, excluded by GR2057 | **6** |
| named-argument requirements among the 1,172 | **0** |
| named-argument requirements among the 6 | **1** — the measured instance |

**GR2070 built on GR2057's extractor verbatim would have a population of zero and would ship completely
mute.** Its one true positive is a double-quoted clause, because the guardrail interpolates a
discovered binding name (`$c`) into its pattern — which is *why* it is double-quoted.

§6.2 condition 4 therefore specifies a **sibling** clause reader, and §6.2's soundness argument is that
GR2070 needs only the pattern's **literal head**, never the whole pattern, so the `$`-ambiguity GR2057
refuses to touch never arises.

And the fact that argues the other way, recorded in the same breath: **5 of those 6 double-quoted clauses
are in the one script**, and the sixth is a `$member` interpolation in plan 28. The form is not an
emerging convention; it is what two authors reached for when they needed a variable in a pattern. §15
risk 1 and §16 D-a both turn on this number.

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
declaration-count bound of §6.2 c6 applied, since that bound is what kills `RunAsync` (66 declarations)
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

**As a standalone diagnostic: no.** GR2070 alone needs a clause extractor, a requirement-polarity reader,
a `planIsClosed` gate, a repository probe, the union-of-scopes rule, an SSOT section and a test suite —
roughly 90% of GR2060's build — to serve a shape that occurs in 1 script of 443. Shipping that alone is
the thing this repo keeps filing issues about: a check that looks rigorous and certifies almost nothing.

**As the second rule of a producer-coverage check whose first rule is already designed and unbuilt:
yes, with the counter-number stated.** GR2060 is the mechanical answer to #474 that doc 19 specified and
nobody built. It catches the **$115.32 terminal-gate instance**, the most expensive one on record.
GR2070 is one additional **path-derivation rule** on the identical machinery: where GR2060 asks *"can
anyone write the file the guardrail **names**?"*, GR2070 asks *"can anyone write the file the guardrail
**implies**?"* One question, two ways of getting to a path.

**The ranking, and it is not a tie: A ≫ C > B.**

- **A removes a review step.** The `/guardrails-review` missing-insertion check, pointed at the gate
  folders, becomes mechanical for its coverage subset. That is what #570's Phase A′ actually asked for.
- **C is one paragraph in a skill** that doc 19 specified and never shipped. Nearly free.
- **B removes no review step.** The reachability probe still has to run for the shape GR2070 cannot see
  (§6.6), so a reviewer's pass over carriers is not retired by it. Its value is entirely in the case
  where that pass is skipped or the plan is edited after review.

**And the number that argues for declining B**, stated so the maintainer can act on it rather than take
my word: its clause form occurs **6 times in 443 scripts, 5 of them in the single script that motivates
it** (§3.5). This is not an emerging convention. Declining B is defensible, and §16 D-a makes that an
explicit choice rather than a silent one.

**My recommendation is still to build it** — the marginal cost once A exists is three tasks on machinery
already there, at WARNING, with a written back-out trigger — because the failure it prevents is a false
green that detonates 26 tasks downstream and is attributed to the wrong task. But the case rests on the
cost of the failure, not on the frequency, and the document should not pretend otherwise.

### 4.2 Three milestones, sequential, each green before the next

| # | milestone | ships | approvable alone? |
|---|---|---|---|
| **A** | **GR2060 — `UnproducibleGateRequirement`** (doc 19 §3.1, built) | ERROR | **yes** — take A only and the design still stands |
| **B** | **GR2070 — `UnproducibleCallArgument`** (this document) | WARNING | no — depends on A's machinery |
| **C** | the authoring rule in `plan-breakdown`, and the knowledge-skill line | — | yes, but worth nothing before A |

**If the maintainer takes A only**, #474's most expensive measured instance is closed mechanically and
the plan-30 instance stays with the review probe, which did catch it. That is a coherent outcome and it
is stated here so that taking it is a choice rather than a retreat.

### 4.3 Placement

| piece | placement |
|---|---|
| GR2060, GR2070 | **harness** — `Guardrails.Core/Loading`, author-time only |
| SSOT §4.8, §4.9, §14.10's code paragraph | **schema** — lands in the same commit as the code (invariant 4) |
| the sibling-datum authoring rule | **skill** — `plan-breakdown` (doc 19 §4 specified it; it did not ship) |
| the reachability probe, the missing-insertion extension | **already shipped** — `e118b9d`, do not touch |
| a dataflow-reachability lint (#474's *first* headline) | **out of scope, permanently** — doc 19 D2 stands (§6.6) |
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

### 5.2 Three things doc 19 assumed that are now facts

- **`PlanIsClosed` is built** (`PlanValidator.cs:3395`) and already documented as GR2060's suppressor.
  Milestone A consumes it; it does not write it.
- **`PlanJson.cs` does not hold the raw config** — doc 19 §10 step 6 named a file that does not exist for
  that purpose; the raw deserialization target is `RawManifests.cs`. Milestone A does not touch either,
  but the handoff table must not repeat the wrong name.
- **`PlanValidator` has a four-overload constructor chain** ending at
  `(IExecutableProbe, BannedPatternRegistry, IScriptSyntaxProbe)`, with a parameterless overload used by
  **two production composition roots** (`PlanProbe.cs:86`, `Scheduler.cs:2213`). The tracked-file probe
  arrives as a **fifth overload with a real default**, exactly as the syntax probe did. Neither
  composition root changes signature.

### 5.3 The corpus sweep, run in advance

Doc 19 §10 step 4 makes a zero-finding sweep a merge gate. Hand-run now, over **all 14 plan folders in
`docs/plans/` that carry a `tasks/` directory** — not just 26–32:

| | |
|---|---|
| `-notmatch` requirement clauses seen | 147 |
| …with a one-hop literal path operand | 41 |
| …with a de-regexable exact witness | 13 |
| …whose witness is **absent** from the named file | **0** |
| **GR2060 findings** | **0** |

**State the limit with the number, on doc 19's own precedent about GR2062.** Today's tree is post-merge,
so the witnesses these plans required are present *because the plans ran*. The zero proves the check is
silent on satisfied requirements; it does not prove it is silent on a *correct plan whose work is not yet
done*. The gate that proves that is the positive control (§8.2), and the sweep the implementers run must
be re-run **against each plan's own pre-run commit** for at least plans 30 and 32 — a stronger form than
doc 19 asked for, added here because it is the version that could actually fail.

### 5.4 Severity: ERROR, and the defense

`RunCommand.cs:198-207` refuses to run a plan carrying any validation error, so an ERROR is a
run-blocking gate on every plan forever, including on **resume**. GR2068/GR2069 ship as WARNING for
exactly that reason: a correct shipped plan can carry a stale handoff cell.

GR2060 is different in kind and stays at ERROR, per doc 19 D4:

- Its verdict is a **provable impossibility about the run about to start**, not a judgement about a
  document. The clause is red now, red at the end, and no agent action inside the plan can change it.
- The alternative is not "the run succeeds". It is "the run spends its whole DAG and fails at the gate" —
  measured at $115.32.
- Its false-positive surface is a **path**, which is unambiguous. GR2070's is a **name**, which is not
  (§6.5) — and that difference is the whole reason the two severities differ.
- The empirical bar is met: 0 findings over 14 plan folders, with §5.3's limit stated.

---

## 6. Milestone B — GR2070 `UnproducibleCallArgument`, the derived path

### 6.1 The predicate

> A script guardrail requires a **named argument `p:`** inside a call to member `M`; **no declaration of
> `M` anywhere in the tracked tree declares a parameter named `p`**; and **at least one file declaring
> `M` is covered by no task's `writeScope`**. The call cannot be written without widening a signature the
> plan may not touch.

### 6.2 Conditions — every one is conservatism spent

1. **PowerShell script guardrail**, any of the six folder instances
   (`PlanValidator.FourFolderScriptGuardrails` already enumerates all six, terminal gate included).
2. **A call anchor.** A quoted single-line literal that, after normalising `\b` and `\s*` away (§3.1),
   contains `(?<![A-Za-z0-9_])(?<M>[A-Z][A-Za-z0-9_]{2,})\\?\(`.
3. **Exactly one distinct `M` in the script.** Two or more → **silence**. Association is by
   co-occurrence and there is no sound way to pick. The corpus has never exercised the multi-anchor case
   (n=1), so this is silence-by-default rather than a rule with evidence behind it.
4. **A named-argument requirement**, read by a **sibling clause regex — NOT `PresenceClause` itself**
   (§3.5: reusing GR2057's verbatim gives this check a population of zero). The sibling admits a
   **double-quoted** pattern operand as well as a single-quoted one; polarity is decided by GR2057's
   shipped `BranchFailsTheGuardrail` reader, unchanged (the branch appends to a failures accumulator,
   exits non-zero, throws, or `Write-Error`s).

   **The soundness rule that makes the double-quote relaxation safe, and it is narrow on purpose.** Take
   the pattern's **head** — everything before the first `$` or backtick — and require that the head alone
   satisfies `^(?<p>[a-z][A-Za-z0-9_]*)(?:\\s\*)?:`, **case-sensitively**. A head containing no `$` and no
   backtick is its own literal content, so no interpolation analysis is needed and GR2057's reason for
   refusing double quotes never arises. GR2057 must de-regex the *whole* pattern and therefore cannot make
   this relaxation; GR2070 needs only the parameter name, and the parameter name is always in the head.
   Verified on the measured instance: `"bucket\s*:\s*$c\s*\.\s*Bucket"` → head `bucket\s*:\s*` → `p = bucket`.

   **Case-sensitivity is load-bearing, not style.** Reading it with `-match` instead of `-cmatch` turns
   1 hit into 295, every extra one a prose string in a failure message beginning `PRECONDITION:` or
   `NOTE:`.

   **And the clause form, not merely the literal, is what is required.** Scanning every quoted literal in
   the script instead of only `if (…) { … }` clause operands re-admits `definitionHash:` — which appears
   in this same guardrail's *failure-message prose*, not in any pattern. The clause form drops it and
   takes the candidate count from 2 to 1.
5. **`M` is declared in at least one tracked `.cs` file.** Zero → **silence**: the plan is creating it,
   which is the red-test archetype and never a defect.
6. **`M` is declared in at most 3 tracked files.** Four or more → **silence**. Measured: the real
   instance has 2; the noise class begins at 4 (`Capture`) and runs to 66 (`RunAsync`). A name declared
   in five places is a common name, and this check has no way to know which declaration the call binds to.
7. **No declaration of `M` declares a parameter named `p`.** Any one does → **silence**: the call
   compiles today and the requirement is about the call site, which the task owns.
8. **At least one declaring file is covered by no task's `writeScope`** — union over every task in every
   wave, decided by `WriteScope.IsInScope`, never `dependsOn` (doc 19 §7). Universal quantification over
   declaration sites; §3.3 is why.
9. **Declaring files under the plan folder are excluded** — harness-written, invariant 2.
10. **`planIsClosed`** — an un-authored JIT wave may own the carrier; the verdict is unprovable.
11. **GR2041 clean** — an undeclared `writeScope` makes the union incomplete.
12. **The tracked-file probe answered.** Probe absent, git absent, or the call failed → **silence, not
    failure** (GR2056's contract).

### 6.3 Message shape

> `GR2070` — this guardrail requires the named argument `bucket:` in a call to `RecordSettleWithAttempt`.
> No declaration of that member accepts a parameter named `bucket`, so satisfying this clause means
> **widening a signature** — and `src/Guardrails.Core/Execution/ISchedulerJournal.cs`, which declares it,
> is in **no task's `writeScope`**. As written the call does not compile, and the in-scope alternatives
> are an honest halt or a cast to the concrete type that satisfies this pattern and journals nothing.
> Either give some task the declaring file, or move the clause to a task that owns it.

The message names the **false green** as well as the finding, because in this class the false green is
the outcome that actually ships. It offers no correction beyond the two structural ones — GR2068's rule,
that a wrong suggestion is worse than none, holds here.

### 6.4 Cost — nothing is read unless there is a candidate

Conditions 2–4 are text operations over scripts the validator already reads. Only when they all hold
does the check touch `.cs` at all, and then it takes the tracked-file list it already has (Milestone A's
probe, one `git ls-files` per validate run), filters to `.cs`, prefilters with `string.Contains(M)`, and
regex-parses **only the files that survive**. Measured: candidates exist in 0.2% of scripts, so on
99.8% of plans the marginal cost is zero. **No second probe, no `git grep` per candidate, no cache.**

### 6.5 Severity: WARNING, and the defense

GR2070 does **not** follow GR2060 to ERROR, and the asymmetry is deliberate.

- **GR2060 keys on a path; GR2070 keys on a name.** A path is unambiguous. A member name is resolved
  across the whole tree by a textual extractor that, during this design alone, was wrong three separate
  ways — twice in the silent direction, once (the `Type.Member` trial) in the firing direction.
- **The evidence base is one instance.** An ERROR is a promise that every finding is a defect. Zero false
  positives over 443 scripts with a population of 2 candidate pairs does not support that promise; it
  supports *"nothing contradicts it yet"*.
- **The known false-positive shape is real and unfixed:** a declaring file that is a **test double** which
  the widening does not require changing — an interface member with a default body, precisely the shape
  `ISchedulerJournal.RecordSettleWithAttempt` has. No such declaration exists in today's corpus, so the
  shape is unmeasured, not absent.
- **The failure costs are asymmetric.** A false ERROR blocks a correct plan from running *and from
  resuming* — including, during this plan's own run, a resume of the run that shipped it. A false WARNING
  costs one line of reading.
- **A warning already collects the whole win.** The defect costs 26 tasks and a terminal-gate detonation.
  A warning at author time is 26 tasks earlier.

**Promotion to ERROR** on GR2068's stated bar, and not before: a hand-run of this code alone across every
plan folder in the repo, at each plan's own pre-run commit, producing only genuine defects. §16 leaves
that to the maintainer.

### 6.6 Why this does not reopen doc 19 D2

D2 — *"#474's headline (reachability) is not decidable and gets no lint, ever"* — stands, unamended.

| | doc 19's instance (shape **b**) | plan 30's instance (shape **a**) |
|---|---|---|
| the file that must change | `ActionRunner.cs` — **owned**, by a merged sibling task | `ISchedulerJournal.cs` — **owned by nobody** |
| what is missing | a *value* on a type: `ActionRun` has no `Usage` | a *parameter* on a member declaration |
| to decide it you must | resolve a parameter's type, walk to its declaration, enumerate its members, infer which one the required expression sources from | read one parameter list, bounded by its own parentheses |
| verdict | C# semantic analysis. **Not a lint.** | a coverage question with a derived path |

The two instances live on the same issue and are not the same defect. GR2070 covers exactly the
right-hand column and **would not have caught the left-hand one** — stated here in the design rather than
discovered later, because presenting it as a fix for #474 would be the overclaim this whole document is
arguing against. #474 **does not close** on this plan; §9 says what it closes and what it does not.

---

## 7. Relationship to #578, and to the review probe — complements, not alternatives

#578 shipped yesterday (`de4e17c`): a `/guardrails-review` probe that **executes** the structural claims
a prompt makes about the code. It explicitly declined a mechanical `validate` check, on the grounds that
prose claims have nothing statically decidable to gate on. That reasoning is right and this plan does not
disturb it: GR2060/GR2070 gate on **guardrail scripts** — machine-readable by construction — never on
prompt prose.

| | #578 probe (shipped) | #474 reachability probe (shipped, `e118b9d`) | GR2060 / GR2070 (this plan) |
|---|---|---|---|
| subject | a prompt's claim about the tree | a guardrail's datum and its carrier | a guardrail's requirement and the union of scopes |
| the defect it names | the **map is false** | the **value has no route** | the **requirement has no producer** |
| when | review, once | review, once | every `validate`, every run start, every **resume** |
| who | a non-authoring agent, 15–20 min | a non-authoring agent | nobody |
| its failure mode | **nobody ran it** | **nobody ran it** | **the shape was not extractable** |

**What the probes catch that no diagnostic can:** any claim expressible only in prose (*"A funnels
through B"*, *"there are nine sites"*), the semantic reachability shape (§6.6), a claim about a file the
plan will **create**, and the case where the guardrail is strong and correct while the *instructions*
around it are wrong — which #570's table says is six of seven recent defects.

**What the diagnostic catches that no probe can:** the case where nobody thought to look. The plan-30
instance *was* caught by the reachability probe — by a reviewer who chose to trace a datum to its
carrier instead of to the file the guardrail named. Everything about that catch was discretionary. A
diagnostic removes the discretion, and it keeps firing on the plan that is edited after review, and on
the resume six days later.

Their failure modes do not overlap, which is the definition of a complement. Neither replaces the other,
and neither should be described as the fix for #474 on its own.

---

## 8. How this is tested — a check is not authored, it is proven to fire (#580)

Six verifications came back green while doing nothing in one session. Every pin below therefore carries
its own evidence that it bites.

### 8.1 Per-condition unit tests, both polarities

Each of GR2060's ten and GR2070's twelve conditions gets a test that it **fires** and a test that it is
**silent** when that condition alone is flipped. A condition with only a firing test has not been shown
to be load-bearing; a condition with only a silence test has not been shown to exist.

### 8.2 Positive controls — recovered artifacts, not synthetic fixtures

Both are real, both verified present in git during this design.

- **GR2060:** `docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1` at
  `1b8e681`, against a tree whose SSOT is `tierSource`-free and the wave's task set as it then stood.
  Verified: **0 of that plan's task manifests name the SSOT in `writeScope`**. Assert: fires **exactly
  once**, naming `tierSource` and the SSOT path.
- **GR2070:** `docs/plans/30-telemetry-phase-1/tasks/16-…/guardrails/03-both-settle-records-set-every-phase1-member.ps1`
  at `10816fb`, with that commit's task manifests and that commit's `ISchedulerJournal.cs`. Verified:
  `bucket` appears as a parameter **nowhere under `src/`** at that commit; `RecordSettleWithAttempt` is
  declared in exactly 2 files; 1 is unowned. Assert: fires **exactly once**, naming `ISchedulerJournal.cs`.
  Assert also that the **same script against `09f223f`'s tree is silent** — the post-fix state — so the
  test proves the check tracks the tree rather than the string.

### 8.3 The negative control that would have caught this design's own bugs

Three ways this check can ship mute were found *while designing it*. Each gets a test that fails if it
regresses, and each test must be shown **red against the pre-implementation tree** and green after — a
green-on-both test proves nothing.

- **The `\b` anchor.** A fixture whose anchor is written `'\bM\s*\('` — the idiomatic form, and the form
  the measured instance uses — **must** be extracted. `(?<![A-Za-z0-9_])M\(` does not match it, because
  the character before `M` is the `b`. Without this test the check ships mute on almost every real anchor.
- **The interface declaration.** A fixture whose only declaration of `M` is an **interface member with no
  access modifier** (`void M(…);`) must be found. A declaration index anchored on
  `public|internal|private|…` misses it — and that is exactly the carrier class this check exists for.
- **The double-quoted clause (§3.5).** A fixture whose requirement clause is
  `if ($v -cnotmatch "p\s*:\s*$x") { $failures += … }` must be extracted. Reusing GR2057's
  single-quote-only `PresenceClause` gives GR2070 a corpus population of **zero**; this test is the pin
  that says so out loud, and its comment must name the number.

A fourth test guards the opposite direction: a fixture whose requirement clause is single-quoted and whose
pattern head is `PRECONDITION:` must be **silent** — the case-sensitivity pin, worth 294 spurious
candidates if it regresses.

### 8.4 The corpus sweep as a terminal gate, not a unit test

The sweep of §5.3, extended: both codes, over every plan folder in `docs/plans/` **and** `examples/`,
each evaluated **at its own pre-run commit** where one exists. Expected findings: **0** for GR2060 and
**0** for GR2070 on every plan except plan 30 at `10816fb`, where GR2070 must produce exactly 1. This
runs at the **terminal gate**, so an extractor that learned to fire on correct plans **withholds
delivery** rather than merging.

### 8.5 The anti-tautology pin

`ProducerCoverage` must never be tested only through fixtures it also generates. At least one test drives
the real `PlanValidator.Validate` over a real on-disk plan folder, through the same composition root
`PlanProbe.cs` uses — the #382 lesson, which is that fake-masked unit guardrails certify green while the
composition-root path is broken.

---

## 9. Done when

1. `guardrails validate` emits **GR2060 (ERROR)** on a plan whose gate requires an absent literal in a
   tracked file no task declares, and is silent on all fourteen committed plan folders.
2. `guardrails validate` emits **GR2070 (WARNING)** on plan 30's task-16 guardrail as it stood at
   `10816fb`, naming `ISchedulerJournal.cs`, and is silent on the same guardrail against `09f223f`.
3. Both positive controls (§8.2) pass, and both were **shown red** before the implementation landed.
4. The §8.4 sweep is a terminal gate and reports its counts.
5. `docs/plans/02-schemas-and-contracts.md` carries **§4.8** and **§4.9**, and §14.10's code paragraph
   advances next-free to **GR2071** — in the same commits as the code (invariant 4).
6. `plan-breakdown` carries the sibling-datum rule; `guardrails-domain-knowledge` names both codes.
7. `docs/plans/19-producer-coverage.md` carries a status line pointing here (§12.4).
8. Neither composition root (`PlanProbe.cs`, `Scheduler.cs`) changed signature.
9. Core + Integration suites green; no existing GR2057 test edited.

**#474 does not close on this plan.** It closes its *coverage* shapes. Its headline reachability shape
stays with the review probe, permanently, per doc 19 D2 and §6.6. The issue gets a comment saying exactly
that, and stays open only if the maintainer wants a durable home for the shape; §16 asks.

---

## 10. Invariants in play

**1 — deterministic guardrails over prompt-judges; judges never alone.** Both codes are the deterministic
half of a class whose larger half is permanently a review probe. The document's discipline is that it
**says which half is which** and does not inflate the lint to look productive: §3.5 records three
widenings that were tried and rejected with numbers.

**4 — the SSOT is the schema SSOT; a contract change lands in the SAME change.** Two new sections and one
code-paragraph edit, specified verbatim in §12, landing in the implementing commits. §5.2 records that
doc 19 specified §4.8 and never applied it — the exact failure this invariant exists to prevent, and the
reason §12's edits are pinned to specific tasks in §13 rather than left as an intention.

**5 — honest halts; nothing is marked done unverified.** All three measured instances **were** honest
halts or withheld deliveries. The system worked; it worked at the most expensive point available. This
design moves the verdict earlier and adds **no escape hatch and no waiver field** — there is no way to
suppress either code from inside a plan, by design.

**2 — the harness is the single writer of merged state.** Both codes skip every path under the plan
folder. `state/`, `logs/`, the journal and `diagram.md` are harness-written and appear in no `writeScope`
by construction; a coverage check that did not skip them would fire on every plan that reads its own state.

**6 — plain files, light setup.** One `git ls-files` per validate run, injected, silent when unavailable.
No index, no daemon, no cache, no new dependency. §3.7 and §6.4 are the reason that holds.

---

## 11. Running this plan unattended

`mergeOnSuccess` defaults ON, so a green run **delivers**. What the run must not be allowed to do:

1. **Raise either severity.** A task that "improves" the check by promoting GR2070 to ERROR can refuse
   every existing plan — including the **resume of the run doing it**. Severity is a maintainer decision
   backed by a sweep (§16). Enforced as a forbidden-token guardrail on `Error(DiagnosticCodes.Unproducible…`.
2. **Widen the extractor to make a test pass.** Dropping the named-argument qualifier (§3.2), the
   one-anchor rule (§6.2 c3), the 3-declaration bound (c6) or the case-sensitivity (c4) each converts the
   check into a measured wolf. Each is pinned by a **silence** test whose deletion is itself a finding.
3. **Weaken the sweep's expectation.** The expected count is 0 (and 1 for the one positive control). A
   run that re-baselines it to "≤ 3 findings" has inverted the gate. The sweep is a terminal gate (§8.4),
   so this fails loudly and withholds delivery.
4. **Touch the run path.** `RunCommand`, `Scheduler`, `TaskExecutor`, `IPromptRunner`, `IActionRunner`,
   `IProgressSink` are all out of scope. This is author-time only, and no `writeScope` in §13 names one.
5. **Change either composition root's signature.** `PlanProbe.cs:86` and `Scheduler.cs:2213` both call
   `new PlanValidator()`. The probe arrives as a fifth overload with a default.
6. **Self-consistency, which this plan must pass on its own terms.** Its SSOT clauses require content in
   `docs/plans/02-schemas-and-contracts.md`. Rows 5 and 8 of §13 own that file, so GR2060 is silent on
   this plan's own gate — as it must be. Any task added later that asserts SSOT content **must** be
   paired with a task owning the SSOT, or this plan trips the check it is building.
7. **The self-lock, stated precisely rather than dramatically.** Task 4 ships an ERROR-severity check
   into `Guardrails.Core`. The run in flight is executed by the **installed** CLI, so it does not pick
   the new code up mid-run — the lock is not immediate. But from the next `dotnet tool update` onward,
   **any resume of this plan re-validates it with the check it built** (`Scheduler.cs:2213` constructs a
   `PlanValidator` on every load). Task 4's own guardrails must therefore include a
   `guardrails validate` of **this plan's folder** with the newly built binary, asserting zero GR2060
   findings. A check that cannot validate the plan that built it has failed its first real test, and it
   is cheaper to learn that inside task 4 than at a resume three days later.

**Sized for cheap retries.** Every task is ≤ 3 files and one concern. Three tasks edit
`PlanValidator.cs`; they are strictly sequential by `dependsOn`, never parallel. Tests are authored
before the implementation they gate, so a retry re-runs one narrow filter rather than a suite.

---

## 12. Exact SSOT edits (`docs/plans/02-schemas-and-contracts.md`)

### 12.1 New §4.8 — immediately after §4.7 (which ends at line 1520) and before `## 5. Child-process contract`

Heading: `### 4.8 Guardrails that CANNOT PASS given what this plan BUILDS (validated, GR2060 — error)`.

Opening paragraph to state: the §4.7 three are decidable from **one script's own text**; this one is
**relational** — it reads the script, the union of every task's `writeScope`, and the workspace's current
bytes. Same consequence (red before the task runs, red forever, and `/guardrails-review` structurally
misses it because it hunts weakness while this guardrail is *strong*), different evidence base, hence a
sibling section rather than a fourth row in §4.7's table. Carry doc 19 §3.1's predicate and all ten
conservatism conditions verbatim, the `planIsClosed` suppression, and the cross-reference to §14.1/GR2062
as its soundness precondition. §4.7 gains one closing sentence pointing forward.

### 12.2 New §4.9 — immediately after §4.8

Heading: `### 4.9 Guardrails that require a SIGNATURE the plan may not widen (validated, GR2070 — warning)`.

Carry: §6.1's predicate; §6.2's twelve conditions; §6.3's message shape; the **universal quantification
over declaration sites** with §3.3's measurement as its justification (one sentence: the existential
reading is silent on the only instance on record); the **derived-path** relationship to §4.8 in one line
— *§4.8's path is the one the guardrail names; §4.9's is the one it implies* — and §6.5's severity
argument including the promotion bar. Add the boundary sentence: **this does not cover the reachability
shape**, with the §6.6 table reduced to two rows.

### 12.3 §14.10's GR-code paragraph

Advance next-free to **GR2071**. Record **GR2060** (`UnproducibleGateRequirement`, this plan, doc 19's
design) as no longer reserved-by-name but **shipped**, and **GR2070** (`UnproducibleCallArgument`). Leave
**GR2061** and **GR2054** reserved. Per that paragraph's own standing instruction, `DiagnosticCodes.cs`
wins — re-verify immediately before allocating, and beware `DiagnosticCodes.cs:565`, a **quoted
historical** marker naming GR2047; the live marker is at `:1026`.

### 12.4 Two edits outside the SSOT

- **`docs/plans/19-producer-coverage.md`** — its status table's `Milestone A — harness half (GR2060)`
  row changes from `NOT BUILT` to a pointer at this plan, and D2 gains one sentence: *"a later instance
  (#474, plan 30) proved to be shape (a) with a derived path; see `33-unproducible-requirements.md` §6.6.
  D2 is unchanged."* No other edit; the document is not rewritten.
- **`docs/plans/03-roadmap.md`** — no change. Neither code is a v2 bet; both are v1 author-time validation.

---

## 13. Implementation handoff

Nothing starts until the #106 draft-PR review of this document is addressed.

Each row is deliverable by **one** task. The `writeScope` column is the **verbatim, concrete** array to
emit in that task's `task.json` — no globs, matching the convention that every `writeScope` in every plan
folder in this repo is concrete.

| # | Agent | Deliverable | `filesTouched` | pinned `writeScope` (verbatim) | depends on |
|---|---|---|---|---|---|
| 1 | `guardrails-harness-developer` | **Refactor, no behaviour change.** Lift GR2057's `PresenceClause`, `BranchFailsTheGuardrail`, `BlankCommentLines`, `TryLiteralWitness` and `MatchesWitness` out of `PlanValidator` into a shared internal helper. **`PresenceClause` moves unchanged** — widening it to admit double quotes would change GR2057's behaviour, and GR2070 gets a sibling regex instead (§6.2 c4). Gate: every existing GR2057 test green and **unedited**. | `src/Guardrails.Core/Loading/GuardrailClauseText.cs`, `src/Guardrails.Core/Loading/PlanValidator.cs` | `["src/Guardrails.Core/Loading/GuardrailClauseText.cs", "src/Guardrails.Core/Loading/PlanValidator.cs"]` | — |
| 2 | `guardrails-harness-developer` | `IGitTrackedFileProbe` + `GitLsFilesProbe` + `NullGitTrackedFileProbe`, mirroring `IScriptSyntaxProbe` including its "silence is not proof" contract; a **fifth** `PlanValidator` constructor overload with a real default so neither composition root changes. | `src/Guardrails.Core/Loading/IGitTrackedFileProbe.cs`, `src/Guardrails.Core/Loading/GitLsFilesProbe.cs`, `src/Guardrails.Core/Loading/PlanValidator.cs` | `["src/Guardrails.Core/Loading/IGitTrackedFileProbe.cs", "src/Guardrails.Core/Loading/GitLsFilesProbe.cs", "src/Guardrails.Core/Loading/PlanValidator.cs"]` | 1 |
| 3 | `guardrails-test-author` | **Red** tests for GR2060: one firing + one silence test per §5.1 condition, plus the §8.2 recovered `model-tiering-stage-2` positive control. | `tests/Guardrails.Core.Tests/ProducerCoverageTests.cs` | `["tests/Guardrails.Core.Tests/ProducerCoverageTests.cs"]` | 2 |
| 4 | `guardrails-harness-developer` | **GR2060** in a new `ProducerCoverage.cs` (the `HandoffScopeCoverage.cs` precedent — one check family, one file, one line in `PlanValidator`), the code constant, and the call site. | `src/Guardrails.Core/Loading/ProducerCoverage.cs`, `src/Guardrails.Core/Loading/DiagnosticCodes.cs`, `src/Guardrails.Core/Loading/PlanValidator.cs` | `["src/Guardrails.Core/Loading/ProducerCoverage.cs", "src/Guardrails.Core/Loading/DiagnosticCodes.cs", "src/Guardrails.Core/Loading/PlanValidator.cs"]` | 3 |
| 5 | `guardrails-harness-developer` | **SSOT §4.8** (§12.1) and §4.7's forward-pointing sentence. | `docs/plans/02-schemas-and-contracts.md` | `["docs/plans/02-schemas-and-contracts.md"]` | 4 |
| 6 | `guardrails-test-author` | **Red** tests for GR2070: one firing + one silence test per §6.2 condition, the §8.2 recovered plan-30 positive control **and its post-fix silence twin**, and the two §8.3 negative controls (`\b` anchor; interface member with no access modifier). | `tests/Guardrails.Core.Tests/CallArgumentCoverageTests.cs` | `["tests/Guardrails.Core.Tests/CallArgumentCoverageTests.cs"]` | 5 |
| 7 | `guardrails-harness-developer` | **GR2070** as a second rule inside `ProducerCoverage.cs`, reusing task 2's tracked-file list; the **sibling double-quote-admitting clause regex** and the head-only rule (§6.2 c4); the return-type-anchored declaration index (§8.3); the code constant; next-free advanced to GR2071. | `src/Guardrails.Core/Loading/ProducerCoverage.cs`, `src/Guardrails.Core/Loading/DiagnosticCodes.cs` | `["src/Guardrails.Core/Loading/ProducerCoverage.cs", "src/Guardrails.Core/Loading/DiagnosticCodes.cs"]` | 6 |
| 8 | `guardrails-harness-developer` | **SSOT §4.9** (§12.2) and §14.10's code-paragraph edit (§12.3). | `docs/plans/02-schemas-and-contracts.md` | `["docs/plans/02-schemas-and-contracts.md"]` | 7 |
| 9 | `guardrails-test-author` | The §8.4 corpus sweep and the §8.5 anti-tautology test: both codes, every plan folder in `docs/plans/` and `examples/`, each at its own pre-run commit where one exists; expected 0 everywhere except plan 30 at `10816fb`. Wired as a **terminal-gate** guardrail, not only a unit test. | `tests/Guardrails.Core.Tests/ProducerCoverageCorpusTests.cs` | `["tests/Guardrails.Core.Tests/ProducerCoverageCorpusTests.cs"]` | 8 |
| 10 | `guardrails-skill-author` | The sibling-datum authoring rule in `plan-breakdown` (doc 19 §4 specified it; it never shipped), and one line in the knowledge skill naming both codes and the producer-coverage invariant. | `.claude/skills/plan-breakdown/SKILL.md`, `.claude/skills/guardrails-domain-knowledge/SKILL.md` | `[".claude/skills/plan-breakdown/SKILL.md", ".claude/skills/guardrails-domain-knowledge/SKILL.md"]` | 7 |
| 11 | `guardrails-harness-developer` | Doc 19's status-table row and its D2 sentence (§12.4). | `docs/plans/19-producer-coverage.md` | `["docs/plans/19-producer-coverage.md"]` | 8 |

**Sequencing.** 1 → 2 → 3 → 4 → 5 → 6 → 7 → {8, 10} → 9, with 11 after 8. Tasks 1, 2 and 4 all edit
`PlanValidator.cs` and are strictly serial; task 10 touches no C# and may run beside 8. Milestone
boundaries: **A** = 1–5, **B** = 6–9 + 11, **C** = 10.

### 13.1 Hand-run of GR2068 / GR2069 against this table

Run by hand against the pinned `writeScope` column above, because this plan has **no task folder yet** —
`HandoffScopeCoverage` runs over a loaded `PlanDefinition`, so it will be structurally silent until the
breakdown exists. The pinned column is what makes the hand-run possible, and it is the contract the
breakdown must emit.

| row | candidates extracted | first segment resolves? | covered by ONE task? | verdict |
|---|---|---|---|---|
| 1 | 2 | `src` ✓ | yes — identical to row 1's scope | **silent** |
| 2 | 3 | `src` ✓ | yes | **silent** |
| 3 | 1 | `tests` ✓ | yes | **silent** |
| 4 | 3 | `src` ✓ | yes | **silent** |
| 5 | 1 | `docs` ✓ | yes | **silent** |
| 6 | 1 | `tests` ✓ | yes | **silent** |
| 7 | 2 | `src` ✓ | yes | **silent** |
| 8 | 1 | `docs` ✓ | yes | **silent** |
| 9 | 1 | `tests` ✓ | yes | **silent** |
| 10 | 2 | `.claude` ✓ | yes | **silent** |
| 11 | 1 | `docs` ✓ | yes | **silent** |

**GR2068 × 0, GR2069 × 0.** Every path is repo-rooted, so segment resolution is trivial; every row's
`filesTouched` is exactly its own task's `writeScope`, so no row is split.

Two rows deserve a note rather than a silent pass:

- **Rows 5 and 8 both own `docs/plans/02-schemas-and-contracts.md`, and rows 4 and 7 both own
  `DiagnosticCodes.cs` and `ProducerCoverage.cs`.** GR2069 is a *per-row* verdict, so repeated ownership
  across rows is invisible to it and correct here — each row is one task's whole delivery. The real
  hazard is a **merge collision**, which the `dependsOn` chain 4→5→6→7→8 removes by serialising them.
- **A tempting simplification that must be refused:** merging rows 4+5 (or 7+8) into "the code and its
  SSOT section" would still be one task and still silent under both codes — but it would put a 400-line
  C# check and a schema section in one retry unit. §11's cheap-retry requirement wins.

---

## 14. Out of scope

- **A dataflow-reachability lint** — doc 19 D2, §6.6. Permanent. The review probe (`e118b9d`) is the answer.
- **Positional arity requirements** — §3.1 item 2. The largest uncovered shape, and there is no proposal
  for it: a comma-counting regex over a call's argument list carries no name to compare against a
  declaration.
- **`Type.Member` derivation** — §3.6(a). Rejected on measurement: 3 fires, 3 wrong, and the failure is
  intrinsic rather than a bug to fix.
- **"Token `T` nowhere in the task's scope"** — §3.6(b), doc 19 §2.2. The loudest wolf in the family.
- **`.sh` guardrails** — GR2057's precedent; ships when a `.sh`-only corpus exists.
- **Multi-hop variable association** — doc 19 §5. One hop covers both measured instances.
- **AST-based clause extraction** — welcome if an in-process parser ever becomes free; not required, and
  must not be the reason this slips.
- **A `filesTouched` handoff table on this plan being *generated* by the breakdown.** Plan 30's breakdown
  declined to author one and was right: a table written from the author's own `writeScope`s is green by
  construction. §13's table is **declared by a human in this document**, ahead of the breakdown, and the
  breakdown's job is to match it.
- **Promoting GR2070 to ERROR** — §16, maintainer's call, on evidence this plan cannot yet produce.

---

## 15. Risks accepted

1. **GR2070's population is 1 script in 443, and its clause form is 6 in 443 with 5 of those 6 in that
   same script.** The zero false-positive rate is therefore **structural, not empirical** — the check has
   had one opportunity to be wrong and took it correctly (§3.4). I considered and rejected the comfortable
   version of this risk (*"the form is new and growing, because a call-scoped argument-list clause is the
   strongest known shape against a discard mutant"*): the corpus does not support it. Two authors reached
   for a double-quoted pattern because they needed a variable in it, and that is all the data says.

   **Priced as a falsification trigger, not a hope.** If GR2070 has not fired on a real plan within six
   months while the review probe keeps catching carrier gaps, **the lint was unnecessary and the probe was
   the whole answer** — back it out and keep GR2060. Doc 19 §5 recorded the same possible outcome for
   GR2060 and was right to. Conversely, if the corpus reaches ~20 named-argument clauses and GR2070's
   false-positive count is still 0, that is when the promotion conversation in §16 D-b becomes worth
   having; before then, "0 findings" is a statement about the corpus and not about the check.
2. **The known false-positive shape is unmeasured, not absent** (§6.5): a declaring file that is a test
   double the widening does not require changing. No such declaration exists in today's corpus. First
   observed instance → narrow condition 8 to exclude declarations that are explicit interface
   implementations, or drop to declaring files under `src/` only, and re-run the sweep.
3. **Association by co-occurrence is untested above n=1** (§6.2 c3). The single-anchor restriction makes
   the untested case silent rather than wrong, which is the correct direction, but it means a script that
   greps two members gets no check at all.
4. **§5.3's zero is partly structural.** Today's tree satisfies the requirements these plans made, because
   the plans ran. §8.4's pre-run-commit sweep is the version that could fail, and it is the merge gate.
5. **GR2060 at ERROR can block a resume.** If it false-fires on a shipped plan, that plan cannot be
   resumed until the finding is fixed or the code is backed out. Accepted on doc 19 D4's reasoning and on
   the measured zero; the escape hatch is deliberately absent, because a suppressible producer-coverage
   check is one an author silences instead of fixing.
6. **This plan does not close #474.** It closes the coverage shapes on both altitudes. Saying so plainly
   is the point; the alternative is a closed issue and an open defect.

---

## 16. Decisions this plan leaves to the maintainer

| # | decision | this plan's recommendation |
|---|---|---|
| D-a | **Take all three milestones, or A + C only?** A alone closes the $115.32 instance and is coherent (§4.2). B's clause form occurs 6 times in 443 scripts, 5 of them in the one script that motivates it. | **All three**, but this is the closest call in the document and **declining B is defensible on the numbers**. §4.1 states both sides; the case for B rests on the cost of the failure, not its frequency. |
| D-b | **GR2070 at WARNING or ERROR?** | **WARNING**, with the promotion bar written into §4.9. §6.5 is the argument; the evidence base is one instance and a name-keyed extractor. |
| D-c | **Does #474 close?** | **No.** Comment on it naming what closed (both coverage shapes) and what did not (reachability, permanently review-only). Close only if you want the shape's durable home to be doc 19 §2.2 rather than an open issue. |
| D-d | **Does the sweep run at each plan's pre-run commit, or only against `HEAD`?** Pre-run is stronger and costs a `git ls-tree` per plan folder. | **Pre-run**, for the plans that have one. It is the only version of the sweep that can fail. |
| D-e | **Does §13's table get emitted into the breakdown, or stay declared here only?** | **Stay declared here.** §14's last bullet: a breakdown-authored table is green by construction, and this is the plan where that matters most. |

**Decided 2026-09-02, by the lead session under the standing autonomy mandate — not by the maintainer in
person.** D-a is the one to revisit first if he disagrees; nothing downstream of it has been built yet.

| # | decision | why |
|---|---|---|
| D-a | **All three milestones.** | The marginal cost of B once A exists is three tasks on machinery A already builds, and the milestones are sequential — if A's build changes the picture, B can still be dropped without stranding anything. The failure B prevents is the plan-30 `RecordSettleWithAttempt` instance: a false green that surfaced 26 tasks downstream attributed to the wrong task. Cost is not the constraint here; a check that certifies nothing is. |
| D-b | **WARNING**, as recommended. | Same reasoning that put GR2068/GR2069 at WARNING: a name-keyed extractor that was wrong three ways during its own design does not get to refuse a correct plan, and a false WARNING costs a line of reading. §4.9's promotion bar stands. |
| D-c | **#474 stays open**, with a comment naming what closed. | Both coverage shapes close mechanically; the reachability headline stays with the review probe by doc 19 D2. Closing it would file the surviving shape somewhere nobody looks. |
| D-d | **Pre-run commit**, as recommended. | It is the only version of the sweep that can fail, which is the whole of #580 — a check is not authored, it is proven to fire. |
| D-e | **Stays declared here.** | A breakdown-authored handoff table is green by construction. This plan's §13.1 hand-run is evidence precisely because the breakdown did not write it. |

---

## 17. Devil's-advocate self-critique

**The strongest counter: "You have spent a design on a check with a population of one, and made it look
substantial by annexing an unbuilt milestone from someone else's document."**

**Half-conceded, and the concession is §4.1's ranking.** The annexation is not a rhetorical move: doc 19's
Milestone A **is** the mechanical half of #474, it was specified in full, and it was never built. Had it
shipped in August this document would be three pages about one derivation rule. What I will not concede is
the alternative — writing plan 33 as GR2070 alone would mean building a git probe, a clause reader, a
witness extractor refactor, a `planIsClosed` gate and an SSOT section **for a shape that occurs once in
443 scripts**, while the check those parts were designed for sits unbuilt beside them. That is the worse
version of the same criticism. The mitigation is that A, B and C are separately approvable and the
document ranks them **A ≫ C > B** rather than presenting three equals.

**"GR2060 at ERROR can refuse the resume of the very run that shipped it."**

**Real, and §11 item 7 is the answer** — a `validate` of this plan's own folder, with the newly built
binary, inside task 4's guardrails. Precision matters here and the document should not over-dramatise: the
in-flight run uses the installed CLI, so the lock arrives at the next tool update, not at the next task.
But it does arrive, and the first plan it would refuse is this one.

**"Your soundness argument for reading double-quoted patterns is one paragraph in a place GR2057
deliberately refused to go."**

**Conceded as a risk, answered on scope.** GR2057 refuses double quotes because it must de-regex the
**whole** pattern to an exact witness, and a `$` there is ambiguous between an anchor and an
interpolation. GR2070 never reads past the first `$` or backtick — it needs only the parameter name, which
is always in the head. The enumeration is small and closed: `$var`, `$(…)`, `${…}` and a backtick-escaped
`` `$ `` all begin with a character that is the cut point, so every one of them terminates the head before
it can matter. It is one fixture to prove and §8.3 requires it. If a reviewer wants the relaxation
withdrawn, the honest consequence is §3.5's: GR2070 has a population of zero and should not ship.

**"Universal quantification will name test doubles, and you have no measurement for that."**

**Conceded in full.** It is why GR2070 is a WARNING and not an ERROR, it is §15 risk 2, and the first
observed instance narrows condition 8 rather than being argued away.

**"Three tasks editing `PlanValidator.cs` in worktree mode is the merge-collision shape #175 exists for."**

**Real, and handled by shape rather than by hope.** Tasks 1, 2 and 4 are chained by `dependsOn`, so they
serialise; and task 4 puts the check itself in a **new file** (`ProducerCoverage.cs`, on
`HandoffScopeCoverage.cs`'s precedent), so its edit to `PlanValidator.cs` is one call-site line.

**"Doc 19 already had a skill obligation that never shipped. Why will task 10 be different?"**

Because it is a task in a DAG with a dependency and a guardrail, not a bullet in a handoff section. That
is the whole difference between doc 19 §4's sibling-datum rule (specified, unshipped, still open here as
Milestone C) and everything this plan will actually run.
