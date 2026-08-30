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

### 3.1 Provenance on failed attempts (#532) — the only item that blocks the others

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

**Open design question, deliberately not answered here:** what a bucket IS. Candidates seen in the
corpus — author-tests vs implement vs doc-edit vs wiring; `writeScope` cardinality; whether the task
has a stub tree. This needs its own decision, and it should be made against the corpus rather than
in the abstract.

### 3.3 The model digest

The report names this itself: *"the corpus stores no model digest, so a provider that swaps the
weights under a stable tag is NOT distinguished here (charter §5 model drift) — a gap in the row
schema."*

Low urgency while every row is a hosted Claude tag. **High the moment a local model is in the mix**: a
re-quantized local model under the same name is a different subject and must not be pooled as one
sample. Sequence it with #223, not before.

### 3.4 Then the charter's original list

Turns-used (computed, printed and discarded today), segmented durations, warm/cold, machine and
concurrency profile including unified memory, harness and skill versions.

The unified-memory item is not hypothetical: the charter records that the 64GB Mac Studio is a
**tighter** box than the 128GB MacBook available today, so the same model name will run at a different
quantization on each and **must not be pooled as one sample**.

## 4. Dependencies

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
