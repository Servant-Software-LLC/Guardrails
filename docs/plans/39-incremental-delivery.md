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
| the merge machinery itself | **`Scheduler.Finalize` → `DeliverToUserBranch`** (`Scheduler.cs:1145`/`:1308`), already shared by the immediate path and the terminal-gate-deferred one. *(Corrected at review: this cell said `DeliverAndCleanup`, which does not exist — `grep -rn DeliverAndCleanup src/ tests/` exits 1. Grep for `DeliverToUserBranch`.)* |
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
delivery gate asserts over what lands.

**DECIDED (review, 2026-09-11): a TRIAL MERGE on a scratch ref.** The first draft said only *"the check is
cheap once the merge is performed first"*, which read as *merge onto the user's branch, then gate* — and
that is not safe. It contradicts §3's *"gated on that wave's `Exit` being green"* (the gate would run
after the write it is supposed to authorize), it contradicts the #588 requirement that the operator's
checkout is not modified, and the only way back from a red gate is the un-merge §1a says the interlock
**cannot** do. The order is therefore:

1. Merge the plan branch onto a throwaway ref — `refs/guardrails/trial/<waveDir>` — never onto the
   user's branch.
2. Run the wave's `Exit` gate against THAT tree. It is the tree the delivery would produce, so §1's
   requirement is met exactly.
3. Consult the interlock (§1a) **before** any write the operator can see.
4. On green, fast-forward the user's branch to the trial ref. On red, delete the ref; the user's branch
   never moved, and there is nothing to un-merge.

The scratch ref is a harness-owned ref under `refs/guardrails/`, not a branch, so it never appears in the
operator's `git branch` output and needs no cleanup beyond the delete.

**What the provider does not offer yet (open: `d39-trial-delivery-primitive`).** No `IWorktreeProvider`
member builds a trial ref or promotes one. The three checks an operator relies on at delivery are private to
`GitWorktreeProvider.MergePlanBranchIntoUserBranch`: the #588 moved-HEAD refusal (`:422`), the #448 dirty-tree
intersection (`:435`), and the #149 rule that the merge commit landing on the user's branch runs their hooks
(`:474`). The promotion in step 4 is always a fast-forward, which runs no commit hook, so a design that
reuses only the promotion makes `HookRejected` unreachable for waved delivery. The recommended shape is two
provider members. The first runs the #588 check, builds `refs/guardrails/trial/<waveDir>` (creating the merge
commit WITH the user's hooks), and reports whether the user's tip was already an ancestor of the plan tip. The
second re-runs #588 and #448 against the trial ref and fast-forwards. In the quiet case the trial ref IS the
plan tip, so the exit gate that already ran on the integration worktree is the gate on the delivered tree;
only a user branch that moved needs a trial worktree and a second gate run.

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

```yaml
# wave-NN-<slug>/brief.md — YAML front matter, at the top of the file
---
delivers: true      # default FALSE
---
```

**Where the flag lives, and why it is NOT a new file (review, 2026-09-11).** The first draft said "the
wave's own manifest". **There is no wave manifest** — SSOT §14.1 is explicit that v1 has *"ONE shared run
config (no per-wave config in v1)"*, and the obvious guess fails silently. A `guardrails.json` dropped into
`wave-NN/` is **ignored by the plan's loader**: the plan stays waved, `validate` says nothing, and a flag
written there is never read, so it looks as if it worked. Only a verb pointed at that wave directory itself
notices the file. `WaveFolder.TryResolveWaveTarget` treats *"a directory that carries its own
`guardrails.json`"* as a plan in its own right, so `validate`, `plan-hash` and `mark-reviewed` aimed at the
wave load it as a separate flat plan instead of reporting GR1010 (a wave folder is not a loadable plan).
*(Corrected at review, 2026-09-13: this paragraph said the stray file "silently un-waves the plan". Measured
with `guardrails` 1.19.0 on a copy of `examples/waved-hello`: `validate <plan>` prints the same two per-wave
diagnostics with and without the file, and only `validate <plan>/wave-01-scaffold` changes, from GR1010 to
"plan is valid".)* The flag therefore goes in the front matter of the **existing optional `brief.md`** (SSOT
§14.10). Two things fall
out for free, and they are the reason this beats a new `wave.json`: the loader needs no change to its wave
DETECTION predicate, and `WaveDefinitionHash` **already folds `brief.md`** (`Compute` → `GateDefinitionOf`),
so flipping `delivers` on a completed wave re-stales that wave's marker and trips drift exactly as SSOT
§14.7 requires — with no hasher change at all.

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

