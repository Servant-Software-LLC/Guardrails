---
charter-format-version: 1
---
# 40 — Supplying a missing resource to an in-flight run

Design of record for **issue #373**. Status: **DRAFT — for Charter review.** Not implemented.

---

## What's being asked

A run executes each task in an isolated git worktree **branched from the run's own base** — its
integration-branch lineage, independent of the human's `master`. There is **no supported channel to inject
or correct a resource into an in-flight run**: a file the human adds to `master` mid-run does not propagate
into task worktrees, so the task cannot see it.

## The measured case (Charter wave-4)

Task `02-vendor-mermaid-runtime` needed a vendored `mermaid.min.js`. It **was** pre-vendored and committed
to `master@88f8514`, and the task prompt was reframed to "confirm-present + embed" per #370. At run time
the task still halted `needs-human`: its worktree branched from `690de91` — the wave-3 completion marker on
the **integration branch** — and `88f8514` is **not an ancestor of that lineage**, because each completed
wave was merged to `master` while the run continued on its own branch.

The agent behaved correctly throughout: it refused to stub or fetch, and produced a precise `needs-human`
offering three fixes (copy the file into the worktree; add the source dir to the session's allowed working
dirs; commit the file to the branch base).

**Note the asymmetry that made this confusing**, because it is the thing to fix rather than to explain:

> **Plan-folder edits — task prompts, guardrails — ARE read live from the canonical checkout, but CODE
> artifacts must be on the run's base.**

So the reframed prompt reached the run and the file did not. Two channels, one live and one not, with
nothing telling the operator which is which.

---

## Invariants in play

**2 — Harness is the single writer of merged state.** This is the invariant the whole design turns on. A
"drop the file into the worktree" affordance would put a second writer into a tree the harness owns, at
arbitrary times. The design instead has the operator hand the harness a file and the **harness** place it —
in one commit, on the run's base, at a boundary the harness chooses.

**5 — Honest halts; needs-human is a feature.** The measured halt was *correct* and its message was *good*.
This design does not remove the halt; it makes the halt **resolvable without abandoning the run**. A
needs-human that can only be answered by killing a 20-task run is a feature with a missing half.

**6 — Plain files, light setup.** No daemon, no IPC, no watch loop. The operator runs a command; the
harness reads a directory at a boundary it already stops at.

**Never-weaker.** A run nobody supplies anything to behaves byte-identically to today.

---

## 1. The shape: a staging directory the harness drains at a boundary

**Decision: `guardrails supply <plan> <path>...` copies files into `logs/<runId>/supplied/`, and the
harness commits them onto the run base at the next task boundary.**

```bash
guardrails supply docs/plans/34-charter/ vendor/mermaid.min.js
# staged: vendor/mermaid.min.js -> logs/2026-…-a1b2/supplied/vendor/mermaid.min.js
# the run will pick it up at its next task boundary
```

Why a staging directory and a boundary rather than a direct write:

- **The harness stays the single writer.** `supply` writes only under `logs/<runId>/supplied/`, which no
  task worktree is branched from and no guardrail reads. The harness performs the actual commit.
- **A boundary is a moment when no task is mid-attempt on that base.** Committing to the run base while a
  task's worktree is branched from it is exactly the drift class the worktree model exists to remove.
- **It survives the operator getting the timing wrong.** Files staged at any moment are drained at the next
  boundary; there is no window in which `supply` silently does nothing.

**There are TWO drain boundaries, and the second one is the important one.** My first draft had only the
first, and it would have refused the measured case:

| when the operator supplies | drained at |
|---|---|
| the run is still executing | the next **task boundary** |
| the run has HALTED and exited | **run start**, on the next `guardrails run`, before scheduling |

The measured incident is the second row. Task `02-vendor-mermaid-runtime` settled `needs-human` and **the
run exited** — so at the moment the operator has the file in hand there is no live run to have a task
boundary. A `supply` that required one would refuse precisely when it is needed, which is the shape of a
feature that demos well and helps nobody.

So `supply` is valid against any **resumable** run — one whose journal has unfinished tasks — and the drain
on the resume path happens before the first task is scheduled, which is the cleanest boundary of the two:
nothing is branched from the base yet.

