---
charter-format-version: 1
---
# 39 — Incremental delivery: a batched plan stops holding finished work hostage

Design of record for **issue #525**. Status: **DRAFT — for Charter review.** Not implemented.

> **Rewritten after review round 1.** The first draft invented `deliveryGroups`, a
> `<plan>/deliveryGates/<group>/` folder, a `deliverAfter` ordering field and a cross-group collision lint.
> The reviewer asked one question — *"do waves have this seam?"* — and the answer is that **they already
> have all of it.** This version is what remains once the invented machinery is deleted. §7 records what
> the first draft got wrong, because the mistake is more instructive than the design.
>
> **Round 2** then removed a second assumption: draft 2 treated *every* wave as a delivery unit. A
> shared-prerequisite wave — a DTO two issues both need — has no standalone value as a PR. Delivery is now a
> property of **some** waves (§1b).

---

## What's being asked

In the maintainer's words:

> For a Claude session, it is really handy to be able to tell you to work on a range of GH issues all in
> one prompt. For you, that doesn't mean to do all the work for the whole range of issues before it goes
> to a CI pass. I am interested in Guardrails to be able to do the same one plan sort of batching.

A Claude session told to work a range of issues **delivers as it goes** — issue, commit, next issue. It
does not hold six issues' work hostage to one terminal pass. Guardrails' plan model does.

## What happens today

| level | integrates into | when |
|---|---|---|
| task branch | — | per attempt |
| **plan branch** `guardrails/<plan-name>` | each green task merges in | as tasks finish |
| **the user's branch** | the whole plan branch, once | **run end, and ONLY if the run is WHOLLY green** |

On `docs/plans/25-backlog-slate` — five issues, 12 tasks, three independent chains — if task 09 fails,
**#510's four completed tasks are not delivered either**, despite sharing no file, no dependency and no
failure mode with it.

**That is the batching tax**: the more issues you bundle — which is what makes the
~$10-regardless-of-size breakdown economical — the more finished work one unrelated failure strands.

---

## 1. The seam already exists: a wave's EXIT GATE

Everything the first draft invented is already in the wave model:

| what a delivery unit needs | what the wave model already has |
|---|---|
| a boundary where a subset of work is complete | the **wave barrier** |
| a deterministic gate over the merged tree at that boundary | **`WaveJournalEntry.Exit`** — *"the plan-guardrail phase scoped to this wave, always re-evaluated on the current HEAD"* (SSOT §14.6) |
| ordering, so a docs sink lands after the code it documents | **waves are strictly ordered** — put the sink in the last wave |
| the merge machinery itself | **`Scheduler.DeliverAndCleanup`** (`:1144`), already *"shared by the flat and waved paths"* |
| per-unit journaling and reporting | `WaveJournalEntry`, `WaveStatus`, the wave gate events |

**So the whole feature is: fire the existing delivery at each wave's existing exit gate, instead of once at
run end.**

```
wave-01-shared-dto  delivers:false  tasks green → Exit green → held, rides along
wave-02-issue-510   delivers:TRUE   tasks green → Exit green → MERGE (waves 01+02)
wave-03-issue-511   delivers:TRUE   tasks green → Exit green → MERGE (wave 03)
wave-04-observer    delivers:TRUE   task 09 fails            → HELD; 01-03 already delivered
wave-05-docs-sink   delivers:TRUE   not reached
```

No `deliveryGroups`. No `deliveryGates/` folder. No `deliverAfter`. No cross-group collision lint — a
wave's tasks are already the unit `validate` reasons about.

### The one thing this design still has to get right

**The gate must run against the tree the delivery will PRODUCE** — the user's branch with this wave's
commits merged onto it — not merely against the plan branch as it stands.

For waves the distinction is much narrower than it was for the first draft's parallel groups (a wave
barrier is a quiescent point; there is no *other* wave mid-flight to contaminate the tree). But it is not
zero: the plan branch also carries the run's own harness commits and anything a prior wave left. The
delivery gate asserts over what lands, and the check is cheap once the merge is performed first.

---

## 1b. Not every wave is a delivery point

Draft 2 assumed each wave delivers. The reviewer's case shows why that is wrong:

> It may be that 2 GH issues that are being solved both require a change to common code (maybe a DTO, etc)
> and it may be that the changes need to be done in a wave, but that it isn't required that they make it
> into a PR before either one of the issues are implemented.

A **shared-prerequisite wave** has no standalone value. Delivering it alone puts a DTO change on your
branch with no consumer — a PR nobody can review on its merits and a state nothing exercises.

