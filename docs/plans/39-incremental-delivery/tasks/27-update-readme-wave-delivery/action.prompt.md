## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `27-update-readme-wave-delivery`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "27-update-readme-wave-delivery": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Update `README.md` for what plan 39 changes about delivery. **Extend the existing
`### Delivery on success` section** (under the command-line reference; grep for the heading rather
than trusting a line number). Do NOT add a parallel section: an operator reads one delivery story, and
two sections that each tell half of it is how one of them goes stale.

Describe what SHIPPED, not the design's rejected alternatives. The sources are design 39
(`docs/plans/39-incremental-delivery.md` §1b, §1a, §1c and §4, as answered in review round 4) and the
SSOT (`docs/plans/02-schemas-and-contracts.md`) as task 20 has just updated it. Where they disagree, the
SSOT and the code win.

Cover these, in the document's existing operator-facing style:

- **Per-wave delivery.** A wave opts in with `delivers: true` in its `brief.md` YAML front matter
  (default false). A delivering wave is a **delivery point**: when its exit gate passes, the plan branch
  as it stands merges into your branch at that wave's barrier, carrying the non-delivering waves before
  it. **The plan's final wave is the exception** (review round 5): say in one sentence that the final wave
  always delivers at the end of the run, after the plan-level terminal gate (`<plan>/guardrails/`) passes,
  so work never lands on your branch ahead of a terminal gate that then fails. Earlier waves deliver
  before that gate can run, so if it fails after they delivered, `run.json` records `partially-delivered`.
  A plan that marks no wave behaves exactly as today — one merge at run end. Say where the flag
  lives plainly: there is no per-wave config file. A `guardrails.json` inside a wave directory is
  silently ignored — the plan stays waved and `validate` does not warn — so an operator who guesses
  that file gets no delivery and no error. **The existing switches apply at every delivery point.** Say
  that `--no-merge-on-success` (or `"mergeOnSuccess": false`) stops a delivery point from delivering
  too, not only the run-end merge, in the sentence that already recommends that flag "whenever you want
  to inspect before anything lands". A task definition edited during the run blocks a delivery point's
  delivery as well (#556), exactly as it blocks the run-end merge.
- **The interlock is wave-scoped, and a held wave's work rides along.** A machine decision that
  suppresses delivery (a proceeded-best-guess or proceeded-unreviewed) is now checked at every delivery
  point, against every wave that delivery carries. A delivery is held when ANY wave it carries recorded
  such a decision, not only the delivering wave, so once a wave is held every later delivery is held too
  until the run ends, unless you force delivery with `--merge-on-success`. Say this in the sentence that
  already explains when delivery is held back, so a reader does not meet two rules, and state it in
  terms of the waves the delivery carries.
- **A refused delivery halts the run at that wave.** A delivery is refused when your checkout has moved
  to another branch, your branch gained commits after the trial merge was built (you kept working while
  the gate ran), the merge conflicts, or your working tree has changes the merge would overwrite. The run
  then halts at that wave instead of running later waves
  whose delivery would be refused the same way. Deliveries that already landed stay on your branch, and
  your checkout is not touched. The wave is not marked complete until its delivery settles, so resuming
  after you fix the cause re-attempts that wave's delivery. Say in one sentence that a refused delivery
  halts the run at that wave. **Give the two branch causes their own remedies**, because they differ: a
  checkout switched to another branch needs that branch checked out again before you resume; a branch
  that advanced after the trial was built needs only a resume, since the next trial includes your new
  commits. Also say that `run.json` records the refusal in `decisions[]` as `delivery-refused`, which the
  console shows, and that the log site shows only the wave as needs-human: there is no log-site panel for
  a refused delivery in this version.
- **A failed exit gate on the trial merge** (review round 5). When the wave's exit gate fails on the trial
  merge with your new commits, the run halts exactly as it does for any failed exit gate — the halt
  banner, `run.json`'s `halt` section and the gate logs — and the headline says the gate failed on the
  merge with your branch, naming your branch tip and a sha-keyed `git log <plan-tip-sha>..<your-tip-sha>`
  range you can run to see which of your commits it merged (it stays accurate after either branch
  moves). `run.json` records that wave's delivery as `trial-gate-failed`.
- **A rejecting hook holds delivery; it does not halt** (review round 5). Say in one sentence that if your
  git hook rejects the trial merge commit, this delivery and every later one wait for the end of the run,
  where the final merge runs your hooks in your own checkout. Say why: a hook that needs untracked
  tooling, such as `node_modules`, can fail in the harness's worktree and pass in yours. If that final
  merge lands, the run is delivered.
- **The merge commit runs your git hooks.** A delivering wave's exit gate runs against a trial merge.
  When your branch has moved on, that merge commit is created with your git hooks, exactly as today's
  run-end merge commit is. Say that in one sentence, and say that hooks installed under a relative
  `core.hooksPath` — the way husky installs them — run too.
- **The partial-delivery report.** A run where an earlier wave delivered and a later wave failed prints
  which waves landed on your branch and which are held on the plan branch, BEFORE the verdict. The exit
  code does not change: a run with a failed wave is still a failed run. `git branch --no-merged` stays
  the confirmation, and the README already points at it — connect the two rather than repeating it.
  `run.json`'s delivery record says the same thing: its outcome is `partially-delivered` with
  `delivered: false`, because `delivered` is true only when all verified work reached your branch. A run
  whose final merge lands after a rejecting hook held its deliveries is delivered, not partially
  delivered: that merge carried every held wave.
- **The post-delivery refresh.** When your branch moved independently between deliveries, the harness
  merges it back into the plan branch after delivering, so later waves build on your new commits. That
  admits content no task authored, so `run.json` records it in `refreshed[]`, and a gate failure over a
  refreshed tree names the refresh instead of blaming the wave.
- **The two new `validate` warnings.** `GR2078`: a wave that follows a delivery point carries no entry
  preflight. `GR2079`: a wave sets `delivers: true` but carries no exit gate, so it cannot deliver. Both
  are warnings and neither moves the exit code.

**Do not write the first breakdown's narrower interlock rule.** An earlier version of this prompt said a
wave delivers only if no suppressing decision was recorded during that wave. A delivery carries every
wave since the last one, so under that rule a clean later wave carries a held wave's commits onto your
branch. The guardrail refuses the narrow rule however it is worded. If you contrast the two, make the
contrast an explicit negation.

**REWORD one existing sentence — it becomes FALSE.** The section currently says *"Nothing is merged on
a run that does **not** reach green: a needs-human halt, a failed gate, or a cancellation leaves your
branch untouched either way."* Once a wave can deliver at its barrier, a later wave's halt no longer
leaves your branch untouched — the earlier delivery has already landed. Rewrite that sentence so it is
true for both flat and waved plans. Do NOT leave it standing and add a qualifier elsewhere: a guardrail
asserts the unqualified claim is GONE, precisely because an append leaves both claims on the page.

**Scope boundary (harness-enforced):** Write only to `README.md`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