**Path semantics.** The second argument is the path the file must have **in the workspace**, taken from the
staged layout: `logs/<runId>/supplied/vendor/mermaid.min.js` lands at `vendor/mermaid.min.js`. There is no
separate destination argument, because a source/destination pair is one more thing to get wrong and the
staged tree already says it unambiguously.

---

## 2. What the harness does at the boundary

1. If `logs/<runId>/supplied/` is empty, do nothing. (The whole feature is inert for every run that does
   not use it — the never-weaker requirement.)
2. Copy the tree onto the integration worktree, commit with a trailer naming the run and the operator
   action, and record it in the journal (§4).
3. **Announce it.** `SuppliedResourcesCommitted` on `IRunObserver`, forwarded through every decorator, and
   a line in the live table and `--no-ui` output. A run whose base changed underneath it must say so; a
   silent base change is indistinguishable from a harness bug when a later task behaves unexpectedly.

**Tasks already settled are not re-run.** Supplying a file does not invalidate completed work, and a design
that re-ran the DAG on every supply would make the feature unusable on a long plan. §3 covers the task that
actually wanted the file.

---

## 3. Resolving the HALTED task — the half that makes this worth building

Staging a file helps nothing if the task that needed it is already settled `needs-human` and the run has
exited. Two cases:

**(a) The run is still going.** Later tasks branch from the new base and see the file. Nothing else needed.

**(b) The task halted, which is the measured case.** The operator supplies the file and re-arms:

```bash
guardrails supply <plan> vendor/mermaid.min.js
guardrails reset <plan> 02-vendor-mermaid-runtime
guardrails run <plan>          # resume-aware; re-runs only the reset task and its descendants
```

Three existing verbs, in an order that is not obvious and is therefore **printed by the needs-human halt
itself**. The halt already names the three fixes; it should name the one that works and is
copy-pasteable — which is the #431 rule applied to a halt rather than a report.

**DECIDED (review): `--resume` ships as an opt-in shorthand.** The three verbs stay the default, because
`reset` chooses WHICH descendants to re-arm and folding that into `supply` hides a real decision. But a
fixed three-command order is a sequence nobody remembers, so `guardrails supply --resume <plan> <path>`
stages, resets the halted task and resumes in one step. The halt text prints the explicit three-command
form; the shorthand is for the operator who already knows what it does. Both paths must produce the
identical journal and provenance record — a shorthand that took a different code path would be a second
mechanism for one decision, which is the defect §4 exists to prevent.

**DECIDED (review): the overwatcher MAY auto-resolve this, but ONLY at `dial:critical`.** The design's
first draft declined it outright. The reviewer's call is that at the highest dial the operator has already
accepted machine judgement, and this case is mechanical enough to qualify.

The caution that produced the original decline is NOT withdrawn, and is recorded here because whoever
builds this has to carry it: applying the fix is mechanical, but *deciding that the file in the operator's
checkout is the file the task should have* is a judgement, and getting it wrong commits an arbitrary file
to the run's base — which then flows into the terminal gate and into anything reading that tree. So at
every dial BELOW critical the overwatcher **proposes** the sequence and does not run it, and at
`dial:critical` an auto-resolve MUST write the §4 provenance record naming the overwatcher as the supplier
— that record is what makes the decision auditable after the fact rather than indistinguishable from a
task's own work.