**DECIDED (review round 2): per-wave `delivers`, default `false`** — a delivering wave ships everything
accumulated since the last delivery point. The two rejected alternatives, and why:

- **default `true` for any wave carrying an exit gate** — it would turn every existing waved plan into a
  per-wave deliverer on upgrade, changing what those plans do with the user's branch without anyone asking.
  The never-weaker rule forbids it.
- **inference** (a wave no later wave depends on is a delivery point) — it would silently mark a
  shared-prerequisite wave as a delivery point whenever nothing yet depended on it, which is *precisely* the
  case that produced §1b. Same reasoning that made declared groups beat inferred ones in draft 1.

The accepted cost is that the feature does nothing until an author sets the flag. If that turns out to mean
nobody uses it, the answer is a better `plan-breakdown` proposal — not a silent default.

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

**Requirement: the interlock becomes WAVE-SCOPED — DECIDED (review round 2).** The maintainer's answer:
*"a wave delivers only if no suppressing decision was recorded during that wave."* The run-end call keeps
the run-scoped reading for the final wave and every flat plan.

**CORRECTED (review, 2026-09-11): `decisions[]` entries do NOT carry a wave attribution, and this design
must ADD one.** The first draft asserted the attribution "already" existed; it does not. `DecisionEntry`
has eighteen public members and none is a wave. `Subject` is free text documented as *"a task id / wave dir
/ the drifted unit(s)"*, and only the `Boundary = "wave"` factories put a wave dir there — the `task`
(Overwatch) and `drift` boundaries, which are the ones that actually produce `proceeded-best-guess`, put a
task id. Deriving the wave by string-splitting a §14.2 wave-qualified id would be undocumented, waved-plans-
only, and silently wrong for exactly the boundaries that matter. So the interlock re-scoping REQUIRES a
real `Wave` member on `DecisionEntry`, recorded at the point the decision is made rather than parsed back
out afterwards. That is a change to the shared `decisions[]` surface (SSOT §2.1/§7) and belongs in the SSOT
in the same change.

This is a **precondition, not a follow-up**: no wave may deliver early until the interlock is re-scoped, or
the feature becomes the way #361 is escaped.

**Ride-along (open: `d39-interlock-ride-along`).** A delivery carries every wave since the last delivery
(§1b), so "no suppressing decision recorded during that wave", read literally, is too narrow. If wave 02
records a `proceeded-best-guess` and is held, a clean wave 03's delivery still carries wave 02's commits onto
the user's branch. The recommended reading scopes the check to the delivery's `covers` list, every wave it
carries: once a wave is held, every later delivery is held too until run end, unless the operator overrides
with `--merge-on-success`.

This is not a nicety. #361's entire point is that a machine-shaped result does not auto-deliver, and a
feature that delivers earlier must not become the way that rule is escaped. **A design that reuses an
existing safety interlock inherits the obligation to re-derive its scope**, and this one changes from
"the run" to "the wave".

## 1c. Does a delivering wave let later waves pick up the user's branch? (review round 3)

The reviewer asked it directly:

> If waves can be deliverables, then when a wave sets the delivery flag, then the following waves will take
> the latest of master. Right? Or is it continuing on the branch that it delivered.

**It continues on the branch it delivered from.** Checked, not assumed:

- `Scheduler.RunWavedAsync` (`:774`) drains every wave *"on the CONTINUOUS plan branch"*, and there is no
  rewind or re-base between waves — the run's state is explicitly *"shared, CONTINUOUS run state across
  every wave"* (`:201`).
- `GitWorktreeProvider.MergePlanBranchIntoUserBranch` (`:400`) is **one-directional**: plan branch → user
  branch. Nothing merges the other way, at any boundary.

So delivery publishes; it does not synchronize. That is invisible today because delivery happens once, at
run end, when there is no "later" left to be stale. Per-wave delivery is what gives the asymmetry somewhere
to accumulate — and this section exists because that was not obvious until someone asked.

### What actually degrades, and what does not

**The quiet case stays cheap, and this is the reassuring half.** After wave 2 delivers by fast-forward, the
user's branch and the plan branch are at the same commit. Wave 3 adds commits on top; if nothing else
touched the user's branch, wave 3's delivery fast-forwards too. *n* deliveries on a quiet branch are *n*
fast-forwards, and the never-weaker guarantee is untouched. For the solo operator this whole section is a
no-op.