**So ordering and delivery are separate properties.** A wave is always an ordering unit; only some waves are
delivery points.

```jsonc
// in the wave's own manifest
{ "delivers": true }     // default FALSE
```

**A delivering wave ships everything accumulated since the last delivery point.** The non-delivering waves
before it ride along. This needs no new mechanism: the plan branch already accumulates exactly that way, so
"deliver at wave N" means "merge the plan branch as it stands", which is what `Finalize` already does at run
end.

Three things fall out of it, all improvements:

- **It replaces the plan-level `deliverPerWave` bool**, which was the wrong grain — all-or-nothing on a plan
  whose whole point is that its waves differ.
- **The gate that matters is the DELIVERING wave's exit gate**, over the accumulated tree. The prerequisite
  wave's code is therefore under test at the moment it *ships*, alongside its first consumer, rather than in
  isolation when it was written — which is the stronger test of the two.
- **A plan that marks no wave `delivers` behaves exactly as today**: one merge at run end. The never-weaker
  guarantee survives without a second flag.

**The failure mode to watch** is a plan where every wave is marked `delivers: true` out of habit, which
re-creates draft 2's assumption by hand. `plan-breakdown`'s Step 7 report should name which waves are
delivery points **and why**, so an author who marked a prerequisite wave as one has to notice.

## 1a. The interlock that per-wave delivery would otherwise BYPASS

Verified against `Scheduler.Finalize` (`:1144`) after the review round, because the whole design now rests
on reusing it. Two findings, and the second is the load-bearing one.

**The good one.** `RunWaveExitGateAsync` runs at `Scheduler.cs:958`, **inside the wave loop** — a barrier
is a point where that wave's exit gate has already run, inside `RunAsync`. Delivering there is
architecturally available, which is what makes this design cheap. (Contrast the four-folder
`<plan>/guardrails/` terminal gate, which the CLI evaluates *after* `RunAsync` returns — which is why
delivery is currently held back and completed by `CompleteDeferredDelivery`.)

**The one that has to be designed for.** `Finalize` does not simply merge. It carries the **#361 / #340
interlock**:

> a run whose result was SHAPED BY A MACHINE DECISION (a proceeded-best-guess or proceeded-unreviewed
> recorded in `decisions[]`) **DEFAULTS delivery OFF** — the verified work stays on the plan branch, never
> auto-delivered — UNLESS the operator EXPLICITLY forced delivery on.

That interlock reads `Document.Decisions` for the **whole run**, once, at the end. It is run-scoped because
delivery is run-scoped.

**Per-wave delivery breaks that, silently and in the dangerous direction.** Wave 1 merges at its barrier.
Wave 3 then records a proceeded-best-guess. At run end the interlock fires and suppresses delivery — of
work that is *already on the user's branch*. The interlock cannot un-merge, so its guarantee is not
weakened, it is **defeated for every wave that delivered before the decision existed.**

**Requirement: the interlock becomes WAVE-SCOPED.** A wave delivers only when no suppressing decision was
recorded **during that wave** — `decisions[]` entries carry a wave attribution already, and the run-end
call keeps the run-scoped reading for the final wave and every flat plan.

This is not a nicety. #361's entire point is that a machine-shaped result does not auto-deliver, and a
feature that delivers earlier must not become the way that rule is escaped. **A design that reuses an
existing safety interlock inherits the obligation to re-derive its scope**, and this one changes from
"the run" to "the wave".

## 2. The cost, and it is a DOCTRINE change

`plan-breakdown` says today:

> **Do NOT wave a flat plan** — fine-grained parallelism is a task DAG inside ONE wave, not multiple waves
> (waves are the COARSE ordering for stages whose downstream tasks can't be authored until the upstream is
> real; a wave barrier destroys cross-wave parallelism, SSOT §14 C5).

Plan 25's three chains are **independent**. They can run in parallel today. Putting them in three waves
**serialises them**, and that is a real loss.

**What is and is not lost.** A wave still contains an ordinary task DAG, so #510's four tasks keep their
internal parallelism. What goes is parallelism *across issues*.

**So this proposes a SECOND legitimate reason to wave**, alongside the existing one:

| reason to wave | why |
|---|---|
| *(existing)* the stage is **undesignable up front** — downstream tasks reference artifacts an upstream stage materialises | JIT breakdown at the barrier |
| ***(new)*** the stage is a **delivery unit** — you want it on your branch before the rest of the plan finishes | delivery granularity |

Both are coarse ordering; the second buys delivery instead of authorability. The doctrine sentence changes
from *"do not wave a flat plan"* to *"do not wave a flat plan **for parallelism** — wave it when you want
the stages delivered separately."*