**This is adjacent to, but does not breach, the standing `dial:critical` ruling.** The maintainer has
previously FORBIDDEN `dial:critical` combined with `proceed-unreviewed` ("Guardrails without guardrails is
self-defeating"). An auto-resolve here is not `proceed-unreviewed`: it supplies a file and re-arms a task
whose gates then run in full, and nothing is certified that was not verified. The distinction is worth
stating explicitly so a later reader does not treat this as the precedent that erodes that ruling.

---

## 4. Provenance: a supplied file is not the plan's work

The run's own output must not be confusable with what an operator handed it. `run.json` grows:

```jsonc
"supplied": [
  { "at": "2026-…", "commit": "…", "paths": ["vendor/mermaid.min.js"], "bytes": 214_­512 }
]
```

and the commit carries a trailer:

```
Supplied-By-Operator: guardrails supply
Guardrails-Run: 2026-09-05T07-47-36Z-2ada
```

This matters more than it first appears. The terminal gate runs on the merged HEAD; if it fails, the first
question is what is in that tree that the plan did not author. Without a record, a supplied file is
indistinguishable from a task's output, and the #453 fault triage would be reasoning over a tree whose
provenance it cannot recover.

---

## 5. Fix the asymmetry, or at least name it

The confusion that produced #373 is that **plan-folder edits are live and code artifacts are not**. This
design adds a channel for the second; it does not unify them, because unifying them would mean either
freezing plan edits (losing the #568 live-plan-edit capability) or making every code file live (which is
the second-writer hazard invariant 2 forbids).

So the asymmetry stays, and the obligation is to **stop it being a surprise**:

- The `needs-human` halt for a missing file names it explicitly: *"plan-folder edits reach a running plan;
  code artifacts do not — use `guardrails supply`."*
- `guardrails-domain-knowledge` states it as a contract, not a footnote.
- The README's plan-folder section (added in #600) gains one sentence.

An operator who knows the rule loses nothing to it. The measured cost was entirely in not knowing.

---

## 5a. Who may call `supply` — and how an agent finds out it can

`supply` is a CLI command, so **anything that can run a shell can call it** — including a task agent
mid-run. That is not a side effect; it is the JIT case raised in review: an agent authoring a script it
then needs on the base can inject it without ending the run and re-paying a breakdown. Genuinely useful,
and the reason the verb is worth more than an operator convenience.

It is also a **write-scope bypass**, and that has to be settled before it is built rather than after.
`writeScope` is what stops a task editing files it does not own; a task that can call `guardrails supply`
can put any file onto the run's base without its `writeScope` being consulted. Every downstream check —
the wave exit gate, the terminal gate, the #453 triage — then runs over a tree containing content no task
was authorised to produce. This is the same shape as the escape hatches the harness already refuses: it is
why the tool-permission layer will not let an agent write under `.claude/` directly, and why
`needsHarnessWrite` exists as a supervised alternative.

**The caller is detectable, and #442 is why.** `ProcessRunner` merges the harness-owned `GUARDRAILS_*`
namespace **hermetically** into every child (SSOT §5.1): after the merge the child's view of that namespace
is exactly what the harness declared — no inheritance leakage. So a `supply` invoked from inside a task
action sees `GUARDRAILS_STATE_OUT` and `GUARDRAILS_WORKSPACE` set, and one invoked from an operator's own
shell does not. Before #442 that test would have been unreliable in precisely the case that matters (a
harness launched from inside another run); it is reliable now, and the hermetic sweep is what makes it so.

**DECIDED (review): scoped.** A task agent may call `supply`, and may supply only paths inside its own
`writeScope`. That keeps the JIT case the reviewer valued — an agent authoring a script it then needs on
the base almost certainly owns that path already — while closing the bypass for everything else.

**But env detection is a guard against accident, not against an adversary** — say so plainly rather than
letting the mechanism imply more than it delivers, because the scoping rule above rests on it. An agent that can run `guardrails supply` can also run
it with those variables cleared, and the same is true of any scoping rule that has to learn *which* task is
calling from the same channel. So the honest division of labour is:

- **The env check** stops the accidental and the naive case, which is the overwhelming majority of it.
- **The provenance record (§4) is the actual defence**, and this is the argument that promotes it from
  nice-to-have to load-bearing: whatever the policy, the tree ends up carrying files no task authored, and
  *"what is in this tree that no task authored?"* has to be answerable after the fact. §4 is what answers
  it. A reviewer inclined to cut provenance for v1 should read this section first.

### The documentation obligation

If `supply` is agent-callable at all, **an agent has to know it exists**, and the place an agent looks is
not this document. This is the #490 rule applied before the fact: doctrine has to reach the **entry
points**, not only the prose, because the entry point is where the reader actually goes. Three surfaces,
all of them v1 acceptance conditions rather than follow-ups:

- **`guardrails-domain-knowledge`** — the skill packed into the shipped tool, which is what a task agent
  has in context while it is deciding what to do about a file it cannot produce. If the capability is
  described nowhere else, this is the one that matters.