**The divergent case degrades, and it compounds.** The moment the user's branch advances independently, the
delivery falls off the FF path into a real merge commit (`:451` → `:486`) — *on the user's branch only*. The
plan branch never learns about it. So:

1. Wave 2's delivery makes a merge commit on the user's branch.
2. Wave 3's delivery can no longer fast-forward — the user's branch now carries a commit the plan branch has
   never seen — so it is another merge commit.
3. Every later delivery merges a plan branch that is **one more wave further out of date**, against a base
   it has never incorporated. The conflict surface grows monotonically with each delivery, and AI-merge is
   withheld here by SSOT §5.3, so a conflict **halts the run** with the work stranded on the plan branch.

That is the honest answer to the question: not "the following waves take the latest of master", but "the
following waves take an increasingly stale base, and the price is paid at each delivery instead of once."

**And `BranchMoved` changes character entirely.** #588 pinned the delivery target at run start and made a
moved HEAD a *refusal* rather than a redirect — correct, and the incident that produced it was real. But
today that refusal fires **once, at run end**, with every task already complete and safely on the plan
branch: a soft landing. With per-wave delivery it fires at wave 2's exit, with three waves still to run, and
every one of those waves' deliveries will hit the identical refusal. Continuing is doing expensive work
whose delivery is already known to be impossible.

### The decision

Delivery is the one moment in a waved run when the two branches are *supposed* to agree — that is what
delivering means. Letting them diverge again immediately afterwards is the surprising state, not the safe
one. The narrow fix is to refresh the plan branch **only when the delivery was not a fast-forward**: an FF
result is itself proof the user's branch did not move, so the reverse merge is provably a no-op and can be
skipped with no probe of its own. It costs nothing in the quiet case and fires exactly when divergence is
real. See the `d39-post-delivery-refresh` question.

**The hazard, and it is the same one design 40 just found.** A reverse merge admits into the run's tree
content **no task authored**. The next wave's exit gate then runs over that content, and if a teammate's
commit is broken, the gate fails and the failure lands on the wave — which did nothing wrong. This is
character-for-character §5a of design 40, where `supply` puts an unauthored file onto the base and the
downstream gates cannot attribute it.

Two designs, arrived at from opposite directions, need the same record: **what is in this tree that no task
authored?** Design 40 §4 specifies it for supplied files. If both are built, it should be built **once**,
with the refresh as a second provenance kind — and a wave-gate failure over a refreshed tree must be able to
say *"this tree includes a refresh from `<branch>` at `<sha>`"* rather than blaming the wave. Neither design
can retrofit it: in both cases the information exists only at the instant of the commit.

### How a refresh is recorded (post-plan-40 refinement)

