# 21 — Run-boundary fault triage: classifying an abort instead of guessing at it (#453) — design of record

> **Status: DRAFT design-of-record. Not implemented.** This document is reviewed in **Charter**, never as
> a draft PR: a PR is a code-review vehicle, and opening one for prose spends a 3-OS matrix to put a green
> tick on something it certifies nothing about. (#106 predates Charter and recorded the older draft-PR
> habit; the design document itself is still committed here, and a PR is still correct for the
> implementation that follows.) Issue: **#453**. Companion (not blocking, not blocked): **#454** (drafts
> folder + permissioned `guardrails file-issues`). This document does not close either.

**One-line statement.** The harness has exactly one place where its own explanation of what happened is a
**disjunctive guess** — the generic infrastructure-fault abort — and that is the only place a run-boundary
triage step fires. It reads evidence the harness captures for it, classifies **harness-bug | environment |
plan-authoring | unclassified**, drafts an issue for the first of those, and is **strictly additive**: the
abort's headline, remedy and exit code are byte-identical with and without it.

**The headline decision, stated up front because it is the one to argue with:** the trigger is **not** "a
run ended badly". It is **"the abort's remedy was not authored for this specific fault"** — a mechanical
property, opt-in per abort site, defaulting to off. Every future *typed* abort (one that names its own
cause) is therefore excluded **by construction**, and the feature's domain **shrinks** every time the
harness learns to name a fault. A post-mortem judge that made typing faults less urgent would be a
regression; this one cannot, because typing a fault removes it from the judge's reach.

---

## What's being asked

Issue #453 records a manual loop. Every time a run dies, the operator pastes the terminal output into an
AI session, which reads the abort log, the stack, the journal, the integration worktree and the harness
source, and answers one question: **is this a Guardrails bug, an environment problem, or a defect in how
the plan was authored?** The operator contributes nothing to that relay except transport. This is #269's
own argument for the task boundary, applied to the boundary #269 explicitly does not cover.