- **The README's command-line section** — and this one is already enforced. `ReadmeCommandCoverageTests`
  (#600) enumerates what the composition root registers and requires `guardrails <verb>` to appear as an
  invocation, so `supply` is covered the day the verb is wired. Its rationale is verbatim the point here:
  *an undocumented command is, for practical purposes, an unshipped one.*
- **The retry-feedback and `needs-human` halt text** — the moment an agent meets the missing file is the
  moment it needs to know there is a channel other than giving up.

The skill surface is the gap: nothing tests it. That is worth knowing when this is built, because the
enforced surface will pass and the unenforced one is the one an agent actually reads.

---

## 6. Seams and contracts touched

**Schema (`02-schemas-and-contracts.md`)**

- §1: `logs/<runId>/supplied/` as a harness-owned staging tree — written by `guardrails supply`, drained
  and deleted by the harness, never read by a guardrail.
- §7 `run.json`: the `supplied[]` section above.
- §8: the observer event.

**New diagnostics** — none. Nothing here is a plan defect; `supply` validates its own arguments and fails
its own invocation.

**New verb**

`guardrails supply <plan> <path>...` — copies into the staging tree, refuses a path outside the workspace
(the `writeScope`-style traversal guard, GR2019's rule applied to a CLI argument), and prints where the file
landed **and at which boundary it will be picked up**.

It refuses only when there is **no resumable run at all** — no journal, or a journal whose every task has
settled. It explicitly does **not** require a run to be executing: the case it exists for is a run that has
already halted and exited (§1), and requiring a live run would refuse it.

**DECIDED (review): a task agent may supply only paths inside its OWN `writeScope`.** An invocation from
inside a task environment is scoped to that task's declared paths and refused outside them; an operator
invocation is unrestricted. The caller is distinguishable because #442 made the `GUARDRAILS_*` namespace
hermetic across the process boundary — see §5a for what that detection does and does not prove.

**Documentation is an acceptance condition, not a follow-up** (§5a): `guardrails-domain-knowledge`, the
README's command-line section, and the `needs-human` halt text. The README surface is already enforced by
`ReadmeCommandCoverageTests` (#600); the skill surface is not, and that is the one an agent reads.

**Harness**

- Drain-and-commit at the task boundary in `Scheduler`.
- `IRunObserver.SuppliedResourcesCommitted`, forwarded through every decorator.
- The needs-human halt for a missing-file refusal gains the three-command sequence.

---

## 7. Devil's-advocate self-critique

**"A staging directory is a worse `git commit`."** For an operator who already knows the run's lineage, yes
— `git commit` onto the integration branch does the same thing. The feature is for the operator who does
*not*, which is everyone the first time; the measured incident is an operator who committed to the right
file to the wrong lineage and could not tell. `supply` cannot target the wrong lineage.

**"The boundary drain means a supplied file may sit unused for the length of a task."** True, and on a
long-running task that is a real wait. The alternative is committing to a base a live worktree is branched
from, which is the exact hazard the worktree model removes. A wait is the correct trade, and the `supply`
output tells the operator to expect it.

**"You are adding a channel for a problem #370 already prevents at breakdown time."** #370 is prevention
and this is recovery; the issue says so, and the measured case is one where prevention *ran and was not
enough* — the file was pre-vendored and the run still could not see it. A prevention with no recovery
channel fails closed onto a killed run.

**"Your first draft refused the case you wrote this for."** It did. `supply` was specified to refuse when
no run was in progress, and the measured incident is a run that had already halted and exited — so the
feature would have been unavailable at the only moment anyone reaches for it. Caught re-reading, not
writing. It is recorded here rather than silently corrected because it is the same class as the thing being
designed: a mechanism that looks complete from the inside and is missing the path its own motivating story
takes.

**"Provenance in run.json is over-engineering for a v1."** It is the cheapest part of the change and the
one that cannot be retrofitted, because the information exists only at the moment of the commit. §4's
argument — that the terminal gate and the #453 triage reason over a tree whose contents they otherwise
cannot attribute — is the one I would defend hardest here.

**The part I was least sure of** was §3(b) requiring three commands, and the review settled it: `--resume`
ships as an opt-in shorthand while the three verbs remain the default and the halt text keeps printing the
explicit form. The explicitness argument was right about `reset`'s descendant choice being a real decision;
it was wrong to conclude that therefore nobody may have a shorthand. The requirement that falls out is
that both paths write the identical journal and provenance — see §3.

---

Refs #373, #370 (breakdown-side resource acquisition — prevention to this recovery), #269 (overwatcher; §3
declines the auto-resolve), #568 (live plan-edit, the capability §5 declines to trade away), #431 (the
copy-pasteable hand-over rule §3 applies to a halt), #453 (the triage §4 keeps honest), #442 (the hermetic
`GUARDRAILS_*` namespace §5a's caller detection rests on), #490 (doctrine reaches the entry points, not
only the prose), #600 (the README coverage test that already enforces one of §5a's three surfaces), SSOT
§1/§3.2/§5.1/§7.

