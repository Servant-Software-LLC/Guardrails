# 30 — Telemetry Phase 1: close the instrumentation gaps (#548)

**Status:** design of record, cut from Phase 0's first real report. Not yet reviewed.
**Issue:** #548. **Parent:** #533 (the model-evidence arc). **Follows:** #535 (Phase 0, merged `2245aec`).
**Depends on:** #547 (corpus integrity) landing first or in parallel.

---

## 1. Why this plan exists, and why it is not the plan the charter guessed

The charter deliberately refused to cut Phases 1–3 as issues up front:

> **Phase 0's first real report changes what Phases 1-3 should be.** Which instrumentation gap matters
> most, whether a bench is even needed, what a graduation threshold should be — all of those are
> answered by looking at the corpus, and none of them can be authored honestly before it exists.

That report has now been read, and it did change the answer. The charter's Phase 1 list opened with
*"turns-used first"*. **Turns-used is not first.** It is fourth, behind a defect that makes every
comparison in the corpus unreadable.

## 2. The finding that reorders everything

Over 171 real tasks / 419 rows / **$791 of real spend**, backfilled from ten plan folders:

```
MODEL FINGERPRINT                       TIER        BUCKET        N   FIRST-PASS  ABANDONED  COST
(no route recorded)                     (unstated)  (unbucketed)  46  2.2%        8.7%       $373.048
?/?/(cli default)                       (unstated)  (unbucketed)  74  100%        0%         $283.0318
claude/claude/(cli default)             (unstated)  (unbucketed)  22  100%        0%         $72.4762
claude/haiku/claude-haiku-4-5-20251001  easy        (unbucketed)  1   insufficient evidence — n=1
claude/opus/claude-opus-5               hard        (unbucketed)  8   100%        0%         $28.1577
claude/sonnet/claude-sonnet-5           medium      (unbucketed)  20  100%        0%         $34.846
```

**Every routed stratum reads 100% first-pass**, which is impossible on this data — plan 27's task 08
alone burned twelve attempts before it was split into two tasks.

The cause, measured directly from `27-operator-visibility/state/run.json` (all 23 attempts):

| attempts | outcome | provenance |
|---|---|---|
| 9 | `succeeded` | **present** |
| 10 | `guardrail-failed` | **absent** |
| 2 | `max-turns` | absent |
| 1 | `permission-denied` | absent |
| 1 | `cancelled` | absent |

**Fourteen of twenty-three attempts — every single failure — carry no provenance.** They cannot be
attributed to a model, so they fall into `(no route recorded)`, and each routed stratum contains only
its own successes. *100% first-pass is not a measurement; it is the definition of what is left after
the failures have been filtered out.*

**The cost column carries the same bias inverted.** `claude/sonnet` shows $34.85 across 20 tasks while
`(no route recorded)` carries **$373** — the largest bucket in the report, attributable to nobody. The
retries those models actually cost are booked to the anonymous bucket, so a per-model cost comparison
understates the expensive models by exactly their failure rate.

**Why it is not visible in the output.** The report is scrupulous about the gaps it knows about — it
prints `(unstated)`, `(unbucketed)`, `insufficient evidence` at n=1, and a legend explaining that
`(not reported)` is not `$0.00`. It has no way to say *"the 100% in this row is survivorship."* Every
other honesty rule is working, which is exactly what makes a reader trust the one that is broken.

## 3. Scope

**Q: 92 older failed rows carry no provenance and 447 of 587 name no usable model. What happens to the pre-fix corpus?** — Answered: Document a boundary date and filter before it
_Question — id: `prefix-corpus-era`; mode: `single`; target: `human`; options: `Document a boundary date and filter before it`, `Backfill from the run journals where the answer survives`, `Re-baseline - archive the corpus and start clean`, `Leave it and let analyses mix eras`; recommended: `Document a boundary date and filter before it`_
_Why: The §3.1 fix is forward-only, so history stays skewed. Documenting a boundary is honest, costs nothing, and is reversible - a backfill can still happen later. Backfilling first sounds better but the run journals may not carry provenance for every era either, so it is unbounded work against unknown yield. Re-baselining throws away 587 rows of real spend history to fix an attribution problem. The one option that is genuinely bad is the last: an analysis that silently mixes a pre-fix and post-fix era is exactly the flattering-numbers failure this whole plan exists to prevent._