The motivating incident, run `aef0591d` (#451): task `02-implement-runner-kind-and-axes` had **passed all
its guardrails**. The fault came afterwards, inside the harness's own merge-and-commit step —
`git commit --no-verify … exited 128: Committing is not possible because you have unmerged files.` No task
was struggling, so no overwatcher trigger matched. 4/9 tasks green, \$48 spent, and the run's own diagnosis
was the generic string the harness prints for every abort:

> This is a harness/environment fault (e.g. an offline or failing git hook on an internal commit, or git
> unavailable), not a task failure — resolve it and re-run to resume.

That guess was **wrong**. The real causes were two Guardrails defects: the AI merge resolver returned
success while leaving unmerged paths, and the plan-root integration guardrail that would have caught it was
excluded from the union re-verify set. "Check your git hook" would have sent the operator nowhere.

### Ambiguity named, and the narrowing

The issue's phrase **"plausibly also a wholly-failed preflight or a terminal-gate failure"** is the
load-bearing ambiguity, and answering it loosely produces a different feature. §2 replaces "a run ended
badly" with a **totality test on the taxonomy**: a trigger is admissible only if `harness-bug |
environment | plan-authoring` has a **correct answer for every instance of it**. A failed preflight or a
failed terminal gate can perfectly well mean *the code under test is wrong* — which is none of the three —
so those triggers would force the judge to answer a question it has no true answer to. That test excludes
them, excludes them for a reason stronger than cost, and is the narrowing everything else rests on. If the
review rejects it, the trigger set has to be re-argued from scratch.

The second ambiguity is **"sibling or new trigger class"**. §1 decides it, and the deciding fact is not
tidiness: `Overwatch.EvaluateAsync` exists to return a control-flow decision to a **retry loop that no
longer exists** at an abort, about a **task that may not exist**, having classified **fix ops this actor
never proposes**.

---

## Placement

| piece | placement |
|---|---|
| The abort funnel + `RunAbort` triage eligibility | **harness** (`Guardrails.Core/Execution/Scheduler.cs`, `RunReport.cs`) |
| Git-state capture on the abort path | **harness** (`IWorktreeProvider` + `GitWorktreeProvider`) |
| `RunFaultTriage` — the read-only judge, its brief and its artifacts | **harness** (`Guardrails.Core/Execution/`) |
| `runFaultTriage` config knob | **harness** + **schema** (SSOT §2) |
| `decisions[]` `boundary: "run"` | **schema** (SSOT §7) — reuses the shipped `DecisionEntry` |
| Run-level log artifacts | **schema** (SSOT §8, run-level section) |
| Abort-block + post-mortem rendering | **harness (CLI)**, design owned by `guardrails-ux` |
| Filing the draft (dedup, GH credentials) | **#454 — NOT v1** |
| Triage on preflight / terminal-gate / wave halts | **excluded, with a stated re-open condition (§2.2)** |
| A dedicated `fault-triage` prompt-runner profile | **deferred** — reuses `ai-triage` (§3.3) |

**No `guardrails validate` change, and no GR code.** Nothing here is decidable before a run: the evidence
is an exception that has already been thrown. **GR2064 stays free.**

---

## Invariants in play

**1 — Deterministic guardrails over prompt-judges; judges never alone.** The judge's classification never
replaces a deterministic output. The abort's `Headline` and `Remedy` — including the honest guess list —
are printed **before** the judge runs and are unchanged by it (§5). The dedup slug that #454 will key on
is computed by the **harness** from the exception signature, never by the judge (§4.3): a prompt must not
own an identity key. And the judge is not alone on the evidence either — the git state it reasons over is
captured deterministically by the harness (§3.2), so a judge that returns nothing still leaves the
operator strictly better off than today.

**3 — Prompt-guardrail verdicts come from verdict files, never CLI exit codes.** Generalized here: the
triage's own `PromptResult.IsError` / exit code is never read as anything. A verdict exists only when a
parseable body was returned; anything else is a **no-verdict**, reported and stood down from (§5.3). The
triage can never change the run's exit code, which stays `HarnessError` for every abort.

**5 — Honest halts; needs-human is a feature.** This is the invariant the whole design is about. The
current message is honest and useless; a confident wrong classification would be useful-looking and
dishonest, which is strictly worse. §5 makes "I could not classify this" a **first-class, cheap, and
informative** outcome — an `unclassified` verdict that publishes its **ruled-out** list is a real
contribution, and it is the only way this feature can beat the guess list without being able to lie.

**2 — Harness is the single writer of merged state.** The judge gets **no write tool at all** (§3.1) and
the harness — not the judge — touches the integration worktree (§3.2). `git status` refreshes `.git/index`;
a "read-only git verb" granted to the judge would put a second actor's writes into the worktree the
harness owns, at the exact moment its state is the evidence.

**6 — Plain files, light setup.** Four plain files under `logs/<runId>/`. No credentials in the run's hot
path, no network call, no new dependency. Drafting is a local write.

---

## 1. Sibling, not a new trigger class

**Decision: a sibling class, `RunFaultTriage`, sharing the overwatcher's substrate but not its type.**

The issue offers both, and "reuse `Overwatch`" is the tempting answer because the substrate genuinely is
shared. It is the wrong answer on four axes, and only the fourth is about tidiness.

**(a) The subject does not exist.** Every signature and every reporting path in `Overwatch` is
parameterized on `TaskNode task`, `int attempt`, and `string taskLogDir`; `Record`, `RecordNoVerdict` and
`EvaluateTerminalAsync` all write `Subject = task.Id` and append to that task's `overwatch.jsonl`. A run
abort has no such subject. In the #451 case the task had **already passed every guardrail** — attributing
the fault to it would be actively misleading. In the `CreateIntegration` abort (Scheduler.cs:184) no task
has been dequeued at all. A `TaskNode?` threaded through those paths is not reuse; it is a second mode
inside a class whose every invariant assumes the first.

**(b) The authority model is inverted.** `Overwatch` exists to return an `OverwatchDecision` —
`Grant | Halt | NoAction` — that the **retry loop consults**. At an abort there is no loop: the worker pool
is terminated, the report is being built, the process is on its way out. The run-boundary actor produces an
**artifact**, not a control-flow decision. Folding it into `EvaluateAsync` would force a return value with
no consumer, and a return value with no consumer is where a future maintainer wires a consumer.

**(c) `OverwatchFixClassifier` does not apply — and this is the sharpest reason.** The question posed is
whether the classifier applies, is bypassed, or needs a sibling. **None of the three: the run-boundary
proposal schema has no fix ops, so there is nothing to classify.** `OverwatchProposal` carries
`fixes[]`, and the classifier's safety property is *"every proposed fix op is classified against the
verdict surface before anything is applied"*. A run-boundary actor proposes an **issue draft** — prose
addressed to a human, applied by nobody. Adding a bypass path inside `Overwatch` would put a hole in
exactly the class whose value is that it has none. A *sibling classifier* would be worse: a classifier
with no ops to classify is ceremony that reads, to the next maintainer, as though a check is happening.

The property the classifier backstops — *diagnose, never edit* — is preserved by the mechanism #452 chose
in preference to the classifier: the **tool profile**. `Read`/`Glob`/`Grep` and nothing else. Structural,
not policed.

**(d) Lifetime.** `Overwatch` is composed by `SchedulerFactory` for the task loop and is threaded a live
`taskLogDir` per fire. The run-boundary actor fires once, at run level, after the pool is torn down.

**What it shares — deliberately, and by reference rather than by copy:**

| shared | why |
|---|---|
| Tool profile `["Read","Glob","Grep"]` | #452's structural "diagnose, never edit". Lifted to one `SupervisoryToolProfile` constant that `Overwatch`, `NeedsHumanTriage` and `RunFaultTriage` all read — three copies of a load-bearing constant is how one of them drifts. |
| `AbortAfterConsecutiveToolDenials = 3` | Same bound, same reasoning (#452): one denial is the desired recovery signal, two shows no adaptation, three is conclusive against a three-tool profile. |
| `journal.AddOverheadCost(result.CostUsd)` **before** any parse | #314/#452: the spend is real whether or not the body parses. |
| `decisions[]` via `DecisionEntry` / `journal.RecordDecision` | The durable audit. New `boundary` value only (§6, edit 3). |
| The `ai-triage` runner profile with fallback resolution | §3.3 — the same class of actor; no new reserved profile. |
| §9.2.1's artifact shape (human report + machine sidecar + draft-only) | A shipped, working pattern (#163). |

---

## 2. The trigger set

### 2.1 The rule, and the one trigger it admits

**A run-boundary triage fires when, and only when, the abort's remedy was not authored for the specific
fault that occurred.**

Mechanically: `RunAbort` gains `bool TriageEligible { get; init; }`, **defaulting to `false`**, set `true`
by exactly one producer — `Scheduler.BuildAbort`, the catch-all that formats an arbitrary escaped
exception. `BuildDefinitionReadAbort` does **not** set it: that abort already names its cause (a transient
definition-file lock) and gives a fault-specific remedy, so there is nothing for a judge to add. Any future
typed abort is excluded automatically, because a developer authoring a specific headline and remedy is, by
that act, declaring the fault understood.

Two consequences worth stating plainly:

- The default is **opt-out-by-default**, which is the fail-safe direction here. The error of a new *typed*
  abort silently paying for a judge is worse than the error of a new typed abort not getting one — because
  a typed abort, by definition, does not need one.
- The feature is **self-limiting**. Every fault the harness learns to name shrinks the triage's domain to
  zero for that fault. This is the design's answer to the strongest objection against it (§7, DA1), and it
  is enforced by a one-line test: `BuildAbort` is the only producer that sets `TriageEligible`.

**Two additional suppressions, both deterministic:**

- **A cancelled run is never triaged** (`cancellationToken.IsCancellationRequested`). The operator caused
  it; there is no fault to explain, and the outer token would insta-kill the child anyway.
- **`runFaultTriage: false`** (or `--no-fault-triage`) suppresses it entirely and silently (§3.4).

### 2.2 What is excluded, and why the reason is not cost

The issue floats a **wholly-failed preflight** and a **terminal-gate failure**. Both are excluded, and the
argument is the taxonomy's **totality**, not the price:

> A trigger is admissible only if `harness-bug | environment | plan-authoring` contains the correct answer
> for **every** instance of that trigger.

An infrastructure fault satisfies this by construction. No task failed a check — the work product is not in
question — so the fault lies in the harness's code, the machine it ran on, or the way the plan was
authored. There is no fourth possibility, which is precisely why the taxonomy has three members.

A **failing gate** breaks it immediately. A terminal gate can fail because *the code is wrong* — which is
the gate **working**, and is none of the three classes. So can a preflight (a red baseline is not a harness
bug, not an environment fault, and not a plan-authoring defect; it is a red baseline). Pointing the judge at
a case whose true answer is outside its answer set is the most reliable way to manufacture a confident wrong
classification — the exact failure mode §5 exists to prevent.

Three corroborating reasons, in descending weight:

1. **A gate failure is already typed.** SSOT §8's gate captures persist `stdout.log`, `stderr.log` and
   `result.json` per check, and the halt record names the failing check and its `logDir`. The harness is
   *not ignorant* there. Triage's value is exactly proportional to the harness's ignorance.
2. **On #477 specifically.** The run drained 20 tasks green, \$115.32, and failed at the terminal gate on a
   wave that was never authored. The answer to "should triage fire here?" is not *"the static check makes
   it redundant"* — `docs/plans/19-producer-coverage.md` is careful to say that reading **(b) reachability**
   is **undecidable** and remains review doctrine permanently, and its **GR2062** one-ahead check is WARN-
   level and needs a recorded `intendedWaves` to fire at all. So a residue survives static checking. It is
   excluded anyway, because at that moment the harness knew exactly which clause failed and had its output
   on disk; what the operator lacked was **not a diagnosis of the failure but a check before the run**,
   which is doc 19's job. Triage would have charged for a post-mortem of a gate that did its job.
3. **Frequency.** An abort is rare and always a defect somewhere. A gate failure is a normal, designed,
   expected outcome. Attaching an AI post-mortem to a working gate teaches the operator that a caught
   defect is an anomaly — the opposite of what the terminal gate is for.

**Re-open condition, recorded so this is falsifiable:** if operators are observed running the manual
paste-the-terminal-into-an-AI relay after **gate** failures at a comparable rate to aborts, the exclusion is
wrong — but the correct response then is a **gate-boundary** actor with a **gate-appropriate taxonomy**
(which would include "the work is genuinely wrong"), not a widened trigger on this one.

**Also excluded:** the definition-drift halt (§7.2 — itemized old→new hashes; fully typed), and every
`WaveHalt` (an unauthored next wave is the **designed** JIT checkpoint, not a fault).

### 2.3 The trigger set, stated

| condition | triage? |
|---|---|
| `RunAbort` from `Scheduler.BuildAbort` (generic escaped exception) — pool loop, drain, or `CreateIntegration` | **yes** |
| `RunAbort` from `BuildDefinitionReadAbort` (typed, fault-specific remedy) | no |
| any future typed abort | no (opt-in `TriageEligible` defaults false) |
| run cancelled by the operator | no |
| `runFaultTriage: false` | no |
| plan/wave preflight failure, terminal-gate failure, wave halt, definition-drift halt, per-task needs-human | no |

---

## 3. Design

### 3.1 The actor

`Guardrails.Core/Execution/RunFaultTriage.cs` — one public entry point, invoked from the Scheduler's abort
funnel, mirroring `NeedsHumanTriage`'s shape:

```csharp
internal async Task<RunFaultTriageReport?> RunAsync(
    RunAbort abort, PlanDefinition plan, string logsRoot, string? faultTaskId,
    string gitStatePath, RunJournal journal, CancellationToken ct);
```

- Tool profile: the shared `["Read","Glob","Grep"]` constant. No `Bash`. No write tool.
- `AbortAfterConsecutiveToolDenials = 3`.
- `MaxTurns = 20` — a ceiling, not a target; the denial abort cuts the pathological case at ~3 turns.
- `Timeout = 5 minutes`, on a **fresh** `CancellationTokenSource` (§2.1: the run's token may already be
  cancelled by the abort's own `runCts.Cancel()`).
- Spend charged via `journal.AddOverheadCost(result.CostUsd)` **immediately after the runner returns,
  before any parse**.
- Never throws. Every failure path returns a `RunFaultTriageReport` carrying a `NoVerdictReason` (§5.3).

### 3.2 What it reads — and how git state arrives without a Bash dependency

**Decision: the harness captures the git state to a file on the abort path, before invoking. The actor only
reads.** This resolves the tension the issue names, and three independent arguments point the same way.

1. **#452's lesson, applied rather than eroded.** The whole point of that fix was that a shell-form denial
   must not be able to blind the actor. Granting `Bash(git status*)` reintroduces exactly the class of
   failure #452 removed — at the one moment when the operator has already lost a run and is waiting. A
   narrow allowlist does not help: the two refused calls in the #452 evidence were a `python` heredoc and a
   `for` loop, neither of which any sane allowlist anticipates.
2. **`git status` is not read-only against the repository.** It refreshes `.git/index`'s stat cache. Handing
   a second actor a write into the integration worktree — the worktree the harness owns, at the exact moment
   its state is the evidence — strains invariant 2 for no gain. Git access in this codebase already goes
   through one seam (`IWorktreeProvider`); a second, unmediated path is a contract regression.
3. **Durability, which is the argument that would win on its own.** A snapshot the harness writes lands in
   the log tree and outlives the run: the operator sees it, #454's filer can quote it, and a human reading
   the issue six weeks later sees the same bytes the judge saw. A `git status` the judge runs itself exists
   only inside a transcript. The abort's evidence must outlive the run — that is what `abort.log` is for.

**Mechanism.** `IWorktreeProvider` gains one member with a default implementation (matching the
interface's established convention — `UnmergedPaths`, `CurrentPlanBranchTip` and others already do this, so
`FakeWorktreeProvider` and test doubles are untouched):

```csharp
/// Best-effort diagnostic snapshot of the integration worktree for a run-boundary post-mortem (#453).
/// NEVER throws: an unavailable git, a missing worktree, or a failed invocation yields an empty snapshot.
GitFaultState CaptureFaultState(IntegrationHandle? integ) => GitFaultState.Empty;
```

`GitWorktreeProvider` implements it over verbs it already owns — `UnmergedPaths(integ)` (which is *exactly*
the #451 defect's fingerprint), `CurrentPlanBranchTip(integ)`, plus a porcelain status and a `MERGE_HEAD`
existence check. The Scheduler writes the rendered snapshot to `logs/<runId>/abort-gitstate.txt` **before**
invoking the judge, and hands the judge that resolved absolute path.

This capture is **valuable on its own, with no AI at all** — it is precisely the evidence that was missing
in #451 — which is why it is milestone 1 and separately approvable (§8).

**The brief.** Following #452's precedent that a brief carries **resolved absolute paths** and **states the
facts the harness already holds** rather than sending the judge hunting:

- **Inline, not a path:** the exception type, message, and full `abort.Detail` stack. The harness has it in
  hand; making the judge read it back out of a file is the #452 anti-pattern, and `abort.log` is not written
  until the CLI renders (§8, deferred tidy).
- **Resolved absolute paths**, each labelled *may not exist*: `state/run.json`, `state/state.json`,
  `state/merge-conflicts.log`, `logs/<runId>/abort-gitstate.txt`, the plan folder, and — when known — the
  faulting task's log directory.
- **Stated facts:** the run's task-outcome table from the journal (which tasks settled green, which were
  in flight), and the run's mode (serial / worktree), the way the diagnose brief states its attempt history.
- **The faulting task.** The worker loop's catch already has the task in scope; capture
  `_faultTaskId ??= task.Id` beside `_fault ??= ex`. One line, and it is the difference between "somewhere
  in the run" and "in task 02's settle". In #451 that pointer is the whole lead.
- **Guardrails source, honestly conditioned:** *"If the stack frames name files that exist under
  `<workspace>`, read them — this run is Guardrails dogfooding itself. If they do not, classify from the
  stack and the message alone; do not guess at code you cannot see."* No config, no capability flag.

### 3.3 The runner profile

**Reuses the reserved `ai-triage` profile with the shipped fallback resolution. No new reserved profile.**
It is the same class of actor as §9.2.1's terminal triage — a read-only post-mortem diagnostician — and a
fifth reserved name is a schema field, a resolution path, and a documentation entry bought with a
hypothesis. Model tiering (#201) already provides the "use a different model for post-mortems" lever. A
`fault-triage` profile is a two-line addition if evidence ever demands it.

**No runner resolves ⇒ not consulted ⇒ silent** (§9.2's shipped rule): nothing recorded, nothing printed,
abort output byte-identical to today.

### 3.4 Cost, and the one contract this design breaks

One prompt invocation per generic abort. Bounded by the tool profile, the denial fail-fast (3), 20 turns
and a 5-minute timeout — the same envelope as the diagnose, whose observed pathological case was \$0.66.

**`maxCostUsd` does NOT suppress it, and this is a deliberate, disclosed departure from §9.2's rule.**

`maxCostUsd` bounds the run's **work**. The run is over; nothing more will be attempted. Honouring the cap
here has a failure mode that is precisely inverted against the feature's purpose: the runs most likely to
abort — long, expensive, many-task, worktree-mode — are exactly the runs that reach the cap, so the feature
would be **systematically absent from the cases it exists for**. A cap that silently disables the
post-mortem on the runs that most need one is not a safety property.

Because that overruns a declared cap, it is disclosed and it is opt-out-able:

- The spend is still charged to `overheadCostUsd` and appears in the reported total, so the overrun is
  **visible**, never hidden.
- **`runFaultTriage`** (SSOT §2, default `true`) and **`--no-fault-triage`** suppress it entirely. An
  operator who means the cap absolutely has a switch that means it absolutely.
- The abort block states the spend on the line it prints, so the operator sees what the post-mortem cost.

**Known gap, inherited:** a killed child emits no terminal result line, so a denial-aborted or timed-out
triage's cost is under-counted in `overheadCostUsd` — the same property #452 recorded for the diagnose and
the timeout path. Not fixed here; named so it is not rediscovered.

---

## 4. Output

### 4.1 The taxonomy

| class | means | draft? |
|---|---|---|
| `harness-bug` | the fault is in Guardrails' own code path | **yes** (at confidence ≥ moderate) |
| `environment` | git unavailable, a failing hook, disk, auth, a locked file | no — a remedy, not an issue |
| `plan-authoring` | e.g. two same-tier tasks with identical `writeScope` guaranteeing a collision | no — points at the plan |
| `unclassified` | the judge could not reach a defensible answer | no |

`environment` and `plan-authoring` write a **remedy** into the report; neither is an issue against the
Guardrails repo, so neither drafts one. This mirrors §9.2.1 exactly, where only a `guardrails-tool`
diagnosis produces `ghIssueTitle`/`ghIssueBody`.

**Alignment with §9.2.1, noted rather than forked:** `harness-bug` ⊃ `guardrails-tool`, and
`environment` + `plan-authoring` refine `local-repo`. The two taxonomies are consistent, not competing.
Consolidating them into one vocabulary is a v2 tidy (§8), not a v1 fork — and if they are ever observed
drifting apart in meaning, that is a smell to act on.

### 4.2 Artifacts, all under `logs/<runId>/`

| file | written | contents |
|---|---|---|
| `abort-gitstate.txt` | always, when triage-eligible — **before** the judge runs | branch/tip, unmerged paths, `MERGE_HEAD` presence, dirty tracked paths, worktree registry. Deterministic; no AI. |
| `fault-report.md` | whenever the judge returned a verdict | class, confidence, evidence citations, **ruled-out** list, remedy, pointer to the draft |
| `fault-triage.json` | with the report | the machine sidecar the CLI and the log site read |
| `issue-draft.md` | `harness-bug` at confidence ≥ moderate only | #454-shaped frontmatter + the drafted body |

The sidecar exists for the same reason `triage.json` does (#163): the CLI must render the class and the
one-liner **without re-parsing prose**.

```jsonc
// logs/<runId>/fault-triage.json
{
  "faultClass": "harness-bug",         // harness-bug | environment | plan-authoring | unclassified
  "confidence": "moderate",            // high | moderate | low; ABSENT when unclassified
  "summary": "AI merge reported success while leaving unmerged paths in the integration worktree",
  "evidence": [
    "src/Guardrails.Core/Execution/AiMergeResolver.cs:212 — returns Resolved without re-checking UnmergedPaths",
    "abort-gitstate.txt — 2 unmerged paths present at the time of the commit"
  ],
  "ruledOut": [
    "environment — git ran and exited 128 with a semantic error; a missing or hook-blocked git exits differently"
  ],
  "issueDraft": "issue-draft.md"       // ABSENT unless a draft was written
}
```

### 4.3 The draft, and the slug the harness owns

`issue-draft.md` carries the frontmatter #454 named, so its filer can consume it unchanged:

```yaml
---
slug: run-fault-9f2c41ab77d0
title: "AI merge resolver returns success with unmerged paths, aborting the run at the internal commit"
labels: [bug, run-abort]
source: run-fault-triage
run: 2026-08-14T11-25-42Z-6dcb
plan: model-tiering-stage-1
---
```

**The `slug` is computed by the harness, not the judge** — a stable hash over the *normalized fault
signature* (exception type + the ordered Guardrails frames' type-and-method names, line numbers excluded so
an unrelated edit does not change identity). Invariant 1 in miniature: a dedup key is an identity, and a
prompt must not own one. It also gives recurrence detection for free later ("this is the third run this
signature has killed"), which a judge-authored slug never could.

**Location.** v1 writes to `logs/<runId>/issue-draft.md`, a location the harness already owns and
gitignores. It deliberately does **not** create `.guardrails/issues/`: that folder is **#454's contract to
set**, `docs/plans/14-guardrails-folder-convention.md` currently makes `.guardrails/` documented-optional
and not a shipped writable location, and forking the decision here would leave #454 inheriting a location
it did not choose. Repointing is one path constant when #454 lands. Neither issue blocks the other, and the
operator copying one file by hand is already strictly less toil than the relay this replaces.

---

## 5. The honesty requirement

This section is the point of the feature. A confident wrong classification is **strictly worse** than the
current honest guess list, so the design has to make being wrong hard and being uncertain cheap. Five
mechanisms, in descending importance.

### 5.1 Additive, never replacing — and printed in that order

`abort.Headline` and `abort.Remedy` are **unchanged**, and the operator sees them **before** the judge runs.
The Scheduler raises `IRunObserver.RunAborting(RunAbort abort)` at the funnel's entry; the CLI renders the
deterministic facts immediately, then the triage runs, then `Finish` appends the triage block.

Three properties fall out at once: the guess list survives (so the design cannot make the operator worse
off); the operator is never left staring at a silent terminal during a five-minute judge; and the ordering
itself teaches the reader which part is deterministic and which is a judgement.

**The mechanical guard:** a test asserting the abort headline and remedy are **byte-identical with and
without triage**. That is invariant 1 rendered as an assertion.

### 5.2 `unclassified` is a first-class answer that still narrows

The judge is told, in the brief, that `unclassified` is a **correct and expected** outcome and that a wrong
confident answer is the failure mode being guarded against. But `unclassified` is not permitted to be empty:
the report must carry a **ruled-out** list with reasons.

> *Not environment: git ran and exited 128 with a semantic error about repository state; an unavailable git
> or a hook rejection exits differently and would not mention unmerged files.*

That is a real contribution with no positive verdict. It is how "I don't know, here are three things it
might be" becomes "I don't know, but it is not these two, and here is the state that rules them out" —
which beats the guess list without claiming anything untrue. **`ruledOut` is required on every verdict,
including a confident one**: a `harness-bug` classification that cannot say why it is not environmental has
not actually discriminated.

### 5.3 Evidence citations are required, and a claim without one is downgraded

Every classification must cite evidence. A `harness-bug` claim must name a **`file:line` at a frame in the
stack** *and* state the observable that contradicts that code's contract. A verdict whose `evidence[]` is
empty is **parsed as `unclassified`** by the harness — a deterministic downgrade the judge cannot talk its
way past. The judge is also told the anti-bias fact explicitly:

> A Guardrails stack frame proves only where the failure **surfaced**, not where it **originated**.
> `environment` and `plan-authoring` are live hypotheses at every frame.

Without that line, asking a judge that is reading Guardrails source whether this is a Guardrails bug is a
leading question.

### 5.4 Confidence is structured, rendered, and gates the draft

`high | moderate | low`, rendered next to the class in every surface, and the **issue draft is written only
at `harness-bug` + confidence ≥ moderate**. A low-confidence harness-bug hypothesis is worth telling the
operator and not worth putting in an issue tracker.

### 5.5 The verdict is advisory in the strict sense

No exit code changes (every abort stays `HarnessError`). No task verdict changes. No file outside
`logs/<runId>/` is written. Nothing is filed anywhere. **The triage can never itself cause an abort:** every
path is wrapped, and a thrown triage becomes a no-verdict.

### 5.6 No-verdict is surfaced, never silent

Straight from #452: the line is drawn at **whether anything was SPENT**.

- **Not consulted** — no runner resolved, `runFaultTriage: false`, cancelled run, or a non-eligible abort —
  records nothing, prints nothing. Byte-identical to today.
- **Consulted and spent, no verdict** — errored, turn-exhausted, denial-aborted, timed out, or returned an
  unparseable body — records a `decisions[]` entry with `boundary: "run"`, `decision: "no-verdict"`, **and**
  prints one line in the abort block: `fault triage: no verdict — <reason>`. The reason is the **runner's
  own summary** (the runner owns the vendor wording; the harness never re-derives it).

---

## 6. Seams and contracts touched

### Seams

| seam | change |
|---|---|
| `Scheduler` | one private abort funnel — every `Abort =` assignment routes through it. Captures git state, invokes triage, stamps `report.FaultTriage`. A single funnel so a future fifth abort site cannot silently skip it. |
| `RunAbort` | `+ bool TriageEligible { get; init; }` (default `false`; `BuildAbort` is the only producer that sets it) |
| `RunReport` | `+ RunFaultTriageReport? FaultTriage { get; init; }` — the CLI's only input for the triage block, covering the verdict and no-verdict cases both |
| `IWorktreeProvider` | `+ GitFaultState CaptureFaultState(IntegrationHandle? integ)` with a default empty implementation |
| `IRunObserver` | `+ void RunAborting(RunAbort abort)` with a no-op default. **Both on-the-fly decorators must forward it explicitly, and that must be asserted** — an unforwarded call resolves to the empty default and recreates #452's exact bug one layer up. |
| `IPromptRunner` | unchanged — reuses `PromptInvocation.AbortAfterConsecutiveToolDenials` and the `ai-triage` profile |
| `OverwatchFixClassifier` | unchanged, and deliberately not involved (§1c) |

### Schema changes — exact `02-schemas-and-contracts.md` edits

**Edit 1 — §2 run config**, immediately after the `triageAutoFile` line (currently line 182):

```
  "runFaultTriage": true,             // OPTIONAL; run-boundary fault triage on a GENERIC abort (§9.2.2, #453). Default ON = one bounded read-only post-mortem prompt after an abort, DRAFTING only (nothing filed). `false` (or --no-fault-triage) suppresses it entirely; NOT suppressed by maxCostUsd (§9.2.2)
```

**Edit 2 — §5 integration semantics**, appended to the paragraph ending *"An aborted report is failed
regardless of per-task outcomes."* (currently line 1657):

> A **generic** abort — one whose remedy is the catch-all guess list rather than a fault-specific remedy —
> additionally triggers **run-boundary fault triage** (§9.2.2, #453) inside the scheduler before the report
> is returned: the harness captures the integration worktree's git state to
> `logs/<runId>/abort-gitstate.txt` and consults a read-only judge that classifies the fault and drafts an
> issue. It is strictly **additive** — the `Headline`, the `Remedy` and the exit code are byte-identical
> with and without it — and it fires only for a `RunAbort` marked `TriageEligible` (the generic builder
> only; a typed abort that names its own cause is excluded by construction).

**Edit 3 — §7 `decisions[]`**, the `boundary` comment (currently line 2110) and one added example line:

```
      "boundary": "drift",              // drift | wave | task | run — the decision-class discriminator (extensible)
```

```
    // a `run`-boundary entry (§9.2.2, #453): { "boundary": "run", "policy": "prompt", "decision": "no-verdict",
    //   "subject": "(run)", "headline": "fault triage: no verdict — the diagnose runner produced no result" }
```

**Edit 4 — new §9.2.2**, immediately after §9.2.1, titled *"Run-boundary fault triage (issue #453)"*,
carrying: the trigger rule and the `TriageEligible` mechanism (§2); the taxonomy (§4.1); the shared tool
profile / denial bound / overhead charge; the harness-captures-git-state rule and its three reasons (§3.2);
the four artifacts and the sidecar schema (§4.2); the harness-owned slug (§4.3); the additive-never-
replacing rule and the byte-identical guarantee (§5.1); required `evidence[]` and `ruledOut[]`, and the
empty-evidence downgrade to `unclassified` (§5.2–5.3); the confidence gate on drafting (§5.4); the
`maxCostUsd` departure and `runFaultTriage` (§3.4); and the not-consulted / no-verdict split (§5.6).

**Edit 5 — §8, the run-level artifacts section** (after the `autonomy.jsonl` / `escalations/` block): add
the four `logs/<runId>/` artifacts of §4.2 with their write conditions, and note that
`abort-gitstate.txt` is written for **every** triage-eligible abort — including one where no runner
resolves — because it is a deterministic capture, not a judge output.

**Edit 6 — §9.2**: one cross-reference sentence stating that the overwatcher's boundary is the **task** and
the run-fault boundary is §9.2.2's, so the two never fire on the same event.

### Non-schema documents

- `docs/plans/11-overwatcher.md` §2 placement table: one row recording that the **run** boundary is a
  sibling actor in doc 21, not a trigger class here, with the §1 reason in one line.
- `.claude/skills/guardrails-domain-knowledge` — execution-semantics section: what an operator now sees on
  an abort, and that the triage files nothing.

---

## 7. Devil's-advocate self-critique

**DA1 (the strongest). This is a diagnostician for a bug class that should be fixed, not diagnosed.** Every
abort is a harness defect or an environment problem. The #451 abort happened because the AI merge resolver
lied about success and the guardrail that would have caught it was excluded from the re-verify set (#125).
The right response is to fix those and to **type** the fault: turn
`InvalidOperationException: unmerged files` into a specific abort with a precise remedy. That costs nothing
per run, is deterministic, and is invariant 1's actual instruction. An AI post-mortem is a standing per-abort
tax that **relieves the pressure** to do the deterministic work.

*Response — conceded in part, and the concession is built into the design.* Two things it does not cover.
First, the set of untyped faults is **open by construction**: the abort path exists precisely because the
harness cannot enumerate what escapes it. Typing is asymptotic; the tail is permanent. Second — and this is
where the objection changed the design — the pressure point is real, so the trigger was rewritten to make
the tax **self-liquidating**: triage fires only for the *generic* abort (§2.1), so every fault the harness
learns to name is removed from the judge's reach forever. The report is also the **input to typing**: the
brief requires a `harness-bug` verdict to name the fault as a candidate for a typed abort with a proposed
remedy string. And the success metric in §8 is stated in exactly these terms — if the number of distinct
untyped abort signatures is not falling, triage is being used as a substitute for fixing and should be cut.

**DA2. The taxonomy is single-valued and the motivating incident had two causes.** #451 was *two* Guardrails
defects, one of which (a guardrail excluded from the re-verify set) is authoring-shaped but lives in the
harness. A single `faultClass` forces a false choice.

*Response.* The sidecar's `faultClass` is the **primary** class, and it exists to route one decision: does a
draft get written. The report's evidence section is free-form prose and is expected to name several causes;
the draft body carries all of them. `unclassified` remains available when the judge genuinely cannot pick a
primary. What is *not* offered is a multi-valued class field — that would make every downstream consumer
handle a set to serve a case the prose already serves.

**DA3. `unclassified` will be the modal answer and this will be a \$0.50-per-abort no-op.** Plausible, and
not disprovable in advance.

*Response.* This is the v1 bet, and §8 states the evidence that would falsify it in numbers. Note the floor,
though: even at 100% `unclassified`, milestone 1 (`abort-gitstate.txt`, no AI, no spend) is a strict
improvement — it is the evidence that was missing in #451 — and the `ruledOut` requirement means an
`unclassified` verdict still narrows.

**DA4. It fires at the worst moment for latency: the operator is waiting at a dead run.** True.

*Response.* Bounded at 3 turns on the pathological path, 20 turns and 5 minutes on the worst legitimate one;
and §5.1's ordering means the operator has **today's full information before the wait begins**, with the
wait announced. If the wait is still judged unacceptable, the mitigation is a shorter timeout, not a
different design — and that is `guardrails-ux`'s call.

**DA5. A judge reading Guardrails source, asked whether this is a Guardrails bug, will say yes.** A leading
question with a confirmation-shaped answer. In #451 the answer *was* yes, but the prior is not 1.0.

*Response.* Three structural counters rather than a hope: the required `file:line` citation plus a stated
contradicted contract (§5.3), the explicit brief instruction that a Guardrails frame proves only where the
fault *surfaced*, and the required `ruledOut` entry — a judge that cannot say why it is not environmental
has not discriminated and gets downgraded. Residual risk is contained by §5.1: the wrong answer is printed
*beside* the deterministic facts, labelled a judgement, and the run's verdict is untouched.

**DA6. It duplicates §9.2.1, which already classifies and already drafts a GH issue.** Why a third
supervisory actor?

*Response.* Different trigger, different subject, different evidence — and decisively, §9.2.1 **cannot** fire
on the #451 shape, because the task *succeeded*. What it does share is copied deliberately (§1): the report
+ sidecar + draft-only artifact shape, the tool profile, the denial bound, the overhead sink. The taxonomies
are aligned rather than forked (§4.1), and consolidating them is a named v2 tidy.

**DA7. `RunAborting` on `IRunObserver` is a new decorator-forwarding hazard**, of exactly the kind #452 had
to fix.

*Response.* Accepted as a real cost, and paid the way #452 paid it: both on-the-fly decorators forward it
explicitly and a test asserts each does. The alternative — no event, and the abort headline printed only
*after* the judge — sacrifices §5.1's ordering, which is the honesty property this design is built around.

---

## 8. Scope: v1, deferred, and what would tell us v1 was wrong

### v1 — three separately-approvable milestones

**M1 — deterministic capture. No AI, no spend.**
The abort funnel; `RunAbort.TriageEligible`; `_faultTaskId` captured beside `_fault`;
`IWorktreeProvider.CaptureFaultState` + the `GitWorktreeProvider` implementation;
`logs/<runId>/abort-gitstate.txt`; `IRunObserver.RunAborting` and its rendering. Ships value alone — it is
the evidence #451 lacked — and stands even if the review rejects the AI half.

**M2 — the judge.**
`RunFaultTriage`, the brief, `fault-report.md` + `fault-triage.json`, the additive abort-block rendering,
the `decisions[]` `boundary: "run"` entry, the no-verdict surface, the overhead charge, `runFaultTriage` +
`--no-fault-triage`.

**M3 — the draft.**
`issue-draft.md`, the harness-computed slug, the `harness-bug` + confidence ≥ moderate gate.

### Deferred, named — not silently dropped

| deferred | why not v1 |
|---|---|
| **Filing** (#454) — dedup'd, permissioned, GH credentials | Not v1 by design. Drafting has no external side effects; filing needs credentials and a dedup mechanism. #453 does not block on it and it does not block #453. |
| Triage at a **gate** boundary (preflight / terminal gate) | The taxonomy is not total there (§2.2). Would need its own taxonomy and its own actor. Re-open condition recorded in §2.2. |
| A dedicated **`fault-triage`** runner profile | YAGNI (§3.3). Two-line addition if evidence demands. |
| **Consolidating** §9.2.1's `guardrails-tool` / `local-repo` with this taxonomy | A v2 tidy. Aligned already (§4.1); a v1 rewrite of a shipped contract buys nothing. |
| **Cross-run recurrence** ("this signature has killed three runs") | The harness-owned slug makes it nearly free later; nothing in v1 reads across runs. |
| Moving **`abort.log`** into Core | Unrelated churn. The judge gets the fault text inline, which is better than reading it back. |

### Out of scope, permanently

The triage changing the exit code, any task verdict, the abort's `Headline`/`Remedy`, or any file outside
`logs/<runId>/`. Filing anything to any remote from the run's hot path.

### The evidence that would tell us v1 was wrong

- **Under-performance.** Over the first 10 triage-eligible aborts, fewer than half produce a classification
  at confidence ≥ moderate that the operator subsequently agrees with. → the judge is not earning its cost:
  cut M2/M3 and keep M1.
- **Dishonesty.** Any confident classification that is wrong *and* would have sent the operator somewhere
  unproductive. §5.1 contains the damage, but a single instance is evidence the confidence calibration is
  broken; two is grounds to cut. Recorded against the run, not remembered.
- **DA1 confirmed.** The count of **distinct untyped abort signatures** (the §4.3 slug) is flat or rising
  across releases. → triage has become a substitute for typing faults; cut it and type them.
- **Latency.** Operators report interrupting the triage wait. → shorten or make it opt-in.

---

## 9. Testing strategy

Every item below is a test a *correct* implementation passes and at least one *plausible wrong*
implementation fails.

**The honesty invariants** — the ones worth the most:

1. **Byte-identical abort output.** Same fault, triage on and off → the `RUN ABORTED` headline, the remedy
   line and the exit code are byte-identical. Rejects any implementation that "improves" the remedy with
   the judge's answer.
2. **`unclassified` writes no draft**, and `harness-bug` at `confidence: low` writes no draft.
3. **Empty `evidence[]` downgrades to `unclassified`.** A fake runner returning a confident `harness-bug`
   with no citations must not produce a `harness-bug` sidecar or a draft.
4. **Missing `ruledOut[]`** is a no-verdict, not a verdict.

**Trigger discipline:**

5. `BuildDefinitionReadAbort` produces `TriageEligible: false` and no runner is invoked.
6. `BuildAbort` is the **only** producer setting `TriageEligible: true`.
7. A cancelled run is not triaged.
8. Every abort site's report carries the funnel's stamp (one integration test per known abort site: the
   pool-loop fault, the drain fault, and the `CreateIntegration` setup fault).

**#452's precedents, re-asserted here:**

9. The composed brief contains the **resolved absolute** `abort-gitstate.txt` path and the resolved plan
   folder path — the direct regression guard for the never-substituted `<runId>` template.
10. The invocation declares `["Read","Glob","Grep"]`, no `Bash`, no write tool, and
    `AbortAfterConsecutiveToolDenials = 3`.
11. A thrown / errored / unparseable triage yields a `decisions[]` entry with `boundary: "run"`,
    `decision: "no-verdict"`, one printed line, and an unchanged exit code.
12. **Not consulted stays silent**: no runner resolved, or `runFaultTriage: false` → zero `decisions[]`
    entries, zero files, output byte-identical to today.
13. `AddOverheadCost` is called **before** the body is parsed (fake runner: cost present, body garbage →
    cost still journaled).
14. **Both on-the-fly observer decorators forward `RunAborting`** — asserted per decorator.

**The #485 boundary, made mechanical:**

15. `NeedsHumanKinds`' token set and the fault-class token set are **disjoint**. One assertion; it is what
    stops the two vocabularies from quietly merging.

**Git capture:**

16. `CaptureFaultState` returns `GitFaultState.Empty` and throws nothing when the integration handle is
    null (the `CreateIntegration` abort), when git is unavailable, and when the worktree is gone.
17. An integration-worktree fixture left with unmerged paths produces a snapshot naming them — the #451
    fingerprint, asserted.

---

## 10. Relationship to #485 — the boundary, and where they share a frame

SSOT §9.2 (`needsHuman`'s `kind`) records the half of the distinction that has shipped — *"it is the
AGENT's claim, never the harness's judgement… the harness cannot verify which kind a halt is; it records
what was asserted and lets a human adjudicate."* This document supplies the other half. **Note for review:**
the maintainer's brief recalled this boundary as already written into
`docs/plans/18-integration-proof-proximity.md`; it is not there today (that doc mentions neither #453 nor
#485), so the table below is the first place both halves sit together, and doc 18 needs no edit. The
distinction is about **who asserted the thing**:

| | #485 `needsHumanKind` | #453 `faultClass` |
|---|---|---|
| altitude | per **task** | per **run** |
| asserter | the **agent**, as a claim | the **harness's judge**, as a judgement |
| vocabulary | `blocked-work` \| `defective-guardrail` | `harness-bug` \| `environment` \| `plan-authoring` \| `unclassified` |
| verifiability | the harness cannot verify it; it records what was asserted | produced by the harness over harness-captured evidence |
| evidence | #481's requirement: quote the guardrail's claim + the `file:line` refuting it | §5.3's requirement: `file:line` at a stack frame + the contradicted contract |

**What must not be shared.** `needsHumanKind` must never gain `harness-bug`; the fault taxonomy must never
gain `defective-guardrail` — even though `plan-authoring` and `defective-guardrail` overlap semantically. A
shared token is an invitation to merge the two streams, and merging them erases the who-asserted-this
distinction #485 exists to make. Test 15 above enforces disjointness as an assertion rather than a
convention. There is also no shared `kind` column anywhere.

**Where they should sit side by side.** An operator triaging a halted run is asking one question — *what do
I go look at?* — and both answer it. They share **one post-mortem block** whose **first column is
provenance**:

```
Post-mortem
  agent claim       defective-guardrail   task 08 — the guardrail's --filter names 6 clauses; its floor demands 14
                                          -> logs/<runId>/08-.../attempt-3/action-out-fragment.json
  triage judgement  harness-bug (moderate)  run — AI merge reported success with paths still unmerged
                                          -> logs/<runId>/fault-report.md   (draft: logs/<runId>/issue-draft.md)
```

The rule: **the asserter column is never omitted, and the class token always comes from that asserter's own
vocabulary.** Sharing the *frame* is what lets them be read together; keeping the vocabularies disjoint and
the asserter explicit is what keeps them from collapsing into each other. (Exact rendering — the live table,
`--no-ui`, the run summary, `guardrails status`, and the log site — is `guardrails-ux`'s to specify.)

The other genuine shared contract is **#454**: both are draft producers, so both write the same frontmatter
schema and are distinguished by its `source:` field.

---

## 11. Implementation handoff

Sequencing is strict: M1 → M2 → M3. Each is separately reviewable and M1 stands alone.

### M1 — deterministic capture (`guardrails-harness-developer`)

`src/Guardrails.Core/Execution/Scheduler.cs` (abort funnel, `_faultTaskId`, git-state write) ·
`src/Guardrails.Core/Execution/RunReport.cs` (`RunAbort.TriageEligible`, `RunReport.FaultTriage`) ·
`src/Guardrails.Core/Execution/IWorktreeProvider.cs` + `GitWorktreeProvider.cs` (`CaptureFaultState`,
`GitFaultState`) · `src/Guardrails.Core/Execution/IRunObserver.cs` (`RunAborting`) ·
`src/Guardrails.Cli/ConsoleRunObserver.cs`, `Ui/LiveRunObserver.cs`, `Ui/OnTheFlyDiagramObserver.cs`,
`Ui/OnTheFlyLogSiteObserver.cs` (forward it) · `docs/plans/02-schemas-and-contracts.md` (edits 2, 5).

### M2 — the judge (`guardrails-harness-developer`)

`src/Guardrails.Core/Execution/RunFaultTriage.cs` (new) ·
`src/Guardrails.Core/Execution/Overwatch.cs` + `NeedsHumanTriage.cs` (extract the shared
`SupervisoryToolProfile` / denial-threshold constants — behaviour unchanged) ·
`src/Guardrails.Core/Execution/SchedulerFactory.cs` (compose it from the `ai-triage` profile) ·
`src/Guardrails.Core/Model/RunConfig.cs` (`runFaultTriage`) ·
`src/Guardrails.Cli/Commands/RunCommand.cs` (`--no-fault-triage`; render `report.FaultTriage` in the abort
block) · `docs/plans/02-schemas-and-contracts.md` (edits 1, 3, 4, 6) ·
`docs/plans/11-overwatcher.md` (§2 placement row).

### M3 — the draft (`guardrails-harness-developer`)

`src/Guardrails.Core/Execution/RunFaultTriage.cs` (slug + draft writer) ·
`docs/plans/02-schemas-and-contracts.md` (§9.2.2 draft-frontmatter paragraph).

### Tests (`guardrails-test-author`, per milestone)

`tests/Guardrails.Core.Tests/RunFaultTriageTests.cs` (items 1–4, 9–13, 15, and the trigger set) ·
`tests/Guardrails.Cli.Tests/RunFaultTriageRenderingTests.cs` (items 1, 14) ·
`tests/Guardrails.Integration.Tests/` (items 5–8, 16, 17 — real git fixtures, including a worktree left
with unmerged paths).

### UX (`guardrails-ux`)

The abort block's shape and ordering (§5.1), the "triaging the fault…" banner and its cost line, the
shared post-mortem block with #485 (§10), and the log site's run page section. Hands the exact rendering
spec back to `guardrails-harness-developer`.

### Skills (`guardrails-skill-author`, after M2)

`.claude/skills/guardrails-domain-knowledge` — execution semantics: what an operator sees on an abort, and
that the triage files nothing.

---

## 12. Proposed plan-document edits

1. **This document** — `docs/plans/21-run-fault-triage.md`, as the design of record for #453.
2. **`docs/plans/02-schemas-and-contracts.md`** — edits 1–6 of §6, landing with the milestone that
   motivates each (edits 2 and 5 with M1; 1, 3, 4 and 6 with M2; the draft-frontmatter paragraph with M3).
   Invariant 4: no contract moves ahead of, or behind, the change that motivates it.
3. **`docs/plans/11-overwatcher.md` §2** — one placement-table row: the **run** boundary is a sibling actor
   (doc 21), not a trigger class here, because the subject, the authority model and the proposal schema all
   differ (§1).
4. **`docs/plans/03-roadmap.md`** — record the deferrals of §8 as named items, so "triage at a gate
   boundary" and "consolidate the two triage taxonomies" are tracked rather than forgotten.
5. **`docs/plans/README.md`** — index entry for doc 21.

Per #106 this document goes out as a **draft PR** for inline review. No implementation milestone starts
until the review comments are addressed.