---

## Decisions for this review

:::question
{"id": "d40-resume-ergonomics", "title": "Should resolving a halted task be three commands, or one 'supply --resume'?", "mode": "single", "options": ["Three commands: supply, then reset, then run", "One command: supply --resume stages, resets the halted task, and resumes", "Three by default, with --resume as an opt-in shorthand"], "recommended": "Three commands: supply, then reset, then run", "rationale": "This is the part of the design I am least sure of, and the issue is explicitness versus ergonomics. `reset` chooses WHICH descendants to re-arm, and folding that into `supply` hides a real decision behind a convenience flag. But three commands in a fixed order is a sequence nobody will remember, which is why the design has the needs-human halt print it verbatim. If you think the ergonomics matter more than the explicitness here, the third option is the honest middle and I have no strong argument against it.", "target": "human", "answer": ["Three by default, with --resume as an opt-in shorthand"]}
:::

:::question
{"id": "d40-overwatcher-autoresolve", "title": "Should the overwatcher be allowed to auto-resolve a missing-resource halt in v1?", "mode": "single", "options": ["No — it may PROPOSE the command sequence, never run it", "Yes — the case is mechanical, so let it supply and resume", "Yes, but only at dial:critical"], "recommended": "No — it may PROPOSE the command sequence, never run it", "rationale": "#373 argues this is an ideal auto-correct target because the fix is 'fully mechanical'. Applying it is mechanical; DECIDING that the file in your checkout is the file the task should have is a judgement, and getting it wrong commits an arbitrary file onto the run's base — which then flows into the terminal gate and into anything reading that tree. I would rather v1 sat on the judgement-free side of the dial and earned the auto-resolve later, but you have overruled a similar caution before and this is your call on the autonomy arc.", "target": "human", "answer": ["Yes, but only at dial:critical"]}
:::

:::question
{"id": "d40-asymmetry", "title": "Plan-folder edits reach a running plan and code artifacts do not. Name the asymmetry, or try to unify it?", "mode": "single", "options": ["Name it — in the halt text, the domain-knowledge skill and the README", "Unify by making code artifacts live too", "Unify by freezing plan edits during a run"], "recommended": "Name it — in the halt text, the domain-knowledge skill and the README", "rationale": "This asymmetry is what produced the confusion in #373: the reframed prompt reached the run and the file did not. Unifying it costs something real in either direction — making code live admits a second writer into a tree the harness owns (against invariant 2), and freezing plan edits gives up #568's live plan-edit capability, which you asked for. The measured cost here was entirely in NOT KNOWING the rule, so naming it in the three places a reader meets it may be the whole fix. Flagging it because 'document it' is the answer that is easiest to reach for and hardest to be sure of.", "target": "human", "answer": ["Name it \u2014 in the halt text, the domain-knowledge skill and the README"]}
:::

:::question
{"id": "d40-agent-callable-supply", "title": "May a TASK AGENT call `guardrails supply` mid-run, given it bypasses writeScope?", "mode": "single", "options": ["Scoped: an agent may supply only paths inside its own writeScope", "Operator-only: refuse when invoked from inside a task environment", "Unrestricted: any caller, any path"], "recommended": "Scoped: an agent may supply only paths inside its own writeScope", "rationale": "You called the JIT case out as the thing you love about this, and I agree it is the most valuable use — but `supply` being a CLI command means a task agent can put ANY file onto the run's base without its writeScope being consulted, and every downstream check then runs over a tree containing content no task was authorised to produce. Scoping costs your case nothing: an agent writing a script it needs on the base almost certainly already owns that path, so option 1 keeps the capability and closes the hole. Option 2 is the conservative read and sends the agent back to `needsHuman`, which is exactly the dead end this design exists to remove. I would argue against option 3. Whichever you pick, note that env-based caller detection (reliable since #442 made the GUARDRAILS_* namespace hermetic) stops the accidental case but not a determined one — the provenance record in section 4 is the real defence, which is why 5a promotes it from nice-to-have to load-bearing.", "target": "human", "answer": ["Scoped: an agent may supply only paths inside its own writeScope"]}
:::
