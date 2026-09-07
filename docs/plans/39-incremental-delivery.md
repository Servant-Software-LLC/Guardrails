# 39 — Incremental delivery: a batched plan stops holding finished work hostage

Design of record for **issue #525**. Status: **DRAFT — for Charter review.** Not implemented.

---

## What's being asked

In the maintainer's words:

> For a Claude session, it is really handy to be able to tell you to work on a range of GH issues all in
> one prompt. For you, that doesn't mean to do all the work for the whole range of issues before it goes
> to a CI pass. I am interested in Guardrails to be able to do the same one plan sort of batching.

That analogy is the whole argument. A Claude session told to work a range of issues **delivers as it
goes** — issue, commit, next issue. It does not hold six issues' work hostage to one terminal pass.

## What happens today

| level | integrates into | when |
|---|---|---|
| task branch `guardrails/<runId>/<task>/attempt-N` | — | per attempt |
| **plan branch** `guardrails/<plan-name>` | each green task merges in | as tasks finish |
| **the user's branch** | the whole plan branch, once | **run end, and ONLY if the run is WHOLLY green** |

Tasks integrate incrementally — into the **plan branch**, never the user's. The user's branch sees **one
merge, or none.**

On `docs/plans/25-backlog-slate` — five issues, 12 tasks, three independent chains:

```
#510 (isolated)     01 → 02 → 03 → 04
#511 (isolated)     05 → 06
observer (serial)   07 → 08 → 09 → 10 → 11
```

If task 09 fails, **#510's four completed tasks are not delivered either** — despite sharing no file, no
dependency and no failure mode with it. The work is not lost (it is on the plan branch) but delivery
becomes a manual merge, and the operator has to reconstruct which chain is safe to take.

**That is the batching tax**: the more issues you bundle — which is exactly what makes the
~$10-regardless-of-size breakdown economical — the more finished work one unrelated failure strands.

---

## Invariants in play

**2 — Harness is the single writer of merged state.** A delivery group merging to the user's branch is
still the harness merging; nothing here hands an agent a merge. The per-group merge uses the same
`mergeOnSuccess` machinery, called N times instead of once.

**5 — Honest halts.** A partially-delivered run is a **new run outcome**, and the report must say plainly
which groups delivered and which did not. The failure mode this design must not create is a run that says
"failed" while three groups are already on the user's branch — the operator would go looking for work that
has already landed. §5 is about that.

**1 — Deterministic gates.** A group is delivered only after a **deterministic gate over the group's own
merged tree**. Delivering on "its tasks went green individually" would ship exactly the merge-collision
class #175 exists to catch.

**Never-weaker.** A plan that declares no `deliveryGroups` behaves **byte-identically** to today: one
group, implicit, containing every task. No existing plan changes behavior.

---

## 1. The delivery unit is DECLARED, not inferred

The natural unit is a weakly-connected component of the DAG. **We do not use it as the unit**, for a
reason plan 25 demonstrates rather than argues:

> Task 12 (the SSOT + domain-knowledge update) `dependsOn` **all three chains**, because invariant 4 says
> the contract lands in the same change. That single edge makes the whole plan one component.

So inference returns "one group" for exactly the plans this feature exists to serve. Worse, it does so
*silently* and *correctly* — there is no defect to find, only a feature that quietly does nothing.

**Decision: `deliveryGroups` is declared in `guardrails.json`.**

```jsonc
"deliveryGroups": [
  { "name": "510-log-viewer", "tasks": ["01-…", "02-…", "03-…", "04-…"] },
  { "name": "511-provider-wait", "tasks": ["05-…", "06-…"] },
  { "name": "observer",  "tasks": ["07-…", "08-…", "09-…", "10-…", "11-…"] }
]
```

**Inference still exists — as a PROPOSAL, in `plan-breakdown`'s Step 7 report.** The skill computes the
weakly-connected components, names the edges that merged two chains it believes are independent, and asks
the human to accept or edit. That puts the inference where a wrong answer is cheap (a human reads it) and
keeps it out of the run, where a wrong answer strands or — far worse — prematurely delivers.

Every task belongs to **exactly one** group. A task in none, or in two, is a validation error (§6).

---

## 2. The fan-in sink: groups deliver, then the sink delivers

The issue names three candidate resolutions. Taking them in turn:

- **Declare groups rather than infer them** — necessary (§1) but not sufficient: the sink still
  `dependsOn` all three chains, so it cannot belong to any one of them.
- **Make the docs sink per-group** — rejected. It contradicts invariant 4 (the contract lands in the same
  change as the code) by splitting one SSOT edit across three commits, and it re-creates #175: three tasks
  appending to one SSOT section on separate branches merge with no conflict marker and three copies.
