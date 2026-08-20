# JIT wave-breakdown durability and wave-scoped attestation

**Design of record for issues #385, #402, #471, #472.** Status: **DRAFT — for inline review (#106).**
Implementation milestones do not start until this document has been reviewed as a draft PR.

Reserved codes at time of writing: `GR2059` (#459, shipping), `GR2060`/`GR2062` (`19-producer-coverage.md`),
`GR2061` (`18-integration-proof-proximity.md` §3.4, deferred). This design takes **`GR2063`**.

---

## 1. What's being asked

Four issues in the JIT wave-breakdown path that keep producing one another:

| issue | one-line |
|---|---|
| **#385** (high, bug) | the auto-breakdown truncates on a large wave, leaving an invalid partial that quarantines |
| **#402** (medium) | checkpointed authoring so a truncated breakdown leaves a valid **prefix** |
| **#471** (high, bug) | the quarantine's message lies, and the quarantine **de-attests a review it never touched** |
| **#472** (high, bug) | the per-wave review stamp flow **cannot execute** — no `guardrails.json` in a wave |

They are one failure chain. #385's truncation is what silently lost the next-wave stub that #477
(`19-producer-coverage.md`) is the downstream victim of. Patching #385's budget alone leaves #471's lying
quarantine and #472's unreachable stamp untouched, and all three bite the same operator in the same run.

**Ambiguity named and narrowed.** #471 says explicitly that "which is the bug is a design call". This
document makes that call (§5) and, separately, answers the attestation question it raises (§6/§7) —
because the two are not the same question, and answering only the first leaves the second live.

**Second ambiguity, resolved by measurement.** #385 asks for the budget to "scale with expected task
count". §3 shows that the signal it would scale on does not exist, and that the budget was never the
binding constraint in the first place. That reframes the whole issue and is why #402 moves into v1.

---

## 2. Placement

| part | placement |
|---|---|
| truncation-cause disclosure (`FailureKind` → halt detail) | **harness** — `WaveBreakdownInvoker`, `Scheduler` |
| pre-invocation inventory + scoped revert | **harness** — `Scheduler.QuarantinePartialWave` |
| incomplete-trailing-task sweep + prefix preservation | **harness** — `Scheduler` |
| breakdown **intent manifest** + resume invocation | **harness** + **skill** (`plan-breakdown` writes it) |
| `GR2063` (declared-vs-authored shortfall) | **harness** (`PlanValidator`) + **schema** (SSOT §14.11, new) |
| per-wave review marker + `plan-hash`/`mark-reviewed` wave resolution | **harness** + **CLI** + **schema** (SSOT §13, §14.1) + **skill** (`guardrails-review` §7) |
| oversized-wave detection / splitting (#385 option 3) | **out of scope** — the #111/#378 over-size family |
| raising the 30-minute timeout | **rejected** — §3.4, a non-fix dressed as a fix |
| `--wave <slug>` flag as a second spelling | **deferred** — §7.2, one spelling is enough |

Nothing here is a v2 bet. Every part is v1 or explicitly deferred with an evidence gate.

---

## 3. #385 — the honest verdict on budget scaling

### 3.1 What I measured

The #385 fix **already landed**: commit `39e4510` (2026-07-24, PR #395) raised
`WaveBreakdownInvoker`'s turn ceiling from a fixed 120 to `400 + 25 × brief-signal`, clamped at 1000.
The issue stayed open because the bug stayed alive. Three breakdown invocations survive on disk:

| run | date | wall clock | `type:result`? | assistant msgs | outcome |
|---|---|---|---|---|---|
| `autonomous-mode-impl` / `3370e2ee` | 2026-07-23 (**pre**-fix, cap 120) | 20:27 → 20:57 = **30:00** | **none** | 136 | truncated → quarantine |
| `autonomous-mode-impl` / `7b5b7df9` | 2026-07-23 (pre-fix) | 20:27 → 20:53 = 25:56 | success, `num_turns: 35` | 167 | completed; output still rejected¹ |
| `model-tiering-stage-2` / `250d3cdd` | 2026-08-17 (**post**-fix, cap ≥ 400) | 08:03 → 08:33 = **30:00** | **none** | 165 | truncated → quarantine |

¹ Contaminated: two concurrent `guardrails run` processes against the same plan folder. Discard as
truncation evidence — but keep it as proof that a **cleanly-completed** session can still produce
output the gate rejects (§4.4 depends on this).

Both truncations stopped at **exactly 30:00**, which is `WaveBreakdownInvoker.BreakdownTimeout =
TimeSpan.FromMinutes(30)`. The last event in the August stream is a *successful* tool result — task 12's
`task.json` written — with no terminal record after it. That is a kill, not an exhaustion.

And the completed run reports **`num_turns: 35`** for a 26-minute session with 167 assistant messages.
Whatever the runner counts as a turn, it counts ~35 for a session of this shape. **A cap of 120 was
never within reach, let alone 400.**

### 3.2 Verdict

**The #385 diagnosis was wrong, and its fix raised a ceiling on a constraint that was never binding.**
Turns were not the bound before the fix and are not the bound after it. Wall clock was, both times.

**Is there a reliable invocation-time signal to size the budget? No.** The only signal available is
`brief.md`'s work-item count, and the record says it under-declares by 3–5× — #385's own text notes a
3-bullet brief expanding to ~11 tasks. The brief states *intent*; the task count is a **result** the
breakdown discovers. Any function of the brief is a guess at a number the session itself produces. On
the failing run the scaling term added headroom to a ceiling nothing touched.

So: **do not scale the budget.** And do not raise the timeout either. 30 → 60 minutes buys exactly one
wave size, relocates the failure, and makes every future truncation cost twice as much before it fails.
That is a raised ceiling, not a fix, and this document will not dress it as one.

### 3.3 The consequence for #402

If no invocation-time bound can be sized correctly, then **any** bound will eventually be hit by a wave
large enough. The only structural answer is to make the work **restartable at a boundary**, so hitting a
bound costs one task rather than the wave.

**#402 is therefore not the "deeper fix". It is the only fix, and it moves into v1.** #385's remaining
options are re-classified: option 1 (raise/scale the budget) is closed as a non-fix, option 3 (detect and
split an oversized wave) goes to the #111/#378 family, option 4 (better diagnostics) is v1 and is the
first milestone because it is what makes every later claim measurable.

### 3.4 What stays: the timeout itself

The 30-minute timeout is **kept, unchanged**. It is the outer liveness bound and #469 already covers the
operator's blindness during it. What changes is that hitting it becomes **legible and recoverable**
instead of silent and total.

---

## 4. Design — #385 + #402 in v1

### 4.1 Stop discarding the reason (milestone 1, ~one field)

`PromptResult` already carries `FailureKind` — a shipped enum with `Timeout`, `MaxTurns`, `OutputCap`,
`Transient`, `Error` (SSOT §9, issues #114/#115/#119). `WaveBreakdownOutcome` keeps only
`ProcessCompleted` and `Summary` and **throws the classification away**, which is why the halt says
"The breakdown invocation did not complete cleanly" and the operator is left guessing between skill bug
and budget — the exact complaint in #385 option 4.

- `WaveBreakdownOutcome` gains `FailureKind` and `NumTurns`.
- The `BreakdownFailed`/`BreakdownIncomplete` detail names the cause in the operator's words:
  *"the breakdown session was CUT OFF by the 30-minute timeout after authoring 11 of 14 declared tasks"*
  vs *"the breakdown ran out of turns (cap N)"* — two different remedies, and only the second is a budget.

This is a one-field change that closes the evidence gap §3.1 had to reconstruct from file mtimes, and it
is the thing that will tell us within two large waves whether §3.2's verdict holds.

### 4.2 A cut-off session can never be reported complete (milestone 2, the safety floor)

**Deterministic rule, no manifest required:** if the invocation did not terminate cleanly
(`FailureKind ∈ {Timeout, MaxTurns, OutputCap}`, or no terminal result was produced), the wave is
**never** `BreakdownComplete`, regardless of what `guardrails validate` says about it.

This matters because §4.3 makes valid prefixes possible, and a valid prefix that reads as a finished wave
is strictly worse than today's loud quarantine — it would send a human to review 11 tasks with no signal
that 3 are missing. That is the #477 shape one level down, and honest halts (invariant 5) forbid it.

### 4.3 Salvage the prefix (milestone 3)

Today's truncation leaves 11 complete task folders plus one folder holding `task.json` and no action file
→ `GR1004` → the whole wave is quarantined. 79% of the work is discarded because of one missing file.

Three harness-side mechanics, in order, at the checkpoint after the invocation returns:

1. **Pre-invocation inventory.** Immediately before invoking, the harness walks the wave folder and
   records `path → (size, sha256)` to `logs/<runId>/<wave>/breakdown/pre-invocation.json`. The harness is
   the single writer of merged state (invariant 2) and owns the invocation boundary, so this is exact,
   not a heuristic. It is also the forensic artifact the #471 investigation had to reconstruct by hand.
2. **Sweep incomplete trailing task folders.** Move to `rejected/` any task folder that (a) the inventory
   shows the attempt **created**, and (b) fails the loader's own completeness predicate (`task.json`
   present *and* a resolved action present). All three conditions must hold. Nothing is deleted.
3. **Re-validate the swept wave.** Then classify on **diagnostic codes**, never on the judge's opinion
   (invariant 1):
   - any error other than `GR2063` → **invalid** → quarantine (§5) → `BreakdownFailed`, as today;
   - only `GR2063` and/or warnings, or a clean validate after a cut-off session → **valid but
     incomplete** → **preserve** → `BreakdownIncomplete`;
   - clean validate after a clean session → `BreakdownComplete`, as today.

### 4.4 Declare the intent (milestone 4 — the skill's only change)

The prefix's *debt* is not computable from the prefix. The measured recovery proves it: a human reading
the same artifacts concluded 13 tasks, and the real number was 14 — the missed task was the SSOT
schema-delta task, which would have failed the terminal gate after every other task ran green (#474).

So the breakdown declares its decomposition **before authoring bodies**. `plan-breakdown` (Step 9, waved)
writes, as its first act:

```jsonc
// <wave>/state/breakdown-intent.json  — harness-owned runtime area, NOT in any definition hash
{
  "version": 1,
  "declaredAt": "2026-08-20T05:00:00Z",
  "tasks": [
    { "folder": "01-author-tests-journal-tiering-schema", "purpose": "…" },
    { "folder": "02-implement-journal-tiering-schema",    "purpose": "…" }
    // … all 14
  ]
}
```

Placement is deliberate: `<wave>/state/` is already in the §14.1 layout, is already excluded from every
definition hash, and is already what `--fresh` clears — so a mid-breakdown wave that is reset starts over,
which is right. The file is **removed on successful completion**; its lifetime is one breakdown attempt.

The alternative — reconstruct the debt from forward references in the already-authored gates, as #402's
comment suggests — is **rejected**. It is precisely the fuzzy-text inference GR2055/GR2057 spent their
whole conservatism budget avoiding, and the false-positive surface is large in this very repo (a guardrail
grepping `docs/plans/02-schemas-and-contracts.md` contains a folder-shaped token). A declared list is
decidable; prose is not.

### 4.5 Resume instead of restart (milestone 5)

On `BreakdownIncomplete`, the wave is **not** quarantined. The checkpoint re-fires on the next run and the
invoker composes a **resume** prompt: the intent manifest, the folders already complete, the folders still
owed, and an instruction to author only the remainder. The 232 KB brief is not re-paid for work already
done.

Bounded, per the honest-halt invariant:
- at most **3** breakdown segments per wave per run;
- a segment that adds **zero** complete task folders is a halt, not a retry — no no-progress loop;
- every segment's spend still lands in `overheadCostUsd` and counts against `maxCostUsd` (unchanged).

Without a manifest (a session cut off before writing it), resume degrades to today's behaviour: the prefix
is preserved if it validates, and §4.2 keeps it from reading as complete.

### 4.6 GR2063 — `WaveBreakdownIncomplete` (WARNING)

**Fires when:** a wave carries `state/breakdown-intent.json` and a declared `folder` has no corresponding
complete task folder under that wave's `tasks/`. Names the missing folders.

**Silent when:** the manifest is absent, unparseable, or satisfied. Absent ⇒ skipped entirely — the same
rule `GR2062` uses for `intendedWaves`, and the same "silence is not proof of validity" discipline
`GR2056` set.

**Severity is WARNING, and the split is the point.** The harness routes on the **code** (`GR2063` present
⇒ incomplete ⇒ never `BreakdownComplete`), so the automated path — where the risk actually lives — gets
full enforcement. The human path gets a nudge, because a human who deliberately finishes a wave with 11 of
14 declared tasks has done nothing wrong; they have merely not updated the manifest. Failing their
`validate` for that is exactly the wolf-cry §4.7 warns about, and GR2025 is the shipped precedent for
warn-at-validate / load-bearing-where-the-harness-reads-it.

**Can a correct implementation be written that this rejects?** Only a wave whose manifest over-declares.
The remedy is named in the message and is to correct or delete the manifest — i.e. to record the intent
that actually holds. Because the manifest is removed on success, **no committed plan folder in the corpus
can trigger it**, so the false-positive rate is zero by construction rather than by measurement. That is a
weaker claim than GR2055/2056/2057's measured zero and this document states it as weaker: the conservatism
here is *structural* (a set-compare against a declared list), not *empirical*.

---

## 5. #471 — the design call

### 5.1 The call: (a), scoped by the inventory

Neither (a) "move everything" nor (b) "keep the gates and say so" as written. Both are guesses about
provenance. The inventory of §4.3 makes provenance **known**, so:

> **Quarantine reverts exactly what the breakdown attempt wrote, and nothing it did not.**
> Files the pre-invocation inventory shows as created or modified by the attempt move to `rejected/`,
> preserving their relative paths. Pre-existing files are left byte-identical. Only the empty `tasks/`
> stub is restored.

This preserves (a)'s principle — nothing from a truncated session is trustworthy, and the gates came from
the same session as the tasks, so keeping half of it is the option with no principle behind it — while
answering the hazard that neither the issue nor a first pass caught:

**A human may hand-author a wave's exit gate before the breakdown runs.** "Write the gate that defines the
wave's postconditions, then let the breakdown author the tasks that satisfy it" is a *good* pattern, and
it is what a `brief.md`-plus-gates JIT stub looks like. Blind option (a) would move that human's work into
`rejected/` and report it as reverted. That is data loss of human work, and it is the strongest argument
against the issue's preferred reading.

### 5.2 Why against the re-run path

Option (b) — keep the gates — fails on what the **next** attempt encounters, and harder than the issue
states:

1. The next attempt either overwrites the retained gates (so keeping them bought nothing) or, seeing them
   present, skips authoring them — inheriting gates written against a decomposition that no longer exists.
   The measured artifact makes this concrete: the abandoned wave gates named tasks **13 and 14 by number
   and by slug**, and task 04's guardrail named task 14 twice. A retained gate that references tasks the
   new attempt never creates is **a guardrail that cannot pass, arriving by inheritance** — the
   GR2055/GR2057 defect class, produced mechanically by our own recovery path.
2. Quarantine stops being idempotent: attempt 3 sees attempt 2's gates layered over attempt 1's with no
   record of which is which.
3. It contradicts the reason to quarantine at all.

### 5.3 The message

The current text — *"The partial output was quarantined (the wave reverted to its empty stub)"* — becomes
true by construction, and states what moved:

```
The breakdown session was CUT OFF by the 30-minute timeout after authoring 11 of 14 declared tasks.
Everything this attempt wrote was reverted; nothing that pre-dated it was touched:
  moved to …/breakdown/rejected/ : tasks/ (12 folders), guardrails/ (3 files + sidecars),
                                   preflights/ (1 file + sidecar)
  left in place (pre-existing)   : guardrails/00-hand-authored-exit.ps1
The wave folder is byte-identical to its pre-breakdown state; PlanDefinitionHash is unchanged.
```

Rendering is a **#469/`guardrails-ux`** concern; the contract above is what the harness must supply.

### 5.4 The provable property

From `PlanDefinitionHash.Compute`, a wave contributes exactly (i) its tasks' file sets, via the flattened
`plan.Tasks`, and (ii) `AppendFolder(wave.Directory, "guardrails")` and `("preflights")`. An empty `tasks/`
and no attempt-written gate files contribute nothing. So the revert restores the **byte-identical** hash.

That is not a claim, it is the regression test: **assert `PlanDefinitionHash` before invocation ==
`PlanDefinitionHash` after quarantine.**

---

## 6. The attestation question — and it is a separate question

Fixing §5 removes **one** way a plan-level marker gets spent. It does not answer whether
`PlanDefinitionHash` should move for a quarantine, and it leaves the other ways alive. So, separately:

> **The plan-level marker should not be what a wave-scoped write touches at all.**

And the decisive finding is that this is **already the specified design and it was never built.**

SSOT §13, *Multi-wave plans*, in the committed document today:

> **Multi-wave plans (§14):** the review marker + its `PlanDefinitionHash` are **per-wave** — each wave
> subfolder carries its own `<plan>/<wave>/state/guardrails-review.json` … GR2025 is surfaced **JIT per
> wave** — checked before that wave runs — **so an already-reviewed + run upstream wave never re-stales
> when a downstream wave is authored later.**

§14.1's layout diagram carries the file: `state/guardrails-review.json  # OPTIONAL, per-wave review marker (§13)`.

None of it exists in code. `ReviewMarker.PathFor(string planDirectory)` is plan-root only;
`Evaluate(PlanDefinition)` computes one plan-wide hash; `PlanValidator` emits one `GR2025`;
`mark-reviewed` and `plan-hash` have no wave concept anywhere.

**So #471's second consequence and #472 are the same defect** — §13's per-wave paragraph is written and
unimplemented — and the harm it predicts *verbatim* is the harm #471 measured.

---

## 7. Blast radius — is #471 a bug or a class?

I checked. **It is a class, it has exactly two members, and they are the same actor — and the member
nobody has filed is the more frequent one.**

### 7.1 The enumeration

The hashed surface, verified against `PlanDefinitionHash.Compute`: `guardrails.json`; per task
`task.json` + resolved action + `guardrails/**` + `preflights/**`; `<plan>/guardrails/**`;
`<plan>/preflights/**`; per wave `<wave>/guardrails/**` + `<wave>/preflights/**`. Excluded: `state/`,
`logs/`, `captured/`, `diagram.*`, `guardrails.baseline`, and the wave's `brief.md`.

| harness-initiated writer | target | moves the hash? |
|---|---|---|
| guardrail/preflight **verdict files** (`GuardrailRunner`) | `logs/<runId>/…/*.verdict.json` | **no** — §9.5 staging promotes into `logs/`, never the plan folder |
| gate captures (`GateArtifacts`) | `logs/<runId>/<gate>/…` | no |
| journal, state, decisions, triage, answers, rewind | `state/**` | no |
| `guardrails.baseline` (`BreakdownManifest`) | plan root | no — explicitly excluded |
| `diagram.md` / `diagram.html` | plan root, `logs/` | no |
| `PlanGitignore` | `<plan>/.gitignore` | no |
| guardrail-script syntax probe (GR2056) | OS temp dir | no |
| **overwatcher fix ops** | any | **blocked by design** — see §7.2 |
| `needsHarnessWrite` (#191/#437) | workspace-relative, `writeScope`-bounded | only where a task legitimately declares the plan folder in scope — task **output**, not a harness side effect |
| **JIT wave breakdown — FAILURE** | leaves `<wave>/guardrails/**`, `<wave>/preflights/**` behind | **YES** — #471 |
| **JIT wave breakdown — SUCCESS** | writes `<wave>/tasks/**`, `<wave>/guardrails/**`, `<wave>/preflights/**` | **YES — and unfiled** |

### 7.2 The corroborating evidence, and the asymmetry

`OverwatchFixClassifier` denylists — at every tier including `auto` — every path inside the four
guardrail/preflight folder families and the verdict-driving `task.json` fields. Its own doc comment states
the reason:

> A guardrail-body change is denylist precisely because applying it changes `PlanDefinitionHash`, which
> self-invalidates `state/guardrails-review.json` (§13).

**The harness already knows that writing this surface spends the review marker, and treats it as
denylist-grade for the overwatcher.** The breakdown invoker writes the same surface with no such gate.
Naming that asymmetry is the finding: the protection was built for the actor that was reasoned about, and
not for the actor that was added later.

### 7.3 The unfiled member is the worse one

Every **successful** JIT breakdown of wave N+1 writes into the hashed surface, moves `PlanDefinitionHash`,
and therefore stales the marker attesting wave N — which was reviewed, stamped, run, and green. #471 is
the failure-path instance. The success path does the same thing on every JIT wave of every waved plan,
silently, and it looks like correct behaviour ("the plan changed"), which is exactly how the noise
normalises. That is the mechanism by which a real staleness warning gets waved through later.

### 7.4 Therefore the fix belongs at the attestation

Both members disappear the instant the attestation's scope matches the write's scope: a wave-scoped write
moves only that wave's hash, and touches only that wave's marker. **#471(2), #472, and the unfiled
success-path instance are one fix — build §13's per-wave marker.** §5 is still required (the message must
not lie, and human work must not be moved), but it is no longer load-bearing for the attestation.

---

## 8. #472 — the fix

### 8.1 Root cause, verified

`plan-hash` and `mark-reviewed` both route through `PlanProbe.LoadAndValidate(folder)`, which requires
`<folder>/guardrails.json` → `GR1001`. A wave folder has none **by design** (§14.1: "ONE shared run config
— no per-wave config in v1"). This is structural, not a bug in either verb.

### 8.2 Resolution — the issue's option 1, mechanics of option 2, one spelling

- **Resolve a wave through its plan**, not "make a wave loadable". Walk up from the target directory to
  the nearest ancestor containing `guardrails.json`; require the target to be an immediate child matching
  the wave regex; load the plan; select the `WaveNode`. One loader, one `guardrails.json`, no second
  notion of a plan.
- Path-shape inference is acceptable here **only** because `^wave-([0-9]+)-[a-z0-9-]+$` is already a
  load-bearing regex (§14.1) that detection itself uses. No new inference surface is created.
- **One spelling.** The `guardrails-review` skill's §7 already prescribes the path form
  (`guardrails plan-hash <folder>/wave-NN-<slug>`), so the skill text needs no change to the commands
  themselves. A `--wave <slug>` flag is **deferred** — two spellings of one resolution is KISS debt with
  no demand behind it.

### 8.3 The hash: `WaveDefinitionHash`, and no fourth hash

`WaveDefinitionHash` is **already shipped**. It folds each constituent task's `TaskDefinitionHash`, the
wave's `guardrails/**` and `preflights/**`, and the wave's `brief.md`; it deliberately **excludes**
`guardrails.json` for exactly the reason this design needs — Open Decision C, *"a config edit must not
re-stale every already-run upstream wave."* `Scheduler.EscalateReviewGate` already uses it as the
`DefinitionHash` of a **review-gate** escalation, so the precedent that this is the right key for a
wave-scoped review concern is already in the code.

SSOT §13's phrase *"keyed on that wave's own `PlanDefinitionHash`"* is loose, and implemented literally it
would create a **fourth** member of the hash family. It must be corrected to name `WaveDefinitionHash`.

**Accepted residual.** `WaveDefinitionHash` folds `brief.md`, which `PlanDefinitionHash` deliberately
excludes as breakdown *input*. So editing a wave's brief after review re-stales that wave's marker. I
accept it: it is a **human** edit to a file inside the wave — the distinction that matters, since the
whole complaint in #471 is about staling from a machine side effect — it errs toward under-attestation,
and it costs far less than the drift risk of a fourth hash differing from a shipped one by a single file.
**Flip condition:** if brief edits on reviewed waves become a routine source of GR2025 noise, split a
`WaveReviewHash` that omits the brief, and pin both against each other in one test.

### 8.4 Surfaces

- Marker: `<plan>/<wave>/state/guardrails-review.json`. Reports: `<plan>/<wave>/state/reviews/`, and the
  #366 F2b path-containment check resolves against the **wave's** `state/reviews/`.
- `GR2025` is emitted **per wave** on a waved plan, and the `run` nudge is evaluated **JIT** — before that
  wave runs — exactly as §13 already promises.
- **Back-compat, and it keeps the corpus quiet.** If a wave has no wave marker but the plan-level marker
  exists **and is fresh** (its recorded hash equals the current `PlanDefinitionHash`), the wave reads
  *reviewed*. This is honest rather than lenient: a fresh plan-level marker can only be fresh if nothing
  in the plan has changed since it was stamped, which is precisely when it is entitled to vouch. The
  moment any wave is authored or edited, the plan marker goes stale and every wave falls through to its
  own marker (missing ⇒ nudge). Today's waved plans do not all light up.
- `validate <waveFolder>` **stays an error** — a wave is not independently loadable and silently
  validating something other than what was asked would be worse. But the bare `GR1001` is replaced with a
  targeted diagnostic naming the parent plan root and the correct invocation. Minimal, honest, and it
  removes the dead end the issue documents.

---

## 9. Seams and contracts touched

| seam | change |
|---|---|
| `IPromptRunner` / `PromptResult` | **none** — `FailureKind` already exists and is already populated |
| `WaveBreakdownOutcome` | `+ FailureKind`, `+ NumTurns` |
| `WaveBreakdownInvoker` | pre-invocation inventory; resume-prompt composition |
| `Scheduler` (`RunBreakdownAsync`, `QuarantinePartialWave`) | classify on diagnostic codes; sweep; inventory-scoped revert; bounded resume |
| `WaveHaltKind` | `+ BreakdownIncomplete` |
| `PlanValidator` | `+ GR2063`; per-wave `GR2025` |
| `ReviewMarker` | `PathFor(plan, wave?)`, `Evaluate(plan, wave?)` keyed on `WaveDefinitionHash` |
| `PlanProbe` / `PlanHashCommand` / `MarkReviewedCommand` | wave-target resolution through the parent plan |
| `plan-breakdown` skill | write `state/breakdown-intent.json` first; honour a resume prompt |
| `guardrails-review` skill | §7 waved-plan paragraph — the three commands now work; report path is wave-scoped |
| `IProgressSink` / `IActionRunner` | **untouched** |

---

## 10. Schema changes — exact `02-schemas-and-contracts.md` edits

> **NOT APPLIED.** Another agent is mid-edit in the SSOT. These are the verbatim deltas to land in the
> same change as the code that motivates them (invariant 4). **Coordination note:** `19-producer-coverage.md`
> claims a new **§4.8**; this design claims a new **§14.11** and does not contend for §4.8.

**E1 — §9.2, replace the "Turn budget (issue #385)" sentence** (currently at ~line 3807):

> **Session bounds (issues #385/#402).** Authoring a whole wave is a long session bounded by turns
> (`--max-turns`) and wall clock (a 30-minute timeout). **Neither bound can be sized from the invocation.**
> The only signal available is `brief.md`'s work-item count, which under-declares the eventual task count
> by 3–5×; the task count is a *result* of the breakdown, not an input to it. Two measured truncations
> (2026-07-23 pre-fix, 2026-08-17 post-fix) both stopped at exactly the 30-minute timeout, and a
> cleanly-completed session of the same shape reported `num_turns: 35` — the turn cap was never the binding
> constraint, before or after it was raised. The turn budget therefore remains a generous internal ceiling,
> **not a fix**: durability comes from §14.11 (declared intent, prefix preservation, and bounded resume),
> and the runner's `FailureKind` is carried into the halt so the operator is told which bound was hit.

**E2 — §13, replace the *Multi-wave plans* paragraph:**

> **Multi-wave plans (§14).** The review marker is **per wave**: `<plan>/<wave>/state/guardrails-review.json`,
> keyed on that wave's **`WaveDefinitionHash`** (§14.5) — *not* `PlanDefinitionHash`, and not a fourth hash.
> `WaveDefinitionHash` already excludes the shared `guardrails.json` (Open Decision C) so a config edit does
> not re-stale every upstream wave, and it folds the wave's `brief.md`; a brief edit after review therefore
> re-stales that wave's marker, an accepted residual (it is a human edit inside the wave, and it errs toward
> under-attestation). GR2025 is surfaced **per wave** at `validate` and **JIT** at `run` — evaluated before
> that wave runs — so an already-reviewed and run upstream wave never re-stales when a downstream wave is
> authored later. **Back-compat:** a wave with no wave marker reads *reviewed* iff a plan-level marker exists
> **and is fresh** (its hash equals the current `PlanDefinitionHash`); a stale plan-level marker vouches for
> nothing and every wave falls through to its own marker. The #366 `attestation` block is per wave exactly as
> the marker is, its report lives under `<plan>/<wave>/state/reviews/`, and the F2b containment check resolves
> against that wave's `state/reviews/`.
> **CLI:** `guardrails plan-hash <plan>/wave-NN-<slug>` and `guardrails mark-reviewed <plan>/wave-NN-<slug>`
> resolve the wave **through its parent plan** — walking up to the nearest ancestor holding `guardrails.json`
> and selecting the wave by the §14.1 folder regex — and emit/stamp the wave hash. `guardrails validate` on a
> wave folder remains an **error** (a wave is not independently loadable) but names the parent plan root and
> the correct invocation instead of a bare `GR1001` (#472).

**E3 — §14.4 step 2, replace the two clauses describing the gate outcome:**

> gated by the **deterministic** re-run of `guardrails validate` in-process (invariant 1, never the judge
> that produced it), classified on **diagnostic codes**: **clean validate after a cleanly-terminated session
> → `BreakdownComplete`** (halt for the human review gate); **any error other than `GR2063` → `BreakdownFailed`**,
> which quarantines to `logs/<runId>/<wave-dir>/breakdown/rejected/` and reverts the wave (§14.11);
> **otherwise → `BreakdownIncomplete`** — a valid but short prefix, which is **preserved, not quarantined**,
> and resumed (§14.11). **A session that did not terminate cleanly (`FailureKind ∈ {Timeout, MaxTurns,
> OutputCap}`, or no terminal result) can NEVER be reported `BreakdownComplete`, whatever `validate` says**
> — a valid prefix that reads as a finished wave is worse than a loud quarantine (invariant 5).

**E4 — new §14.11, *Breakdown durability — declared intent, prefix preservation, resume* (issues #385/#402/#471):**

> **Pre-invocation inventory.** Before invoking the breakdown the harness records the wave folder's
> `path → (size, sha256)` to `logs/<runId>/<wave>/breakdown/pre-invocation.json`. It is the harness's own
> record of what pre-dated the attempt (invariant 2), and it makes the revert exact rather than heuristic.
>
> **Intent manifest.** `plan-breakdown`'s first act on a waved invocation is to write
> `<wave>/state/breakdown-intent.json` — `{ version, declaredAt, tasks: [{ folder, purpose }] }` — the
> ordered decomposition it intends to author. It lives under the hash-excluded `state/` tree, is cleared by
> `--fresh`, and is **removed on successful completion**: its lifetime is one attempt.
>
> **Sweep.** After the invocation the harness moves to `rejected/` any task folder that the inventory shows
> the attempt **created** *and* that fails the loader's completeness predicate (`task.json` present and a
> resolved action present). All conditions must hold; nothing is deleted.
>
> **`GR2063` `WaveBreakdownIncomplete` (WARNING).** A declared `folder` in the manifest has no complete task
> folder under the wave's `tasks/`; the message names the missing folders. **Absent or unparseable manifest ⇒
> skipped entirely** (the `GR2062` rule). Severity is a warning so a human hand-finishing a wave is nudged,
> not blocked; the **harness routes on the code**, so the automated path is fully gated. Remedy: correct or
> delete the manifest.
>
> **Quarantine scope (#471).** A quarantine moves **exactly what the attempt wrote** — every path the
> inventory shows created or modified, `tasks/`, `guardrails/`, and `preflights/` alike — preserving relative
> paths under `rejected/`, and leaves pre-existing files byte-identical. Only the empty `tasks/` stub is
> restored. A human's hand-authored wave gate written **before** the breakdown is therefore never moved. The
> halt message states what moved and what was kept. **Invariant: `PlanDefinitionHash` after a quarantine
> equals its value before the invocation** — a quarantine never spends a review attestation.
>
> **Resume.** On `BreakdownIncomplete` the wave is preserved and the checkpoint re-fires; the invoker composes
> a **resume** prompt naming the manifest, the complete folders, and the folders still owed. Bounded: at most
> **3** segments per wave per run, and a segment adding **zero** complete task folders halts rather than
> retries. Spend accrues to `overheadCostUsd` unchanged.

**E5 — §14.1 layout diagram**, annotate the existing wave `state/` line:

> `├── state/guardrails-review.json  #  per-wave review marker (§13), keyed on WaveDefinitionHash`
> `├── state/breakdown-intent.json   #  TRANSIENT, one breakdown attempt (§14.11) — hash-excluded`

---

## 11. Devil's-advocate self-critique

**C1 — "You promote a medium-priority enhancement into v1 on two data points."**
Two instances, one of them *after* the fix that was supposed to prevent it, plus a mechanism that
generalises: no invocation-time signal predicts the required session size. But the honest concession is
that I have **not proven** the cut was the harness timeout rather than context exhaustion or an upstream
kill — two runs ending at exactly 30:00 is strong but circumstantial, and the harness does not record why
the runner stopped. That is why §4.1 is **milestone 1** and not an afterthought: the runner already
computes `FailureKind` and the invoker discards it. Within two large waves we will know, from the halt
text, whether §3.2 holds. If those halts say `MaxTurns`, §3.2 is wrong and the budget lever returns.

**C2 — "Per-wave markers are far bigger than the bug warrants; this is scope expansion."**
It is not an expansion, it is **shipping written spec** — §13's waved-plan paragraph and §14.1's layout
line are in the committed SSOT today. The counter has real force on cost (it touches `PlanValidator`,
both CLI verbs, `ReviewMarker`, and the review skill), and the mitigation is the §8.4 fallback: a fresh
plan-level marker still vouches, so the change is strictly additive and no existing plan lights up. The
argument for doing it **now** is §7.3: every waved plan run between now and later accumulates a
de-attested history, and each one teaches the operator to ignore GR2025.

**C3 — "GR2063's zero false-positive rate is measured on a corpus that cannot contain the trigger."**
Correct, and §4.6 says so rather than borrowing GR2055/2056/2057's empirical zero. The claim here is
structural: a set-compare against a list the author declared. The residual risk is real — "delete the
file to silence the lint" is a smell — and it is bounded by making the severity a warning and the
manifest's lifetime one attempt.

**C4 — "Blind option (a) destroys hand-authored wave gates."** This is the counter that changed the
design; see §5.1. Neither #471 nor a first pass caught it, and it is why the answer is inventory-scoped
rather than folder-list-based.

**C5 — "Two resolution spellings for a wave target is KISS debt."** Accepted; `--wave` is cut (§8.2).

**C6 — "The resume prompt is a judge deciding it is finished."** No. The judge authors; the **harness**
decides, from `validate` diagnostic codes plus the runner's termination classification, whether the wave
is complete, incomplete, or invalid. Invariant 1 holds — the breakdown's own opinion of its completeness
is never read.

**C7 — "The strongest counter overall."** *Do §5 and §8 only. Fix the lying message, ship per-wave markers,
and leave truncation to be re-run manually — three high-priority bugs closed for a third of the work, and
#402 stays a medium.* The response is §3.2 plus one number: the re-run is not cheap. The same 232 KB brief
would very likely truncate in the same place, for another ~30 minutes and another breakdown's spend, and
the manual recovery it forces got the task count **wrong** (13 of an actual 14), shipping a plan that
would have failed the terminal gate after every task ran green. Manual re-authoring is not a fallback;
it is the failure mode #474 came from. That said, the milestone order in §12 is deliberately chosen so
that this counter can still win at any point: milestones 1, 6 and 7 are independently valuable, and
stopping after them leaves a strictly better system than today.

---

## 12. Implementation handoff

| # | milestone | agent | filesTouched |
|---|---|---|---|
| 1 | Carry the truncation cause: `WaveBreakdownOutcome.FailureKind`/`NumTurns`; name the bound in the halt detail | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/WaveBreakdownInvoker.cs`, `Scheduler.cs` |
| 2 | Cut-off session ⇒ never `BreakdownComplete`; `WaveHaltKind.BreakdownIncomplete`; classify on diagnostic codes | `guardrails-harness-developer` | `Scheduler.cs`, `RunReport.cs` |
| 3 | Pre-invocation inventory; incomplete-trailing-folder sweep; **inventory-scoped** quarantine + true message (**#471**) | `guardrails-harness-developer` | `WaveBreakdownInvoker.cs`, `Scheduler.cs` |
| 4 | `GR2063` + intent-manifest reader | `guardrails-harness-developer` | `Loading/PlanValidator.cs`, `Loading/DiagnosticCodes.cs`, `02-schemas-and-contracts.md` (E4) |
| 5 | Intent manifest + resume prompt; bounded segments with a progress requirement | `guardrails-skill-author` (manifest), then `guardrails-harness-developer` (resume) | `.claude/skills/plan-breakdown/SKILL.md`, `WaveBreakdownInvoker.cs`, `Scheduler.cs` |
| 6 | Per-wave `ReviewMarker` on `WaveDefinitionHash`; per-wave/JIT `GR2025`; fresh-plan-marker fallback (**#471(2)**, **#472**) | `guardrails-harness-developer` | `Review/ReviewMarker.cs`, `Loading/PlanValidator.cs`, `02-schemas-and-contracts.md` (E2) |
| 7 | Wave-target resolution for `plan-hash`/`mark-reviewed`; targeted `validate`-on-a-wave diagnostic (**#472**) | `guardrails-harness-developer` | `Cli/Commands/PlanHashCommand.cs`, `MarkReviewedCommand.cs`, `Cli/PlanProbe.cs` |
| 8 | `guardrails-review` §7 waved-plan paragraph matches what shipped | `guardrails-skill-author` | `.claude/skills/guardrails-review/SKILL.md` |
| 9 | Halt/diagnostic rendering for `BreakdownIncomplete` + the quarantine message (with **#469**) | `guardrails-ux` → `guardrails-harness-developer` | console + log-viewer surfaces |

**Sequencing.** 1 → 2 → 3 are strictly ordered (3 needs 2's classification, 2 needs 1's signal). 4 → 5
follow. **6 → 7 → 8 are independent of 1–5** and can run in parallel; 8 must land with 6+7 or the skill
still documents commands that fail. 9 last, once the halt kinds are stable.

**Tests** (`guardrails-test-author`, alongside each milestone):
- **The #471 regression, and it is the load-bearing one:** author a wave stub with a hand-written exit
  gate → snapshot `PlanDefinitionHash` → run a stub breakdown that writes tasks + gates then reports
  `FailureKind = Timeout` → assert the hand-written gate is **untouched**, everything the attempt wrote is
  under `rejected/`, and `PlanDefinitionHash` is **byte-identical to the snapshot**.
- Truncated-prefix salvage: 11 complete + 1 incomplete + a 14-entry manifest ⇒ `BreakdownIncomplete`,
  prefix preserved, `GR2063` names folders 12/13/14.
- Clean validate after a cut-off session ⇒ **still** `BreakdownIncomplete`, never `BreakdownComplete`.
- No-progress resume halts; segments are capped at 3.
- `GR2063` silent on every committed plan folder in the corpus.
- Per-wave marker: stamping wave 1 leaves wave 2 unstamped; authoring wave 2 does **not** stale wave 1;
  editing wave 1's guardrail body **does**; a fresh plan-level marker vouches for both; a stale one for
  neither.
- `plan-hash` / `mark-reviewed` on a wave folder: succeeds, emits `WaveDefinitionHash`, writes under
  `<wave>/state/`; `validate` on a wave folder errors with the parent-plan pointer.

---

## 13. Scope, honestly

**v1:** milestones 1–9 above.

**Deferred, with evidence gates:**
- `--wave <slug>` as a second spelling — until a real path-shape ambiguity appears.
- `WaveReviewHash` (a wave hash without `brief.md`) — until brief edits are a measured GR2025 noise source.
- Forward-reference reconstruction of the missing tail (#402's comment) — superseded by the manifest;
  revisit only if manifests turn out to be routinely absent at truncation time.
- Oversized-wave detection and splitting (#385 option 3) — belongs to #111/#378, not here.

**Rejected outright:**
- Raising the turn budget further, or raising the 30-minute timeout, as a fix for #385.
- Keeping the wave gates on quarantine (#471 option (b)) — §5.2.
- Correcting the `guardrails-review` skill to drop the per-wave stamp (#472 option 3) — it gives up the
  property #254 introduced the per-wave marker to get, and §7.3 shows that property is load-bearing.

**What would tell us v1 was wrong:**
1. Milestone 1's halts report `MaxTurns` rather than `Timeout` on the next large-wave truncation ⇒ §3.2 is
   wrong, the budget lever is real, and #402's priority drops back.
2. Truncations land *before* the manifest is written more often than after ⇒ the manifest is in the wrong
   place in the session and the safety rests entirely on §4.2.
3. `GR2063` fires on a hand-finished wave and an operator's first instinct is to delete the manifest rather
   than correct it ⇒ the warning is not carrying its remedy; re-shape the message before considering severity.
4. Per-wave markers produce *more* GR2025 lines than the single marker did ⇒ the fallback rule in §8.4 is
   too narrow.