**This is the reviewer's call, not mine** — it changes what `plan-breakdown` tells every future author, and
the wall-clock cost is paid by whoever runs the plan. See the question at the end.

---

## 3. What still has to be built

Much smaller than the first draft:

- **`DeliverAndCleanup` becomes callable at a wave barrier**, gated on that wave's `Exit` being green — not
  only at run end. The run-end call stays for the last wave and for every flat plan.
- **Delivery is opt-in per WAVE** (`"delivers": true` in the wave's manifest, default **false**) — §1b. A
  plan that marks nothing behaves byte-identically to today: one merge at run end. That is the never-weaker
  requirement, and the reason this cannot silently change what an existing waved plan does with your branch.
- **A wave with no `guardrails/` folder is never delivered early.** No gate, no delivery; it waits for the
  plan-wide gate like today. This is the first draft's opt-in principle surviving, now for free.
- **The report** (§4).

**Not built:** group declaration, group gates, group ordering, cross-group `writeScope` reconciliation.
All of it was reinventing the wave.

---

## 4. The report, and the outcome that does not exist today

A partially-delivered run is a **new run outcome** and must render as neither of the two that exist.

```jsonc
"waves": {
  "wave-01-issue-510":  { "status": "succeeded", "delivered": { "at": "…", "commit": "…" } },
  "wave-03-observer":   { "status": "failed",    "delivered": null }
}
```

```
DELIVERED to your branch: wave-01-issue-510, wave-02-issue-511 (2 of 4 waves)
HELD on the plan branch:  wave-03-observer (09-… needs-human), wave-04-docs-sink (not reached)
```

Three requirements, each earned by a defect already shipped here:

- **Printed BEFORE the verdict.** The `mergeOnSuccess` banner (#340) printed after the green summary and
  was read straight past — an operator concluded a run had shipped when it had not.
- **`git branch --no-merged` is the confirmation**, and the report says so: the report is the harness
  describing itself; the branch state is the fact.
- **The exit code does not change.** A run with a failed wave is a failed run. The exit code answers "did
  the plan complete"; the delivery block answers "what landed". Conflating them is how a green tick comes
  to certify what it did not check.

---

## 5. Seams and contracts touched

**Schema** — a wave manifest gains `delivers` (bool, default false); `run.json`'s `waves.<dir>` gains
`delivered` (`{at, commit, covers: ["<wave>", …]}` or null — `covers` names the non-delivering waves that
rode along, so the report can say what a merge actually carried). No new folder.

**New diagnostics** — one, not four: a WARNING when a wave sets `delivers: true` and carries no
`guardrails/` exit gate, so an author learns that wave cannot deliver rather than discovering it by its
absence from the report.

**Harness** — `DeliverAndCleanup` callable at the barrier; `IRunObserver.WaveDelivered`, forwarded through
every decorator (the `ObserverForwardingSweepTests` contract); the delivery journaled with `status: running`
before the merge, per the #625 rule.

**Skills** — `plan-breakdown`'s §0 wave/flat fork gains the second reason to wave (§2), and the Step 7
report names which waves are delivery units.

---

## 6. Devil's-advocate self-critique

**"You are pushing authors to wave plans the doctrine says not to wave."** Yes, and §2 says so plainly
rather than burying it. The honest framing is that the existing sentence conflates two different reasons
not to wave, and only one of them (parallelism) applies here. If the reviewer thinks the parallelism loss
outweighs delivery granularity, the answer is to reject this and keep #525 open rather than to build the
first draft's parallel-group machinery — which had all the same problems *plus* a new schema.

**"A wave barrier is a synchronisation point, so per-wave delivery makes runs slower AND more complex."**
Slower, yes, for a plan that would otherwise run its chains concurrently. More complex, no — this is
strictly less machinery than today's plan-wide-only delivery plus the first draft's proposal.

**"Delivering wave 1 before the docs-sink wave still breaks invariant 4."** It does, for the window between
them, exactly as the first draft did. The mitigation is the same and is now free: the sink is the last
wave, and waves are strictly ordered, so the window is bounded by construction rather than by a
`deliverAfter` field somebody has to remember to write.

**"Draft 2 assumed every wave is a delivery unit."** It did, and the reviewer's shared-DTO case broke it
in one sentence. The pattern across both rounds is the same: I took an existing structure (first the absence
of one, then the wave) and assumed its grain matched the problem's. The wave is the right ORDERING unit and
was never the right DELIVERY unit on its own — those had to be separated, and only a concrete counter-example
made that visible.

**"You reused an interlock without re-scoping it."** Draft 2 did, for about an hour — §1a exists because
reading `Finalize` to check the reuse was feasible turned up the #361 machine-decision gate, which is
run-scoped and would have been silently defeated for every early-delivered wave. It is the same class of
error as draft 1: taking an existing mechanism and assuming its shape carries over. The cheap defence is
the one that caught it — read the code you are claiming to reuse.

**What I got wrong in draft 1, since it is the useful part.** I designed a parallel-group delivery model
without asking whether the ordered-stage model already had the seam. Every piece I invented —
`deliveryGroups`, `deliveryGates/`, `deliverAfter`, the cross-group collision lint — has a wave equivalent
that already ships, is already validated, and is already journaled. The reviewer found it with one
question. The lesson generalises past this design: **when a feature needs "a subset of the plan, a gate
over it, and an order", check the wave model before inventing a second one.**

---

Refs #525, #340 (mergeOnSuccess default-on), #175 (the merge hazard waves already order around), #625 (the
journal-the-start rule §5 adopts), SSOT §14 (waves), §14.6 (the wave exit gate), §5.3 / §3.3.

---

## Decisions for this review

:::question
{"id": "d39-wave-doctrine", "title": "Should DELIVERY GRANULARITY become a second legitimate reason to wave a plan?", "mode": "single", "options": ["Yes — waves mean coarse ordering, and delivery is as good a reason as authorability", "No — the parallelism loss is too high; keep waves for undesignable stages only", "Yes, but only when the plan is a backlog batch of independent issues"], "recommended": "Yes — waves mean coarse ordering, and delivery is as good a reason as authorability", "rationale": "The remaining cost of this design and the only decision that changes doctrine. plan-breakdown says today 'Do NOT wave a flat plan — a wave barrier destroys cross-wave parallelism'. Plan 25's three chains are independent and could run concurrently; waving them serialises them. A wave still holds a parallel task DAG, so what is lost is parallelism ACROSS issues, not within one. I lean yes because that sentence conflates two different reasons not to wave and only the parallelism one applies here — but it changes what plan-breakdown tells every future author, and the wall-clock is paid by whoever runs the plan.", "target": "human", "answer": ["Yes \u2014 waves mean coarse ordering, and delivery is as good a reason as authorability"]}
:::

:::question
{"id": "d39-delivers-grain", "title": "Is a per-WAVE 'delivers' flag (default false) the right grain, after the shared-prerequisite case?", "mode": "single", "options": ["Per-wave 'delivers', default false — a delivering wave ships everything since the last delivery point", "Per-wave, but default TRUE for any wave carrying an exit gate", "Infer it — a wave whose writeScope no later wave depends on is a delivery point"], "recommended": "Per-wave 'delivers', default false — a delivering wave ships everything since the last delivery point", "rationale": "Your DTO case killed my plan-level bool: a shared-prerequisite wave must be ordered but must NOT ship on its own, so all-or-nothing was the wrong grain. Default false keeps the never-weaker guarantee — a plan that marks nothing behaves exactly as today. I rejected inference for the same reason declared groups beat inferred ones in draft 1: it would silently mark a prerequisite wave as a delivery point whenever nothing happened to depend on it yet, which is precisely the case you raised. Option 2 is the one worth arguing for if you think the flag will just be forgotten.", "target": "human", "answer": ["Per-wave 'delivers', default false — a delivering wave ships everything since the last delivery point"]}
:::

:::question
{"id": "d39-interlock-scope", "title": "Confirm the #361 machine-decision interlock must become WAVE-scoped before any wave delivers early?", "mode": "single", "options": ["Yes — a wave delivers only if no suppressing decision was recorded during that wave", "No — keep it run-scoped and simply forbid early delivery once any suppressing decision exists", "No — early delivery should ignore the interlock; it is about the run's final verdict"], "recommended": "Yes — a wave delivers only if no suppressing decision was recorded during that wave", "rationale": "Found by reading Finalize rather than trusting its name. The #361/#340 interlock reads decisions[] for the WHOLE run, once, at the end — because delivery is run-scoped today. With per-wave delivery, wave 1 merges, wave 3 then records a proceeded-best-guess, and the interlock fires at run end against work already on your branch. It cannot un-merge, so its guarantee is not weakened but DEFEATED for every wave that shipped before the decision existed. Option 2 is the conservative alternative and I would accept it; option 3 is listed only so it is on the record as rejected — it would make this feature the way #361 gets escaped.", "target": "human", "answer": ["Yes \u2014 a wave delivers only if no suppressing decision was recorded during that wave"]}
:::