**Q: Does Phase 1 OWN closing the model-attribution gap (#577), or only the census that scopes it?** — Answered: Phase 1 owns the census only; the fix is its own issue
_Question — id: `577-ownership`; mode: `single`; target: `human`; options: `Phase 1 owns closing it`, `Phase 1 owns the census only; the fix is its own issue`, `Leave #577 entirely outside this plan`; recommended: `Phase 1 owns the census only; the fix is its own issue`_
_Why: The bucket schema was settled today and makes like-work-to-like-work EXPRESSIBLE; #577 is what keeps it UNANSWERABLE, so the two belong in the same phase. But the census comes first and is cheap - what fraction of the 313 'None' rows are script actions (correct by construction) versus a recording gap (a defect). Until that split exists, 'close it' has no defined scope, and committing a plan to close an unscoped defect is how a phase slips. The roadmap #570 now lists #577 in Phase A on this reasoning._

### 3.1 Provenance on failed attempts (#532) — SHIPPED 2026-09-01

> **STATUS: DONE, and its own acceptance test is met.** Merged as `3129919` *("a failed attempt now
> says which model it was billed on")*. This section is kept as the record of what was asked and why,
> not as remaining work.
>
> **The acceptance below was "the report changing, not the field appearing" — verified against the
> live corpus:** 48 failed rows now carry provenance where **zero** did before. Counted 2026-09-01
> over 587 rows.
>
> **One residue the acceptance did not cover, recorded rather than left to be rediscovered:** the fix
> is forward-only. 92 older failed rows still carry no provenance, so any analysis over the *full*
> corpus history remains skewed unless filtered by date or tool version. Deciding what to do about
> the pre-fix era — backfill, re-baseline, or a documented boundary — belongs with #577, which owns
> the wider model-attribution gap.
>
> #532 itself stays OPEN for a **different** gap (the harness-write disposition), which was never
> what this section was about.

Journal `provenance` on **every** attempt record, not only succeeded ones. The route resolves at
`TaskExecutor.cs:648-654`, **before the action runs**, so it is known for a failed attempt exactly as
it is for a successful one — this is a plumbing gap, not a knowledge gap.

**Done when:** a plan whose task fails and then succeeds produces corpus rows in which BOTH attempts
carry the same fingerprint, and the report's first-pass rate for that stratum is measurably below
100%. **The acceptance is the report changing**, not the field appearing — a field that is populated
but not reaching the corpus is the same defect one layer over.

### 3.2 The task-fingerprint bucket

Every row is `(unbucketed)`, and the report's own legend states the constraint: *"a bucket is a fact
about a task, never one read off its name."* Without it the corpus compares models but never **like
work to like work**, which is the comparison a graduation threshold actually needs. *"Sonnet handles
this class of task"* is the claim worth making; *"sonnet is 100%"* is not.

**SETTLED 2026-09-01, against the corpus as this section asked.** The first finding was that *none*
of the candidates was computable: a telemetry row carries `taskId`, `model`, `runner`, `kind`, `tier`,
`tierSource`, `effort`, cost and tokens — and **nothing structural about the task**. No `writeScope`,
no guardrail shape. So `author-tests vs implement` could only have been read off `taskId`, which the
report's own legend forbids. The question is therefore not *which* candidate but **what structural
fact the harness emits at write time**.

**The bucket is derived from two things the harness already holds at attempt time — the task's
`writeScope` roots and its guardrail archetypes.** Never from the task's name.

| bucket | rule | measured |
|---|---|---|
| `test-authoring` | writes `tests/**` only, **and** carries a TDD-red guardrail (`tests-fail-on-stubs` / `-on-current-code`) | 45 (14%) |
| `implementation` | writes `src/**` only, gated by a `tests-pass` guardrail | 82 (26%) |
| `structural` | writes `src/**` or `tests/**` with **no** behavioural gate — stubs, anchors, record additions, renames | 35 (11%) |
| `code+tests` | writes **both** `src/**` and `tests/**` | 67 (21%) |
| `documentation` | writes `docs/**` / `.claude/**` only | 44 (14%) |
| `no-write` | `writeScope: []` — verification and state-only tasks | 39 (12%) |

Measured over **316 tasks across 18 plan folders**. No bucket is degenerate and none is a catch-all:
the largest residual category in the first cut, `multi-root` at 23%, turned out to be **90% one
shape** (`src+tests`, 67 of 74), which is why it is a named bucket rather than an "other". It is also
the shape Step 2 rule 5 of `plan-breakdown` says must split, so its rate is worth watching on its own.

**What this bucket does NOT claim.** It is a fact about a task's *write surface and gate shape*, not
about its difficulty. `structural` contains both a two-line stub and a 1,000-line anchor test.
Difficulty is `action.tier`, which is already a separate column — do not collapse the two.

**DECIDED 2026-09-01 — the pre-fix era gets a documented boundary, not a backfill.** The §3.1 fix is
forward-only: 92 older failed rows carry no provenance, and 447 of 587 name no usable model. Phase 1
records a boundary date and every analysis filters before it. Backfilling was rejected as unbounded
work against unknown yield (the run journals may not carry provenance for every era either), and
re-baselining was rejected as discarding real spend history to fix an attribution problem. Both remain
available later; a documented boundary forecloses neither. The option deliberately ruled out is
letting analyses silently mix a pre-fix and post-fix era — which is precisely the flattering-numbers
failure this plan exists to prevent.

**Known limit, recorded rather than discovered later.** Bucketing makes the comparison *expressible*;
it does not make it *answerable yet*. Of 587 corpus rows only **140 name a real model** (313 `None`,
134 `(cli default)`), so per-(bucket × model) cells are single digits today. The schema is worth
emitting now — every future row is bucketed, and the corpus fills as tiered runs accumulate — but no
graduation threshold should be computed off it until the cells are populated. The model-attribution
gap is tracked separately.

### 3.3 The model digest

**Q: #223 has SHIPPED, so this section's own trigger has fired — is the model digest in Phase 1 scope now?** — Answered: Yes - bring it into Phase 1
_Question — id: `model-digest-scope`; mode: `single`; target: `human`; options: `Yes - bring it into Phase 1`, `No - keep it for the bring-up week (Phase C)`, `Only the schema field now, the digest capture later`; recommended: `Only the schema field now, the digest capture later`_
_Why: This section says 'low urgency while every row is a hosted Claude tag, HIGH the moment a local model is in the mix. Sequence it with #223, not before.' #223 merged as plan 28, so the condition it names is met. But no local model has actually produced a row yet, so the URGENCY it describes still has not arrived. Adding the nullable field now is cheap and means the first local row is capturable; building digest capture before any local runner has been pointed at real hardware is work against an unmeasured surface. The middle option is the one that cannot go stale either way._

The report names this itself: *"the corpus stores no model digest, so a provider that swaps the
weights under a stable tag is NOT distinguished here (charter §5 model drift) — a gap in the row
schema."*

Low urgency while every row is a hosted Claude tag. **High the moment a local model is in the mix**: a
re-quantized local model under the same name is a different subject and must not be pooled as one
sample. Sequence it with #223, not before.

> **DECIDED 2026-09-01 — IN Phase 1, in full. This overrides the drafting agent's lean.** The agent
> recommended the schema field now and the digest capture later, reasoning that #223's *condition* has
> fired while the *urgency* has not, since no local model has produced a row yet. **The maintainer
> chose the full scope**, and the work must not drift back toward field-only: Phase 1 delivers both the
> row-schema field and the capture that populates it.
>
> The reviewer's call has the better of the argument on timing. The digest exists to stop a
> re-quantized model under a stable tag being pooled as one sample — and the first moment that can
> happen is the *first local row*, which Phase C produces. A field with no capture behind it would be
> present and empty exactly when the first sample it was meant to disambiguate arrives.

### 3.3a Model attribution — the CENSUS (#577)

**DECIDED 2026-09-01: Phase 1 owns the census; the fix ships as its own issue (#577).**

Of 587 corpus rows only **140 name a real model** — 313 `None`, 134 `(cli default)`. Settling §3.2's
bucket made the like-work-to-like-work comparison *expressible*; this is what keeps it *unanswerable*,
which is why the two sit in the same phase.

Phase 1's deliverable is the split, not the repair: **what fraction of the 313 `None` rows are script
actions** — correct by construction, since a script invokes no model — **versus a genuine recording
gap.** Until that number exists, "close it" has no defined scope, and committing a phase to closing an
unscoped defect is how a phase slips.

### 3.4 Then the charter's original list

**Q: Is the incoming Mac Studio really the TIGHTER box than the 128GB MacBook? The plan's reasoning depends on it.** — Answered: Yes - 64GB Mac Studio, tighter than the MacBook
_Question — id: `unified-memory-fact`; mode: `single`; target: `human`; options: `Yes - 64GB Mac Studio, tighter than the MacBook`, `No - the Mac Studio has MORE unified memory`, `Not decided yet`_
_Why: This section records that the 64GB Mac Studio is a tighter box than the 128GB MacBook available today, and concludes the same model name will run at a different quantization on each and must not be pooled as one sample. That conclusion is right EITHER WAY - two boxes with different memory produce different quantizations regardless of which is larger - but the plan states a specific fact about your hardware that only you can confirm, and #570 already flags that #544 carries a stale '~Sept 2026' date. If the configuration changed, the sentence should be corrected before it is quoted downstream. Genuinely no lean: this is a fact, not a judgement._

Turns-used (computed, printed and discarded today), segmented durations, warm/cold, machine and
concurrency profile including unified memory, harness and skill versions.

The unified-memory item is not hypothetical, and the maintainer **confirmed the configuration on
2026-09-01**: the 64GB Mac Studio is a **tighter** box than the 128GB MacBook available today, so the same model name will run at a different
quantization on each and **must not be pooled as one sample**.

## 4. Dependencies

> **Note:** **Every dependency this section names is now CLOSED, verified 2026-09-01.** #547 (test suites
> poisoning the real corpus), #546 (verbatim provider tokens) and #223 (the local-inference runner,
> shipped as plan 28) are all merged. Nothing in this plan is blocked on other work.
>
> That has a consequence §3.3 has not caught up with — see the question there.

- **#547 first, or in parallel.** Any `dotnet test` of the Integration suite currently writes hundreds
  of fixture rows into the operator's real corpus — a single suite run produced 683 rows across 248
  synthetic runs, and Phase 0's first report rendered a confident stratified table over **exactly zero
  real data**. Instrumenting a corpus that a test run poisons is wasted work.
- **#546** (verbatim provider tokens) is small and should land before the first `openai-compat` row.
- **#223** gates §3.3's urgency, and nothing else here.

## 5. Out of scope

- **Phase 2 (the bench)** — and note it is **not gated on new hardware**: the charter is explicit that
  a 128GB MacBook is available today and the only real dependency is #223.
- **Phase 3 (graduation thresholds)** — cannot be authored until §3.1 and §3.2 make a like-for-like
  comparison possible. Same reasoning that kept this plan from being written before Phase 0 ran.
- **Any change to the report's honesty rules.** They are working; §2 is a data defect, not a rendering
  one, and "annotate the 100% as survivorship" would be treating the symptom.

## 6. What the corpus can and cannot answer today

**Can:** total real spend, by whom, over 171 tasks; which strata have enough n to say anything at all
(`insufficient evidence` fired correctly at n=1).

**Cannot:** any first-pass or attempts-to-green comparison between models (survivorship); any
per-model cost comparison (the same bias inverted); any like-for-like task comparison (no bucket); any
claim about model drift (no digest).

Every one of those is a decision #533 says the measurement is supposed to make.

<!-- charter: answers-sha256=none -->

<!-- charter: plan-sha256=77eb2bb5308379a319bbe983cb9f28bb78193f936648329c59a97e5ee9951c45 -->