> **Status: under review in round 4 (`d39-refresh-record`).** Written 2026-09-13, after plan 40 (#373,
> PR #711) shipped the `supplied[]` record the paragraph above could only anticipate. It decides what "built
> once" means now that one of the two records exists. Tasks 14, 15, 20 and 24 through 27 already encode it,
> so treat it as a proposal until that question is answered.

**DECIDED (architect, unreviewed): a sibling `refreshed[]` section, NOT a `kind` on `supplied[]`.** What is
built once is the provenance *contract* and its *reader*, not the record type:

- **One contract.** Both sections are optional, append-only, top-level `run.json` sections, absent (never null)
  when empty, written only after their commit exists. Both commits carry a trailer derived from the record
  plus `Guardrails-Run: <runId>` (the journal's run id).
- **One reader.** `UnauthoredContentNote` is the only code that answers *"what is in this tree that no task
  authored?"*, and it reads BOTH sections. A consumer that reads one of them is the defect this rule exists to
  prevent.

**Why not a `kind` on the shipped record.** Plan 40 shipped a *supplier*-shaped record, and each field that
makes it one is wrong for a refresh. `by` names who called `guardrails supply` (`operator` | `overwatcher` |
`task:<folder>`); a refresh has no caller — the harness performs it and the content comes from a branch. The
trailer `Supplied-By: <by>` is derived from `by`, so it would be a false statement on a refresh commit. `bytes`
has no honest value for a merge, which deletes as well as adds. SSOT §7 defines the entry grain as *"one entry
per DRAIN of the staging tree"*, and a refresh has no staging tree. A `kind` would make every field's meaning
depend on another field — the internally inconsistent record #538 removed from `guardrail-failed` — and every
existing `supplied[]` reader would silently count a refresh as a supply. (The shape is unreleased — no tag
contains `5b2b0bbf` — so this rejection rests on semantics, not on compatibility.)

**The record.**

```jsonc
"refreshed": [
  {
    "at": "2026-09-13T10:02:11+00:00",
    "commit": "7e1d…",                    // the refresh merge commit on the plan branch
    "from": "master",                     // the delivery target pinned at run start (IntegrationHandle.OriginalBranch)
    "upstream": "4c9a…",                  // the sha of <from> that was merged — <commit>^2
    "deliveredWave": "wave-02-issue-510", // the wave whose non-fast-forward delivery triggered it
    "paths": ["src/Teammate.cs"]          // git diff --name-only <commit>^1 <commit>, forward-slash, ordinal-sorted
  }
]
```

The write path is `RunJournal.RecordRefreshed(RefreshedRecord)`, mirroring `RecordSupplied` (null-check, lock,
append, persist).

**The commit, and why its shape is load-bearing.** The refresh is a `git merge --no-ff --no-verify` of the
`upstream` **sha** (never the branch name, so the record names exactly what was merged even if the branch moves
in between) in the integration worktree, with the message `Refreshed-From: <from>` / `Guardrails-Run: <runId>`
— the same shape as the supply drain commit.

1. **Its first parent is the plan branch's pre-refresh tip.** A fast-forward of the plan branch onto the
   delivered commit would be cheaper, but whether it keeps the spine intact depends on that commit's parent
   order, which §1 does not fix. If its first parent is the user's tip, earlier waves' task commits leave the
   plan branch's `--first-parent` spine, and `SafeSuffixEvaluator` answers a later rewind with "nothing to
   rewind" while those commits are still in the tree. With `--no-ff`, the user's commits sit on merge lineage,
   and a rewind across the refresh is refused — the honest floor.
2. **The trailer is written.** It keeps the refresh attributable from git alone if the process dies between
   the commit and the journal write, and it travels to the user's branch with the next delivery, where
   `git log` is the only provenance a teammate has. It is `Refreshed-From:`, never `Supplied-By:` — nothing was
   supplied.

The upstream already contains everything the plan branch delivered, so the merge is conflict-free by
construction. A refresh that fails anyway is an infrastructure fault — an honest halt through the #150 path,
no record written — never a silent continue on the stale base this section set out to remove.

**The trigger, corrected for §1's trial merge.** *"Refresh only when the delivery was not a fast-forward"*
cannot be read off the delivery's last step: under §1 the promotion is ALWAYS a fast-forward to
`refs/guardrails/trial/<waveDir>`, so a check on it would never refresh. The discriminator is ancestry at
delivery time — refresh iff the user's branch tip was NOT an ancestor of the plan-branch tip, which is exactly
when the trial merge had to create a merge commit. The trial merge already has to know this to choose between
a fast-forward trial and a merge commit, so the refresh reads that fact from the trial result
(`d39-trial-delivery-primitive`) rather than probing again. That keeps what the `d39-post-delivery-refresh`
answer relied on, a trigger that costs nothing in the quiet case; only the signal moved, from the promotion's
result to the trial merge's.

**The gate halt names it — in scope for v1.** A record nothing reads at the moment of failure does not stop a
wave being blamed. The seam is `Scheduler.BuildGateHalt`, which already builds BOTH the wave entry-preflight
and exit-gate halts. It appends `UnauthoredContentNote`'s headline suffix — every `supplied[]` and
`refreshed[]` record, oldest first, a refresh as `refresh from '<from>' at <upstream, 10 chars>` and a supply
as `supplied by <by> at <commit, 10 chars>` — plus one detail line per record. The failing check names stay
first: the disclosure is added, never substituted. The headline already flows through `RecordGateHalt` into
`run.json`'s `halt.headline` and from there into the log-site banner, so console, journal and log site carry it
with no `RunHalt` schema change and no CLI change. A run with neither section renders byte-identically to today.
It names every record in the run, not only those since the last passing gate: every one of them IS in the
tree, so the sentence stays true; narrowing the window needs the gated commit recorded and is a later
refinement.

**Not in v1:** a live observer line for the refresh (`WaveDelivered` already marks the moment); the disclosure
at the plan-level terminal gate (a CLI phase) or in task retry feedback; and `SafeSuffixEvaluator`'s refusal
wording, which still calls any trailer-less commit "a human hand-fix?" — refresh and supply commits included.

---

### The compensating control already exists: the wave ENTRY preflight (round 3)

The reviewer's follow-up is a better answer than the paragraph above, and it changes the recommendation:

> But each wave also has its own pre-flight checks. Right? Therefore, post-delivery waves should provide the
> types of checks (like "all tests are passing") that are typically in the pre-flight checks of a full
> harness run.

**They do, and it is the right seam.** `Scheduler.RunWaveEntryGateAsync` (`:1391`) runs a wave's entry
preflight against the plan-branch HEAD — *"the materialized prior wave"* — per SSOT §14.3. Nothing new has
to be invented.

**It fixes the ATTRIBUTION problem, not merely the detection one**, which is why it beats the refresh
argument on its own terms. Unauthored content in the tree — from a refresh, or just from a stale base —
that is broken will fail the wave's EXIT gate, and the failure lands on a wave that did nothing wrong.
Asserting the baseline at wave ENTRY makes the identical defect fail before any task runs, where it reads
correctly as *"the tree was already red on arrival"*. That is #181/#182's positive-baseline archetype —
never build on red — applied at the wave barrier instead of only at plan start.

So the refresh question changes shape. It stops being *"is admitting unauthored content safe?"* and becomes
*"a refresh is safe **because** the next wave re-verifies its own baseline before spending anything."* The
recommendation on `d39-post-delivery-refresh` stands, and this is what makes it defensible.

**Two things have to change for it to actually work, and both are cheap.**

**(1) The entry gate is SKIP-ONCE, and that is wrong for a positive baseline.** Verbatim from `:1385`: *"a
passed entry marker for this wave is not re-evaluated on resume (a negative-baseline entry check runs
exactly once)"*. Correct for what it was built for — a TDD-red baseline asserting the thing does not exist
yet is a fact about a *moment*, and re-running it after the work is done would fail.

A positive baseline is the opposite animal. *"All tests pass"* is a fact about the tree **as it is now**,
and after a delivery or a refresh the tree has changed — so skip-once would pass a wave over a tree it never
checked, silently. The asymmetry is already in the model and points the other way: the wave **exit** gate is
*"always re-evaluated on the current HEAD"* (SSOT §14.6); entry is not. This proposal lands on the side that
skips.

So the entry gate has to distinguish the two baseline kinds — **a positive baseline re-evaluates; a negative
one keeps skip-once.** That is the whole change, and it is the only place in this design where the harness
must grow a new distinction rather than reuse one.

**REOPENED (review, 2026-09-13; open: `d39-entry-baseline-kind`).** The three paragraphs above assume a run
can reach a passed entry marker whose tree a delivery has since changed. Checked against `RunWavedAsync`, no
run reaches that state:

- A delivery and its refresh run at the delivering wave's own barrier, before the next wave's entry gate runs
  for the first time.
- A resume skips completed waves and re-delivers nothing.
- A refused delivery leaves its wave incomplete (§4), so a resume re-attempts it after that wave's exit gate,
  still before the next wave's entry gate.
- Every rewind or reset that re-runs a wave clears the entry markers of that wave and every later one
  (`ResetWaveToPending`, `Scheduler.cs:1606-1613`).

So skip-once never skips a check over a tree a delivery changed, and task 14's
`AnEntryGateFailureOverARefreshedTree_NamesTheRefresh` already fails if a refresh ever lands after the next
wave's entry gate. Re-evaluating EVERY entry check instead is ruled out by SSOT §14.6: many entry checks are
negative baselines, true only at the wave's start, and would false-red existing waved plans on every resume.
What stays true is point (2) below: the capability is worth nothing unless the preflight is emitted, and
GR2078 (a post-delivery wave with no entry preflight) cannot tell a positive preflight from a negative one.

**(2) A wave with no authored preflights returns `Pass` immediately** (`:1394`). The capability is worth
nothing unless the check is emitted, so this is a `plan-breakdown` rule as much as a harness one: **a wave
that follows a delivery point gets a positive-baseline entry preflight over the touched areas**, on the same
`$baselineArea` machinery Step 5 already has for plan-level preflights. Step 7's report should name which
waves got one and why, exactly as §1b asks it to name which waves deliver.

**DECIDED (review): `plan-breakdown` emits one for every post-delivery wave.** So the authoring rule is
settled.

**DECIDED (review): a new WARNING code — GR2078 — names the gap without blocking the plan.** A
post-delivery wave with no entry preflight is reported by `validate` and does not fail it.

Why a warning and not an error, recorded because the softer option is the one that looks like a
compromise and is not: an ERROR would fail plans that are **correct-but-unguarded**, and a wave whose
author deliberately skipped the baseline for a good reason would have no way to say so. #181's own
worth-it gate exists precisely because a *false* baseline is worse than none — a check that runs zero
tests, or asserts "0 failed" over an empty set, certifies nothing while looking like a gate. An ERROR
here would push authors toward exactly that. The warning is also not toothless in this repo's practice:
`plan-breakdown` Step 7.1 treats every `validate` WARNING as a **fired trigger** that must be fixed or
documented with a reason, so an unguarded post-delivery wave cannot pass review silently.

**The reasoning that got here is worth keeping, because the premise it started from was wrong.** As
written
above this is an instruction with no gate behind it, which is this repo's most-repeated defect shape. The
reviewer's instinct was that it should carry the same requirement as a plan-level preflight; checking that
premise turned out to matter, because **plan-level preflights are not deterministically required either**.
Nothing in `DiagnosticCodes.cs` requires a preflight to EXIST: `GR2027` is a malformed `catches:` line and
`GR2028` is the terminal gate's integration re-run. The #181 baseline is an authoring rule with a worth-it
gate and a stated skip reason.

So "be consistent with the plan level" resolves to "stay an authoring rule", and the interesting question is
whether a wave deserves a **higher** bar than the plan. The argument that it does is about blast radius, not
importance: a missing plan-level baseline costs slow attribution on a run someone is usually watching, while
a missing post-delivery wave baseline lets a wave build on a tree a refresh just changed and surfaces at
that wave's EXIT gate — **blaming a wave that did nothing wrong**, which is precisely the attribution defect
this section exists to prevent, reappearing because the control against it was optional.

---

## 2. The cost, and it is a DOCTRINE change

`plan-breakdown` says today:

> **Do NOT wave a flat plan** — fine-grained parallelism is a task DAG inside ONE wave, not multiple waves
> (waves are the COARSE ordering for stages whose downstream tasks can't be authored until the upstream is
> real; a wave barrier destroys cross-wave parallelism, SSOT §14 C5).

Plan 25's three chains are **independent**. They can run in parallel today. Putting them in three waves
**serializes them**, and that is a real loss.

**DECIDED (review): the loss is accepted, and the wave barrier STAYS.** The reviewer's words:

> I'm willing to live with the wave limitation of only running serially, as syncing to master and merging
> them in, leans us toward serial working for our worktree/branches anyhow.

That is the load-bearing half — the parallelism being given up is smaller in practice than it looks on
paper, because the sync-and-merge model already pushes this repo's worktree/branch work toward serial.

**This design does NOT propose cross-wave parallelism**, and a later reader should not mistake the
paragraph above for an argument that it should. Asked directly in review, the objections were: more race
surface, a task table that stops reading as a DAG, and a live-status diagram that cannot show what is
actually running. All three hold. The fourth, which is the one that settles it: cross-wave parallelism
would destroy the single property the barrier buys — **a wave's exit gate runs on a QUIESCENT tree**, and
there is no such moment if a later wave is already in flight. The gate is the whole feature; the barrier is
what makes it mean anything.

**What is and is not lost.** A wave still contains an ordinary task DAG, so #510's four tasks keep their
internal parallelism. What goes is parallelism *across issues*.

**So this proposes a SECOND legitimate reason to wave**, alongside the existing one:

| reason to wave | why |
|---|---|
| *(existing)* the stage is **undesignable up front** — downstream tasks reference artifacts an upstream stage materializes | JIT breakdown at the barrier |
| ***(new)*** the stage is a **delivery unit** — you want it on your branch before the rest of the plan finishes | delivery granularity |

Both are coarse ordering; the second buys delivery instead of authorability. The doctrine sentence changes
from *"do not wave a flat plan"* to *"do not wave a flat plan **for parallelism** — wave it when you want
the stages delivered separately."*

**DECIDED (review round 2): YES.** The maintainer's answer — *"waves mean coarse ordering, and delivery is
as good a reason as authorability."* So `plan-breakdown`'s wave/flat fork gains delivery granularity as a
first-class reason to wave, and the doctrine sentence is reworded from *"do not wave a flat plan"* to *"do
not wave a flat plan **for parallelism** — wave it when you want the stages delivered separately."*

The cost stands and is accepted: waving independent chains serializes them. What is bought is that a
failure in one stage no longer strands the finished work of the others.

---

## 3. What still has to be built

Much smaller than the first draft:

- **The delivery path becomes callable at a wave barrier**, gated on that wave's `Exit` being green
  against the TRIAL MERGE (§1) — not only at run end. The run-end call stays for the last wave and for
  every flat plan. **The entry point is `Scheduler.Finalize` → `DeliverToUserBranch` →
  `IWorktreeProvider.MergePlanBranchIntoUserBranch`** — corrected at review, 2026-09-11: there is no method
  called `DeliverAndCleanup` anywhere in the tree (`grep -rn DeliverAndCleanup src/ tests/` exits 1). The
  name entered at charter review and propagated; grep `Scheduler.cs` for `DeliverToUserBranch` rather than
  trusting any name in this document.
- **Delivery is opt-in per WAVE** (`delivers: true` in the wave's `brief.md` front matter, default **false**) — §1b. A
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
  "wave-01-shared-dto": { "status": "completed" },            // delivers: false, so no "delivered" key
  "wave-02-issue-510":  { "status": "completed",
    "delivered": { "status": "delivered", "startedAt": "…", "at": "…", "commit": "…",
                   "outcome": "fast-forwarded", "covers": ["wave-01-shared-dto", "wave-02-issue-510"] } },
  "wave-03-issue-511":  { "status": "needs-human",
    "delivered": { "status": "refused", "startedAt": "…", "at": "…", "outcome": "branch-moved",
                   "detail": "run started on 'master'; HEAD is now 'spike'", "covers": ["wave-03-issue-511"] } }
}
```

**The record, pinned at review (2026-09-13).** §5 required the delivery to be journaled `status: running`
before the merge but never said where that status lives, and this example used `succeeded`/`failed`, which
are not wave status tokens. Every record carries `startedAt` and `covers`, and ends in one of three settled
states:

- `delivered`, with `commit` (the user's branch tip after promotion);
- `refused`, with `outcome` and `detail`: `conflict` or `hook-rejected` when the trial merge could not be built
  (review round 4, `d39-trial-delivery-primitive`), `branch-moved` or `dirty-working-tree` when the promotion
  refused;
- `suppressed`, when the §1a interlock held it, with `detail` naming the decision and its subject.

`status: "running"` precedes the one write the operator can see, `PromoteTrialDelivery` (#625), and is then
replaced by `delivered` or `refused`. A delivery that never reaches promotion — suppressed by the interlock, or
refused because the trial could not be built — writes nothing the operator can see, so its record is written
already settled. `covers` lists every wave the delivery carries, computed from the journal so a resume computes
the same set. A wave whose delivery never began — it never reached its barrier, or its exit gate failed — has
no `delivered` key. The absence means that wave's work is not on the user's branch; the report reads the wave's
own status to say whether it is *held* or *not reached*.

**A refused wave delivery halts the run at that wave — DECIDED (architect, extending
`d39-branchmoved-midrun`).** The answered question covered `branch-moved`. A `conflict` repeats at every later
delivery for the same reason, and `dirty-working-tree` or `hook-rejected` needs an operator action before
anything can land. So every refusal halts with its own `WaveHaltKind.DeliveryRefused`. It never reuses
`ExitGateFailed`, whose console label would say a gate failed over a wave whose every check passed. The wave's
marker commit and `completed` status are written only after its delivery settles, so a resume re-attempts a
refused delivery at that wave's barrier instead of silently postponing it to the next delivery point. The
durable record is the wave's `delivered` entry. The top-level `halt` section stays scoped to gates (#432),
which is how an end-of-run refusal is recorded today.

**`run.json`'s top-level `delivery` is wrong on a partial run today (open: `d39-partial-delivery-record`).**
`RunCommand.DescribeDelivery` (`RunCommand.cs:2045-2097`) derives it only from the end-of-run merge, and a
wave gate or barrier halt returns before `Finalize` (`Scheduler.cs:907-912`, `946-954`, `964-969`). When
wave 02 delivers and wave 03 halts, the #542 record says `delivered: false`, `not-attempted`, "the run was not
wholly green", while wave 02 is on the user's branch. Whatever the answer, the report's source is a
`RunReport.WaveDeliveries` map stamped from the journal in `BuildReport`, the one method every report passes
through, halted or not.

**Not in v1:** a log-site banner for a refused delivery. The log site's halt banner reads only `halt`, so a
refusal at a barrier, like an end-of-run refusal today, is visible on the console and in `run.json` but not
on the log site.

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

**Schema** — a wave's `brief.md` front matter gains `delivers` (bool, default false; §1b); `run.json`'s `waves.<dir>` gains
`delivered`, the §4 record (`status`, `startedAt`, `at`, `commit`, `outcome`, `detail`, `covers`), absent until
the wave's delivery begins. `covers` names every wave the delivery carries, ending with the delivering wave, so
the report can say what a merge actually carried. No new folder.

**Provenance** *(post-plan-40 refinement, NOT yet reviewed in Charter — §1c "How a refresh is recorded")* —
`run.json` gains `refreshed[]` (`at`, `commit`, `from`, `upstream`, `deliveredWave`, `paths`), a sibling of
plan 40's `supplied[]`, which is unchanged; `RunJournal.RecordRefreshed` is its write path. The refresh commit
is `--no-ff` with the plan-branch tip as first parent and carries `Refreshed-From: <from>` /
`Guardrails-Run: <runId>`. `UnauthoredContentNote` is the single reader of both sections, and
`Scheduler.BuildGateHalt` appends its disclosure to wave entry-gate and exit-gate halt headlines. No new
diagnostic, no new observer event, no `RunHalt` schema change.

**New diagnostics** — TWO, not four (corrected at review: this line said "one" while §1c's DECIDED
answer had already added the second). **GR2079**, a WARNING when a wave sets `delivers: true` and carries
no `guardrails/` exit gate, so an author learns that wave cannot deliver rather than discovering it by its
absence from the report; and **GR2078**, a WARNING when a wave that FOLLOWS a delivery point carries no
entry preflight (§1c). Both are warnings and neither moves the exit code.

**Harness** — the `Finalize` → `DeliverToUserBranch` path callable at the barrier (against the §1 trial merge); `IRunObserver.WaveDelivered`, forwarded through
every decorator (the `ObserverForwardingSweepTests` contract); the delivery journaled with `status: running`
before the merge, per the #625 rule.

**Wiring and halts (added at review, 2026-09-13).** The bullet above required the event and the journal
write but gave neither an owner, so the first breakdown built both and wired neither (the #120 shape). The
Scheduler writes `waves.<dir>.delivered` around every barrier delivery (§4) and raises
`IRunObserver.WaveDelivered(WaveNode, WaveDeliveredRecord)` only for `status: delivered`, after the record is
persisted, so an observer never sees a result the journal does not hold. `BuildReport` stamps
`RunReport.WaveDeliveries` from the journal on every report, halted ones included. A refused delivery halts
with `WaveHaltKind.DeliveryRefused`; no `RunHaltKind` is added. The provider members the trial merge needs
are open (`d39-trial-delivery-primitive`, §1).

**Skills** — `plan-breakdown`'s §0 wave/flat fork gains the second reason to wave (§2), and the Step 7
report names which waves are delivery units.

---

## 6. Devil's-advocate self-critique

**"You are pushing authors to wave plans the doctrine says not to wave."** Yes, and §2 says so plainly
rather than burying it. The honest framing is that the existing sentence conflates two different reasons
not to wave, and only one of them (parallelism) applies here. If the reviewer thinks the parallelism loss
outweighs delivery granularity, the answer is to reject this and keep #525 open rather than to build the
first draft's parallel-group machinery — which had all the same problems *plus* a new schema.

**"A wave barrier is a synchronization point, so per-wave delivery makes runs slower AND more complex."**
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
error as draft 1: taking an existing mechanism and assuming its shape carries over. The cheap defense is
the one that caught it — read the code you are claiming to reuse.

**What I got wrong in draft 1, since it is the useful part.** I designed a parallel-group delivery model
without asking whether the ordered-stage model already had the seam. Every piece I invented —
`deliveryGroups`, `deliveryGates/`, `deliverAfter`, the cross-group collision lint — has a wave equivalent
that already ships, is already validated, and is already journaled. The reviewer found it with one
question. The lesson generalizes past this design: **when a feature needs "a subset of the plan, a gate
over it, and an order", check the wave model before inventing a second one.**

---

Refs #525, #340 (mergeOnSuccess default-on), #175 (the merge hazard waves already order around), #625 (the
journal-the-start rule §5 adopts), SSOT §14 (waves), §14.6 (the wave exit gate), §5.3 / §3.3.