- **Groups deliver BEFORE the sink; the sink delivers last** — **chosen.**

The sink becomes its own delivery group, ordered last by its own `dependsOn` edges. Nothing special is
needed to order it: the DAG already does.

**What this costs, stated plainly.** A run that delivers `510-log-viewer` and then fails in the sink has
put code on the user's branch whose SSOT documentation has not landed. That is a real inconsistency and
this design accepts it, because the alternative is the status quo — where the same failure strands *all*
the code as well. The report says so explicitly (§5), and the sink group is named as the outstanding one.

**A plan may opt out** per group with `"deliverAfter": ["<group>"]` — an explicit delivery-ordering edge
for a group that must not land before another. Default: none.

---

## 3. The terminal gate: per-group, then plan-wide

GR2028 requires `<plan>/guardrails/` to carry a real integration re-run on the merged HEAD. That gate is
**plan-wide by construction** and it stays. The question is only what a *group* must pass before it
delivers.

**Decision: a group delivers on `<plan>/deliveryGates/<group>/`, and the plan-wide gate still runs at the
end over everything.**

- A **group gate is required** to deliver that group. A group with no gate folder is never delivered
  incrementally; it waits for the plan-wide gate like today. This makes the feature **opt-in per group**
  and makes the cost visible: you pay for a gate where you want early delivery.
- **A group gate runs against the tree the delivery will PRODUCE** — the user's branch with that group's
  commits merged onto it — and **not** against the plan branch as of that moment.

  This is the correction to my own first draft of this section, which said the plan branch and justified
  it as "not a synthetic tree". It is wrong, and wrong in the exact direction this whole report is about.
  The plan branch accumulates **every** green task from **every** group as it finishes (see the table in
  *What happens today*). A group gate run there is a gate run over other groups' in-flight work, which
  breaks both ways:

  - group A **fails** on code group B is midway through — a false red, on work A did not write; and worse
  - group A **passes** *because of* group B's code, is delivered alone, and **arrives on the user's branch
    broken** — a green gate certifying a tree that will never exist.

  The second is a false green manufactured by the delivery mechanism itself, which would make this feature
  a net loss over the all-or-nothing behaviour it replaces.

  What nobody ever runs is a tree of a group's commits with **no base**. The post-delivery user branch is
  emphatically something someone runs — it is the thing they asked for — so that is the tree to gate.
- **The plan-wide gate is not weakened and is not skipped.** It runs at the end over the whole merged
  HEAD, exactly as now. A group that delivered early is re-verified there; if the plan-wide gate then
  fails, the report says which groups are already on the user's branch (§5). We do **not** attempt to
  un-deliver.

**Cost, honestly.** The whole-suite check is the expensive one, and this runs it `N + 1` times. That is
the price of the feature and it is why the group gate is opt-in rather than automatic. A plan whose group
gate is a *subset* — the tests for that issue's area — pays much less and gets most of the value; the
`plan-breakdown` proposal (§1) should suggest exactly that shape.

---

## 4. File-disjointness is CHECKED, never assumed

> Chains are not automatically file-disjoint. Plan 25's observer chain exists *because* `LogSiteRenderer.cs`
> and `OnTheFlyDiagramObserver.cs` are wanted by three of the five items — two tasks appending to the same
> file on separate branches merge with no conflict marker and two copies (#175).

A delivery group that shares a file with an undelivered group can deliver a half-written file. So:

**`validate` computes the pairwise `writeScope` intersection across groups and warns on any overlap**
(a new GR code; the conservative-silence discipline GR2057 models, extended by the #601 GR2076 work which
already reconciles clauses across tasks). An overlap is not always wrong — a TDD stub/impl pair legitimately
shares a file and belongs in one group anyway — so it is a WARNING that names the file and both groups.

This is the check that makes the feature safe to default on for declared groups, and it is cheap: the
`writeScope`s are already in hand.

---

## 5. The report, and the outcome that does not exist today

**A partially-delivered run is a new outcome and needs its own words.** Today's summary has two states,
delivered and not. This design creates a third, and the honesty requirement is that it never renders as
either of the other two.

`run.json` grows a `deliveries` section:

```jsonc
"deliveries": [
  { "group": "510-log-viewer",   "status": "delivered",  "mergedAt": "…", "commit": "…" },
  { "group": "511-provider-wait","status": "delivered",  "mergedAt": "…", "commit": "…" },
  { "group": "observer",         "status": "held",       "reason": "task 09-… settled needs-human" },
  { "group": "docs-sink",        "status": "not-reached" }
]
```

And the terminal output leads with it, before the pass/fail verdict:

```
DELIVERED to your branch: 510-log-viewer, 511-provider-wait (2 of 4 groups)
HELD on the plan branch:  observer (09-… needs-human), docs-sink (not reached)
```

Three requirements, each earned by a defect this repo has already shipped:

- **Printed BEFORE the verdict, not after.** The `mergeOnSuccess` banner (#340) was printed after the green
  summary and was read straight past — the operator concluded a run had shipped when it had not.
- **`git branch --no-merged master` is the operator's confirmation**, and the report says so, because the
  report is the harness describing itself and the branch state is the fact.
- **The exit code does not change.** A run with a failed group is a failed run; partial delivery is not a
  partial success. The exit code answers "did the plan complete", the delivery block answers "what
  landed", and conflating them is how a green tick comes to certify something it did not check.

---

## 6. Seams and contracts touched

**Schema (`02-schemas-and-contracts.md`)**

- §2 `guardrails.json`: `deliveryGroups[]` (`name`, `tasks[]`, optional `deliverAfter[]`). Absent ⇒ one
  implicit group of every task ⇒ today's behavior exactly.
- §1 plan folder: `<plan>/deliveryGates/<group>/` as a fifth check folder, sharing the one guardrail
  grammar (`catches:` required, GR2027 applies).
- §7 `run.json`: the `deliveries[]` section above.

**New diagnostics** (next free is GR2077 after this session's GR2076):

| code | severity | fires when |
|---|---|---|
| `GR2077` | ERROR | a task belongs to no declared group, or to two |
| `GR2078` | ERROR | a `deliveryGroups` entry names a task id that does not exist |
| `GR2079` | WARNING | two groups' `writeScope`s intersect (§4) |
| `GR2080` | ERROR | `deliverAfter` forms a cycle |

**Harness**

- `Scheduler` learns group completion — a group is complete when every task in it has settled `succeeded`.
- A new `DeliveryGatePhase`, structurally the sibling of `PlanGuardrailPhase`, running one group's gate.
  It journals `status: "running"` before its first check, per the #625 rule.
- `mergeOnSuccess` becomes callable per group rather than once at run end.
- `IRunObserver` gains `GroupDelivered` / `GroupHeld`, forwarded through every decorator (the
  `ObserverForwardingSweepTests` contract).

**Skills**

- `plan-breakdown`: propose groups in the Step 7 report (§1), with the merging edges named; suggest a
  subset group gate rather than a whole-suite one (§3).
- `guardrails-review`: a probe for §4 — groups sharing a file — and for a group gate that is vacuous.

---

## 7. Devil's-advocate self-critique

**"The group gate is the whole cost, and you made it opt-in, so most plans will not get the feature."**
Correct, and intended. A delivery that is not gated is not a delivery, it is a push; making it automatic
would ship the #175 collision class straight to the user's branch. Opt-in with a visible cost is the
honest shape. If measurement later shows nobody opts in, the answer is a cheaper *default group gate*
(the touched area's tests), not an ungated delivery.

**"Declared groups will drift from the DAG as a plan is edited."** They will, which is why §6 makes a task
in no group an ERROR rather than a warning — the drift is caught at `validate`, before the run.

**"Delivering code before its SSOT edit violates invariant 4."** It does, at the level of the user's
branch, for the window between the code group and the sink. Invariant 4 is about a *change* landing whole,
and the plan branch still satisfies it. The design chooses a bounded, reported inconsistency over the
current unbounded stranding; a reviewer who disagrees should say so, because this is the load-bearing
trade and it is the one I am least certain of.

**"Your first draft of §3 gated the wrong tree."** It did, and I caught it re-reading rather than writing —
which is the #467 lesson landing on this document. Gating the plan branch would have let a group pass on a
sibling's in-flight code and then ship without it. Recording it here rather than quietly correcting it,
because a design whose author found one hole should be read as a design that may have two.

**"Why not just run one plan per issue?"** ~$10 breakdown per plan regardless of size, plus a review pass
each. Five issues becomes ~$50 and five reviews before a single task runs. Batching is what makes a
backlog sweep affordable; all-or-nothing delivery is what makes it risky. Fixing the second is what makes
the first usable — which is the issue's own framing and I have not found a reason to doubt it.

---

Refs #525, #340 (mergeOnSuccess default-on), #175 (the duplicate-definition merge hazard), #601/GR2076
(cross-task reconciliation, the machinery §4 extends), #625 (the journal-the-start rule §6 adopts),
SSOT §5.3 / §3.3.
