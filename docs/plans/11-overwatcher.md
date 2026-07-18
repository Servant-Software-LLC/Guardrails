# 11 — The Overwatcher (active AI run supervisor) — design of record (issue #269)

> **Status: APPROVED — v1 IMPLEMENTED (issue #269).** The #305 review decisions are baked in (§12): the
> diagnose core is ON by default (B), the action-side-gaming residual is accepted by design (A), the
> trigger set is **EAGER** (`attempt ≥ 2` + the typed transitions, once per attempt, `maxCostUsd`-bounded —
> the maintainer override of C), and the per-task `overwatch.jsonl` detail stream is kept (D). **v1 = the
> active *diagnose* + *propose* supervisor** (§9), shipped in `Guardrails.Core` (`Overwatch`,
> `OverwatchFixClassifier`) + `02-schemas-and-contracts.md` §9.2/§9.2.1/§8. **Bounded auto-heal
> (`auto`-tier silent application of authoring-defect fix classes) and the inter-wave role are v2 bets**
> (§10) that only need the seam defined in v1 — the same seam the #254 multi-wave skeleton already opens
> (roadmap bet #6).

This document is the SSOT-companion for the harness contract summarized in
`02-schemas-and-contracts.md` §9.2. Where the two differ, §9.2 wins for the wire contract; this doc
owns the rationale, the mechanics, and the phasing.

---

## 1. What it is, and the pain it removes

The overwatcher is an **active, tiered, asymmetric AI supervisor** the harness consults *during* a
`guardrails run` when a task struggles. At a struggle boundary it reasons about **"will more attempts
help, or is this structurally doomed?"** and produces one of three decisions:

1. **Grant adjusted attempts** — extend the turn/retry budget and/or inject sharper, failure-specific
   guidance into the next attempt — *but only ever coupled to a sanctioned change* (§5).
2. **Declare doomed → halt with a precise diagnosis** — hand the human "here is exactly why, and here
   are the options," not a bare `needs-human`.
3. **Apply a bounded self-healing fix and resume** — v2 (§10); constrained by the mechanical
   asymmetry (§3).

**The pain it removes** (straight out of dogfooding a real multi-wave plan): today, when a task hits
attempt 2/3 or `needs-human`, a human pauses the run and pulls in an external AI to judge *doomed vs.
retryable*, then relays that verdict back to the harness (kill / fix / resume). The human contributes
**nothing to the initial kickoff** — they are a *manual relay for an AI judgment*. The overwatcher
automates that judgment and renders it inline; a human is needed only for the decisions an AI
genuinely cannot make, and never at the cost of a deterministic guardrail's verdict.

**North star:** long-running, self-healing runs. v1 automates the *judgment* (and halts honestly for
the human to *act*); v2 automates a bounded slice of the *action*.

## 2. Placement and relationship to shipped mechanisms

The overwatcher is **not a new framework** — it is the *active generalization of one shipped thing*
plus the *unification of three fixed policies* under judgment:

- **Subsumes the one-shot triage (§9.2, `NeedsHumanTriage`).** Today an advisory triage fires *once*,
  *post-hoc*, at the terminal `needs-human` exhaustion transition, and writes `feedback.md` +
  `triage.json`. The overwatcher makes that step **active** (fires at multiple trigger transitions,
  §4) and **richer** (classifies doomed-vs-retryable, not just tool-vs-local). The shipped terminal
  triage becomes **one trigger case** of the overwatcher (§9.2.1) — its advisory-never-gates
  invariants are preserved verbatim.
- **Generalizes #94 / #264 / #174 as policies, not fixed rules.** The max-turns auto-escalation (#94),
  the identical-deterministic-script short-circuit (#264), and the no-op-deadlock short-circuit
  (#174/#182) are today hardcoded heuristics. The overwatcher applies them *with judgment* — but the
  deterministic short-circuits **remain the floor** (§8): they can always halt without the judge.
- **Trust-anchored by #260 (shipped).** The review marker now keys on `PlanDefinitionHash`, which
  covers guardrail/preflight/action **bodies**. That is exactly what lets the asymmetry guarantee "a
  machine-touched verdict surface reads as un-reviewed" (§3). Before #260 this gate could not exist.

Placement of the pieces:

| Slice | Placement |
|---|---|
| Active diagnose + propose supervisor (subsumes §9.2) | harness (`Guardrails.Core`) + schema (§9.2 rewrite, §8 `overwatch.jsonl`) + `guardrails-domain-knowledge` |
| Governing knob | **reuses** the shared §2.1 `autonomyPolicy` — **no new field** |
| Reporting | **reuses** the shared §7 `decisions[]` (`boundary:"task"`) + a per-task `overwatch.jsonl` detail stream (§8) |
| Bounded auto-heal (`auto` tier, authoring-defect fix classes) | **v2 bet #6** |
| Inter-wave adjustment | **v2 bet #6, couples to #254** (`10-multi-wave-plans.md` §14.7/§14.9) |
| Auto-applying a guardrail-body change | **out of scope, permanently** — routes to human + `/guardrails-review` + review-marker re-stale |

## 3. The load-bearing constraint and the mechanical asymmetry

**The whole point of Guardrails: self-healing must NEVER soften a deterministic guardrail's verdict.**
So the overwatcher's fix authority is **asymmetric**, and the asymmetry is **mechanical, not vibes** —
a pure classifier the harness (not the LLM) applies.

The overwatcher **proposes typed fix operations**; the harness deterministically classifies each into
one of three authority classes by a **path/field-membership test against the already-shipped
`TaskDefinitionFiles` enumeration** (`Guardrails.Core.Journal.TaskDefinitionFiles`, §7.2/§7.3) —
reused, never reinvented, exactly like `WriteScope.IsInScope` / `WorkspaceContainment.Escapes`.

### 3.1 DENYLIST — the verdict surface — FORBIDDEN to auto-apply at every tier, including `auto`

- Any file under the four guardrail/preflight folders — the guardrail/preflight members of
  `TaskDefinitionFiles`: `tasks/<id>/guardrails/**`, `tasks/<id>/preflights/**`,
  `<plan>/guardrails/**`, `<plan>/preflights/**` (and, in a waved plan, `<plan>/<wave>/guardrails|preflights/**`).
- The `task.json` fields that **drive a deterministic verdict**: `writeScope` (any change — narrowing
  *hides* a §3.4 violation, widening *changes* the checked surface), `integrationGate`, `dependsOn`,
  and a guardrail's `scope`.

A denylist operation may only be emitted as a **proposal** that requires **(a)** human approval **AND
(b)** a re-run of `/guardrails-review`, and **(c)** automatically **re-stales the review marker** —
because touching any of these files changes `PlanDefinitionHash` (§7.3), which self-invalidates
`state/guardrails-review.json` (§13) the instant the bytes change (GR2025 nudge returns). This is the
#260 interaction, load-bearing. **Even under `auto`, a denylist operation always halts / routes to the
human** — matching §2.1's invariant that `auto` authorizes a SAFE action, never an UNSOUND one.

### 3.2 ALLOWLIST — the action / budget layer — auto-applicable per tier

- **Ephemeral guidance injection** (v1) — sharper, failure-specific guidance appended to the *next
  attempt's composed prompt* via the existing `PromptComposer` feedback channel. **Touches no authored
  file, no hash, no review marker.** The safest lever; the primary v1 heal.
- **Budget overrides for this run** (v1) — `maxTurns` / `retries` / `timeoutSeconds` applied as
  *runtime overrides*, exactly as #94 already escalates `maxTurns` without editing `task.json`. No
  authored-file mutation, no hash change.
- **Persistent authoring-defect fixes** (v2) — a per-worktree dependency restore (#259; an environment
  prep command, not a definition edit) and persistent `action.prompt.md` edits. These *do* change
  `PlanDefinitionHash` and therefore **re-stale the review marker** (correctly — a machine-edited
  action prompt is un-reviewed until re-attested).

### 3.3 DEFAULT — closed allowlist

Anything not on either list → **propose-only**. A closed allowlist makes it impossible to auto-apply an
unclassified operation (KISS + fail-safe).

**The rule in one sentence:** *the overwatcher may auto-adjust only what does not change the verdict
surface; everything that changes the verdict surface routes through human + `/guardrails-review` +
review-marker re-stale.*

## 4. Triggers — deterministic detection, once per transition

Triggers are detected **deterministically by the harness** (invariant 1) — the judge never decides
*when* it fires. Per the maintainer's **#305 Decision C override**, v1 fires **EAGERLY**: the overwatcher
engages as soon as a task reaches **`attempt ≥ 2`** (the eager trigger), *in addition to* the typed
transitions the harness already classifies with a distinct outcome:

- **eager** — a retryable failure at **`attempt ≥ 2`** with budget remaining (`EagerAttempt`);
- terminal exhaustion → `needs-human` (today's §9.2 trigger — now one overwatcher case, §9.2.1);
- the no-op-deadlock short-circuit about to fire (#174/#182), or an identical deterministic-`script`
  failure (#264) — floor boundaries where a sanctioned change may un-halt;
- the permission-wall early halt (§9.3 / #266) — may fire even on attempt 1 (diagnose-only);
- a write-scope violation on ≥2 attempts and a `max-turns` exhaustion (§7 / #94) — both are guardrail-class
  failures at `attempt ≥ 2`, so the **eager** trigger already covers them.

It fires **at most ONCE per attempt** (a short-circuit consult takes precedence over the eager consult so
both never fire the same attempt), **never** multiple times within an attempt, and the whole thing is
**bounded by `maxCostUsd`** — each diagnose's own prompt spend is journaled (the top-level
`overheadCostUsd`, SSOT §7 — the shared overhead sink, since #314 also covering the AI-merge worker and the
terminal triage) and folded into the run's cumulative cost, so once that cost reaches the cap no further
diagnose is spent, and the diagnose spend also shows up in the reported total (the cost mitigation for
eager). It does not fire on an agent-emitted `needsHuman`.

## 5. Decision authority — and "no sanctioned change ⇒ no grant"

The overwatcher does **not** decide "retry vs. halt" as a raw verdict. It decides **what change (if
any) to apply**:

- If it applies a **sanctioned change** (guidance injection / budget bump — or, v2, an allowlist
  authoring fix) → the harness grants **one more attempt** with that change.
- If it applies **no change** → the deterministic policy (short-circuit / budget exhaustion) stands and
  the task **halts honestly**.

So **"grant adjusted attempts" always means "grant *because* I changed X."** A "keep trying,
unchanged" verdict is not in the overwatcher's authority — that is the deterministic short-circuit's
domain, and it always halts. This is the exact reconciliation with #174/#264 (§8): the short-circuit
fires precisely when "no observable change + byte-identical failure" holds, and the only way the
overwatcher un-halts is by injecting a genuine change that makes the next attempt materially different.

All grants are bounded by the existing hard caps (#94's escalation cap, `maxCostUsd`) and a **hard
cumulative per-task granted-retry ceiling** (`TaskExecutor.MaxCumulativeGrantedRetries`): a per-grant clamp
bounds one grant, and the cumulative ceiling bounds the SUM across every grant a task receives, so repeated
grants can never grow the budget without limit even if every one is approved (WEAK-2). The overwatcher can
never exceed them.

## 6. Tiers mapped onto the shared `autonomyPolicy` (§2.1)

The overwatcher introduces **no new policy field** — it is governed by the **one** shared §2.1
`autonomyPolicy` (`prompt | halt | auto`, default `prompt`, GR2031). The three internal "tiers" of the
original #269 proposal (diagnose / propose / auto) map onto the shared values as follows:

| #269 tier | `autonomyPolicy` value | v1/v2 | Overwatcher behavior at a struggle boundary |
|---|---|---|---|
| **diagnose** (the advisory *core*) | present under **`halt` AND `prompt`** (and `auto`) | **v1** | classify doomed-vs-retryable, render the diagnosis under the table + `decisions[]`, halt honestly with the rich reason. Never gates, never applies. |
| **(pure halt)** | **`halt`** | **v1** | diagnose + **always halt**; propose nothing, apply nothing. Most conservative. |
| **propose** | **`prompt`** (default) | **v1** | diagnose + on a TTY, propose the sanctioned **action-layer** change (budget bump / guidance) with details and ask `y/N`; apply on approve, halt on decline. **Non-interactive (`Console.IsInputRedirected`) → honest halt** — never blocks. |
| **auto** | **`auto`** | **v2 (bet #6)** | diagnose + auto-apply the **allowlist** action-layer fix classes wherever SAFE/SANCTIONED, no prompt. A **denylist** (verdict-surface) operation is UNSOUND → always routes to propose-to-human + re-review regardless. |

**The diagnosis is the always-on core** — it runs under *all three* policy values and never gates
(it is the active generalization of the advisory §9.2 triage). Only the *action* differs by policy.

**v1 boundary (important):** v1 fully implements the diagnosis core + the `prompt`/`halt` decision
paths + the allowlist's **ephemeral guidance and budget levers**. Under v1, the `auto` *value* — which
already exists in the shared field for #254 wave-drift and the between-wave checkpoint — does **not**
grant the overwatcher silent auto-application of its own fixes: v1 treats `auto` for the overwatcher's
own proposals the same as `prompt` (propose on TTY, honest-halt non-interactive). **Turning `auto` into
silent overwatcher auto-application, and adding the persistent authoring-defect fix classes, is v2 bet
#6.** This keeps the maintainer's "auto-heal = v2" line crisp: no overwatcher-*initiated* auto-application
in v1. (The deterministic #94/#264/#174 floor continues to auto-fire regardless of policy — it is not an
overwatcher action.)

## 7. Reporting surface

The overwatcher reuses the **shared** §7 `decisions[]` audit log verbatim — it does **not** invent a
separate durable schema:

- **Durable audit** — each overwatcher decision appends a `decisions[]` entry with
  `boundary: "task"`, the `policy` in force, the `decision`
  (`halted | prompted-approved | prompted-declined | auto-applied`), `at`, `subject` (the task id),
  `headline`, and `detail`. Rendered **under the live task table** (`IRunObserver`) and in the static
  log site (§12), exactly as the #254 wave/drift decisions are.
- **Per-task detail stream** — because the overwatcher may fire multiple times per task (unlike the
  single terminal `triage.json`), it also appends to a per-task
  `logs/<runId>/<task-id>/overwatch.jsonl` (§8) — one record per decision with the trigger,
  classification, proposed/applied fix ops, and the authority-class the classifier assigned. This is
  the multi-fire *detail*; the durable *audit* is `decisions[]`.
- **Terminal case unchanged** — the terminal-exhaustion trigger still writes `feedback.md` +
  `triage.json` (§9.2.1); `TriageSummaryReader` (#163) and its console summary are untouched
  (back-compat).

## 8. Reconciliations

- **The deterministic floor (#94 / #264 / #174) stays the floor.** The short-circuits always fire
  deterministically; the overwatcher never overrides a deterministic halt into "keep going unchanged"
  (§5). #94's fixed 1.5×→4× escalation is the fallback the harness applies when the policy is `halt`
  or the overwatcher is inactive; when active under `prompt`/`auto`, the overwatcher chooses the bump
  within the *same* hard cap and under `maxCostUsd`.
- **Drift-halt (#274 A/C) — disjoint by task state, never confused.** Definition-drift (§7.2) detects
  an *unintended* edit to an **already-`succeeded`** task, cross-run, at resume. An overwatcher edit is
  a *sanctioned* edit to a **still-failing** task, in-run, inside its live retry loop. They are
  **disjoint by task state**: a task is either succeeded (drift-halt's domain; the overwatcher will not
  touch it) or failing (the overwatcher's domain; drift-halt does not apply). Because any overwatcher
  authoring edit lands *before* the task settles, the new `TaskDefinitionHash` is stamped in its settle
  trailer, so a later resume sees a **match** — no false drift-halt on the overwatcher's own sanctioned
  change. This mirrors the wave-level `isCompleted` predicate (`10-multi-wave-plans.md` §14.7): *drift
  ⟺ the changed unit was already completed*; the overwatcher only ever changes not-yet-completed units.
- **#260 — the trust anchor, confirmed.** Any verdict-surface change self-invalidates the review
  attestation (§3.1). Without #260 the never-touch-verdict guarantee could not be made mechanical.
- **Invariants.** (1) *Deterministic over judges* — triggers detected deterministically, proposals
  classified deterministically; the judge is never the verdict authority. (2) *Single writer* — the
  overwatcher emits a proposal file; the harness applies sanctioned ops; it can never mark a task
  succeeded or merge a fragment. (3) *Verdicts from files* — a malformed/absent/errored proposal = no
  action; the deterministic policy stands (advisory exactly as §9.2). (5) *Honest halts* — "declare
  doomed → halt" is always safe at every tier; the overwatcher makes halts *earlier and richer*, never
  softer.

## 9. The inter-wave role — auto wave breakdown at the JIT checkpoint (#360; v2, couples to #254)

> **Status: DESIGN (extends the v1 seam).** §9.1–§9.6 flesh the reserved between-wave seam into a complete,
> implementable design for the #360 inter-wave breakdown invocation (Phase 1/2), superseding the deferral in
> `docs/plans/design-360-auto-wave-breakdown.md` (whose Q1–Q5 decisions are carried in and cited). The
> **criticality dial** that governs this checkpoint under an unattended run is designed in the companion
> `docs/plans/12-autonomous-mode.md` (#361); Phase 1 here needs only the shipped `autonomyPolicy`.

The overwatcher is **one supervisor invoked at decision boundaries**, and a **wave boundary** is
another such boundary. `10-multi-wave-plans.md` (§14.7/§14.9) and roadmap bet #6 already reserve this:

- **#269 boundary** = a struggling-task trigger; scope = the current failing task's action/budget.
- **#254 boundary** = a wave's completion; scope = the *next, all-`pending`* wave's tasks/guardrails.

The asymmetry holds identically at the wave boundary: **adjusting a downstream wave's guardrails is not
"softening a verdict," because those guardrails are being *authored* (JIT, against materialized upstream
artifacts) through `/plan-breakdown` then `/guardrails-review`.** The overwatcher does not hand-soften
wave N+1's guardrails; it (re)generates them through the *same reviewed pipeline*, gated by the same
`autonomyPolicy`, re-staling that wave's per-wave review marker (§13). The mandatory `/guardrails-review`
pass on the freshly-authored wave IS the verdict-surface protection at the wave boundary — the analogue
of §3.1's "route through re-review." §14.7's rule already scopes the overwatcher's write authority to
**fully-`pending` future waves**, which is exactly the "changes no completed unit ⇒ never drift"
guarantee.

**Shared contracts the two designs agree on (already authored in #254; this doc reuses them):** the one
`autonomyPolicy` (§2.1); the `boundary`-discriminated `decisions[]` (§7); the nested
`<plan>/<wave>/<tasks>` layout + strict wave order (§14). No new shared contract is introduced here.

### 9.1 The trigger — the JIT checkpoint, not a task struggle

The between-wave actor fires at a **different boundary** from the per-task overwatcher and must not be
conflated with it (design-360 Q4): the per-task overwatcher (§4) triggers on a **failing task** inside its
retry loop; the between-wave actor triggers at the **JIT wave checkpoint** (SSOT §14.4 step 2) — a wave
folder present but with an **empty/unauthored `tasks/`** — on an **all-`pending`** next wave. They are
disjoint by unit state (a struggling task vs. an unauthored wave) and share only the `autonomyPolicy` knob
and the `decisions[]` log. **This is therefore NOT a new trigger on the `Overwatch` class** — it is a
distinct between-wave actor the wave loop (`Scheduler.RunWavedAsync`) invokes at the checkpoint. They are
"one supervisor" conceptually (both are the harness reasoning at a boundary), two components mechanically.

**Opt-in signal (design-360 Q1) — the wave `brief.md`.** Auto-breakdown fires only when the unauthored wave
carries a human-authored `wave-NN-slug/brief.md` (the `.md` plan-breakdown already expects as input). Its
presence is the opt-in: **absent ⇒ the current honest-halt** (`WaveHalt` kind `NextWaveUnauthored`) with an
updated message naming the `brief.md` convention; **present ⇒ auto-breakdown-eligible**, gated by
`autonomyPolicy` (and, unattended, the dial's `wave-checkpoint` gate — doc 12 §5.1). `brief.md` is committed,
excluded from `PlanDefinitionHash` (it is breakdown *input*, not reviewed output) but included in
`WaveDefinitionHash` (a changed brief on a completed wave is legitimate drift). Contents: what the wave must
accomplish, the materialized upstream it builds on, any intra-wave constraints (design-360 Q1).

### 9.2 The `breakdown` prompt-runner profile

The invocation uses the shipped `IPromptRunner` seam under a **reserved `breakdown` profile** in
`promptRunners` (alongside `overwatch` / `ai-merge` / `ai-triage`), resolved with fallback to the default
runner. It differs from the `overwatch` diagnose profile along the one axis that matters:

| | `overwatch` profile (diagnose, §4) | `breakdown` profile (this section) |
|---|---|---|
| Tool set | **Read-only** (Read, Grep, Glob) — it only reasons | **Full authoring** (Read, Write, Edit, Bash, Grep, Glob) — it writes task files |
| Writes | nothing (advisory proposal only) | the next wave's `tasks/**` (+ its diagram) |
| Verdict gate | none (advisory) | **`guardrails validate`** on the output (§9.4) — deterministic |
| Cost sink | `overheadCostUsd` | `overheadCostUsd` (same sink; not a task attempt) |

The full tool set is the reason it is a **distinct** profile, not a mode of `overwatch`: breakdown authors
files (invariant 2 — it writes into a `pending` wave folder, never merged state); diagnose never writes.

### 9.3 Composing the invocation

The harness composes a skill-invocation prompt exactly as the diagnose composes its own (inline the skill,
inject the context):

1. **Inline the `plan-breakdown` SKILL.md** content (the same technique the diagnose uses for its skill).
2. **Pass `wave-NN-slug/brief.md`** as the breakdown target (the input plan).
3. **Inject the integration worktree path** so the skill reads the **materialized** upstream — the outputs
   of the completed prior waves live on the plan branch at `<worktreeRoot>/<runId>/_integration`, NOT the
   user's read-only checkout (SSOT §14.4 Decision D / the #197 hand-fix flow). This is the sharpest genuine
   tension (design-360 C4): the author/skill works against the integration worktree, consistent with #197,
   not the checkout.
4. **`--add-dir <integration-worktree-path>`** so the Claude sub-process can access those materialized files.

The invocation is **not retry-aware** in v1: a failed breakdown falls back to honest-halt with the failure
detail (§9.5), and the human intervenes. Cost is charged to `overheadCostUsd` (the shared overhead sink,
alongside diagnose / AI-merge / terminal triage, #314) — folded into `maxCostUsd` and the reported total.

### 9.4 The deterministic gate on the output (invariant 1)

The breakdown output is verified **deterministically**, never by the judge that produced it: after the skill
writes `wave-NN-slug/tasks/**`, the harness runs **`guardrails validate <plan>`** (the existing command, no
new code) as the gate. It already catches missing required files, zero-guardrail tasks, cross-wave
`dependsOn` (GR2034), and numbering gaps (GR2033). **Pass → `WaveHalt` kind `BreakdownComplete`** (the wave
is authored, awaiting human review — §9.6). **Fail → `WaveHalt` kind `BreakdownFailed`** carrying the full
`guardrails validate` output; the partial `tasks/` is left in place for human repair.

No hot-reload (design-360 Q3): after `BreakdownComplete` the human reviews, then the next `guardrails run` is
an **ordinary resume** — the plan re-loads fresh, the completed waves skip, and the now-authored wave's JIT
checkpoint passes. Hot-reloading mid-run would replumb the Scheduler's single-write-at-start plan/journal/
worktree references for a negligible latency saving; the hard-halt-then-resume pattern is battle-tested.

### 9.5 Halt kinds + logs

- **`WaveHaltKind.BreakdownComplete`** / **`.BreakdownFailed`** — two new kinds added to the shipped
  `WaveHaltKind` enum (`NextWaveUnauthored` / `WaveDrift` / `EntryGateFailed` / `ExitGateFailed`).
  Phase 0 (design-360) reserves them as stub values; Phase 1 emits them.
- **Transcript location** (design-360 Q3 / doc 12 §6.1): `logs/<runId>/<wave-dir>/breakdown/` — the
  between-wave invocation is NOT a task attempt, so it does not live under `logs/<runId>/<task-id>/
  attempt-N/`. It holds the composed prompt, the raw stream, the transcript projection, and the
  `guardrails validate` output. **Proposed SSOT §8 addition** (named, not written — the §14/§8-owning
  worktree lands it): the `logs/<runId>/<wave-dir>/breakdown/` sub-tree.
- **`decisions[]` entry** — every checkpoint invocation OR decline emits a `boundary:"wave"` entry
  (`decision`: `halted` | `prompted-approved` | `auto-applied`, extended by doc 12 §6.2 under the dial).
  Today the JIT checkpoint emits no `decisions[]` entry — closing that gap is a Phase-0 deliverable.

### 9.6 The review-gate invariant survives `auto` / full-autonomous — the load-bearing constraint

Auto-breakdown may **invoke** breakdown and (per dial) proceed past *some* gates, but **marking a JIT wave
"reviewed" without attestation is forbidden at every tier, including `auto` and the fully-autonomous dial.**
This is §3.1's asymmetry at the wave boundary: the mandatory `/guardrails-review` pass on the freshly-authored
wave IS the verdict-surface protection, and the harness never self-attests it (SSOT §13; invariant 5).

The `autonomyPolicy` table applies to **invocation** (design-360 Q5), NOT to the review gate:

| `autonomyPolicy` | `brief.md` | Behavior |
|---|---|---|
| `halt` | any | honest-halt, `brief.md` named in the message |
| `prompt` (default), interactive TTY | present | prompt "invoke plan-breakdown for wave-NN? [y/N]" → invoke / halt |
| `prompt`, non-interactive | present | honest-halt (consistent with drift) |
| `auto` | present | invoke without prompting |
| any | absent | honest-halt, message names the `brief.md` convention |

**`auto` governs invocation; it NEVER authorizes skipping review.** After a successful `BreakdownComplete`,
the harness still halts for the human to run `/guardrails-review` — the review step is impossible to miss
(the halt message says so) and the wave's `WaveDefinitionHash` re-stales the per-wave review marker on any
edit during review, so a subsequent run sees the wave as reviewed only if it genuinely was.

**The unattended-run reconciliation (design-360 Q4, resolved in doc 12 §5.2).** In a fully-unattended
firstmate run there is no human to run `/guardrails-review`, so the review gate cannot be auto-cleared. The
resolution: **default = escalate to firstmate** (halt the wave, record a `review-gate` escalation with full
context, surface via `IEscalationSink` — doc 12 §7); **opt-in = proceed-with-recorded-unreviewed-risk**
(`gateThresholds.review-gate: "proceed-unreviewed"`, doc 12 §5.2 Option P), which runs the wave WITHOUT a
review marker, records `decision: "proceeded-unreviewed"`, and permanently flags the run as "ran with N
unreviewed waves" — it never marks the wave reviewed. Escalate is the recommended default; the opt-in is a
deliberate, named, forensically-indelible risk transfer, never a silent one and never a forged attestation.

## 10. Phasing

- **v1 (fast-follow, ships under #269):** the diagnosis core (always on) + the `prompt`/`halt` decision
  paths + the allowlist's **ephemeral guidance and budget levers**; the mechanical classifier for the
  allowlist/denylist boundary (v1 exercises the allowlist's ephemeral+budget members and the denylist's
  propose-only routing); the shared reporting (`decisions[]` + `overwatch.jsonl`); honest-halt-with-rich
  diagnosis. Low risk: advisory + honest-halt + no authored-file mutation.
- **v2 bet #6 (auto-heal):** the `auto` value grants silent overwatcher auto-application; the persistent
  authoring-defect fix classes (#259 dep-restore, `action.prompt.md` edits); the `auto`-tier
  block-until-review hardening (Open Decision A). Depends on #260 (shipped).
- **v2 bet #6 (inter-wave) — auto wave breakdown (#360, §9.1–§9.6):**
  - **Phase 0 (docs + minor harness, no AI-invocation):** the `brief.md` convention (SSOT §14.10); the
    enhanced `NextWaveUnauthored` halt naming `brief.md`; the `WaveHaltKind.BreakdownComplete`/`.BreakdownFailed`
    stub values; the `boundary:"wave"` `decisions[]` entry at the checkpoint. Ships without any prompt runner.
  - **Phase 1 (the invocation):** the `breakdown` profile (§9.2), the between-wave actor in
    `Scheduler.RunWavedAsync` (§9.3), the `guardrails validate` gate (§9.4), the `logs/<runId>/<wave-dir>/
    breakdown/` transcript (§9.5), `overheadCostUsd` charging, and the review-gate invariant under `auto`
    (§9.6). Governed by the shipped `autonomyPolicy` alone.
  - **Phase 2+ (the dial + unattended review-gate resolution):** designed in `docs/plans/12-autonomous-mode.md`
    (#361) — the `wave-checkpoint` dial gate and the escalate/`proceed-unreviewed` review-gate policy (§9.6).

## 11. Devil's-advocate self-critique

- **Strongest counter — the asymmetry has a real hole: action-side gaming.** The classifier protects
  guardrail *bodies*, but an `action.prompt.md` edit (v2) can still produce a *tautological pass* — e.g.
  sharpen the prompt to write the exact string a `grep`-guardrail checks for, without doing the real
  work. The path-based classifier *allows* it (the action file is on the allowlist), yet it is precisely
  the cheapest-wrong-implementation `/guardrails-review` exists to catch. **Response, and where it
  lands:** (1) in **v1 the lever is ephemeral appended guidance, not authored-prompt rewriting** —
  appended context cannot remove the task's real deliverable requirement, so v1 exposure is minimal;
  (2) v2 persistent action-prompt edits **re-stale the review marker**, so a machine-gamed prompt reads
  as un-reviewed and demands re-attestation before the plan is trustworthy; (3) fundamentally, the
  overwatcher makes guardrails **no weaker than they already are** — a guardrail passable by a magic
  string was already exploitable by the original agent; the mitigation is the product's existing one
  (strong deterministic guardrails + the review pass). **This is an accepted residual, stated not
  hidden:** *the asymmetry protects the verdict definition, not the verdict's semantic robustness
  against action-side gaming.* Open Decision A offers a stronger v2 gate (block-until-review).
- **YAGNI.** v1 is honestly "active triage + the shared policy knob," framed as such — not a framework.
  The "first-class overwatcher" earns its keep only when auto-heal + the inter-wave role arrive (v2).
- **Cost.** Conservative triggers + once-per-transition + `maxCostUsd`, not a blanket `attempt ≥ 2`.
- **Judge-in-loop safety.** Worst case is bounded extra spend (capped) or an honest halt — both safe.
- **Watching the watcher.** Advisory, verdict-from-files, silently skipped on error; never blocks the run.
- **Honest-halt tension.** `halt`/`prompt`-non-interactive halt exactly when the deterministic policy
  would; only `prompt`-TTY (and v2 `auto`) grant extra attempts, and a "doomed" diagnosis makes the halt
  *earlier + richer*, never later.

## 12. Open decisions — RESOLVED (#305)

- **A — the action-side-gaming residual: ACCEPTED BY DESIGN.** The v1 lever is ephemeral appended
  guidance, which cannot remove the task's real deliverable requirement, so v1 exposure is minimal; a
  guardrail passable by a magic string was already exploitable by the original agent — the mitigation is
  the product's existing one (strong deterministic guardrails + `/guardrails-review`). The v2 `auto`-tier
  block-until-review hardening is NOT built in v1.
- **B — v1 default posture: the diagnose core is ON by default.** It fires whenever an overwatch-capable
  prompt runner resolves (the reserved `overwatch` profile with fallback to the default/sole runner);
  a script-only plan (no runner) gets no overwatcher.
- **C — trigger set: EAGER (maintainer override).** The overwatcher engages at `attempt ≥ 2` in addition
  to the typed transitions (§4), fires at most once per attempt, and is bounded by `maxCostUsd`.
- **D — `overwatch.jsonl`: KEPT.** The append-only per-task detail stream is retained alongside the
  shared `decisions[]` (mirrors the multi-fire nature).
- **E — GR code:** v1 introduces **no new GR code** (it reuses GR2031/`autonomyPolicy` and GR2025/review
  staleness). If a distinct validation is later needed (e.g. an overwatcher-specific config sub-field),
  the next-free code is **GR2035** (GR2031–GR2034 are allocated by #254).

## 13. SSOT deltas + implementation handoff

**SSOT deltas (this draft):** §9.2 retitled to "The overwatcher (active AI supervisor)" with the shipped
triage repositioned as §9.2.1 (terminal-exhaustion case, invariants verbatim); §8 log layout gains
`overwatch.jsonl`; §7.2 gains the overwatcher-vs-drift disjoint-by-task-state cross-ref. §2.1
`autonomyPolicy` and §7 `decisions[]` are **referenced, not redefined** (owned by the #254 draft).

**Implementation handoff (after this design-of-record's #106 draft-PR review):**

1. `guardrails-harness-developer` — an `Overwatch` class (refactor `NeedsHumanTriage` into it), trigger
   detection in `TaskExecutor`/`Scheduler`, the `TaskDefinitionFiles`-keyed fix classifier, wiring to the
   shared `autonomyPolicy`, `decisions[]` (`boundary:"task"`) + `overwatch.jsonl`, CLI under-table
   rendering. `filesTouched: src/Guardrails.Core/Execution/**, src/Guardrails.Cli/**`. Sequenced **after**
   the SSOT edit lands (invariant 4).
2. `guardrails-test-author` — trigger-detection determinism; classifier allowlist/denylist matrix (esp.
   `writeScope`/guardrail-body → propose-only); advisory-never-gates + malformed-proposal = no-op;
   drift-halt disjoint-by-state. `filesTouched: tests/**`.
3. `guardrails-skill-author` — `guardrails-domain-knowledge` execution-semantics section (overwatcher
   subsumes §9.2). `filesTouched: .claude/skills/guardrails-domain-knowledge/**`.

### 13.1 Inter-wave role (#360, §9) — SSOT deltas + handoff (v2, separate draft PR)

**Proposed SSOT deltas (named, NOT written here — the §14/§8-owning worktree lands each in the change that
implements it, invariant 4):**
- **§14.10 (NEW)** — the `brief.md` convention: optional human-authored wave-input file; opt-in semantics;
  `PlanDefinitionHash` exclusion + `WaveDefinitionHash` inclusion; `guardrails validate` does not error on
  absent `brief.md` (design-360 Q1).
- **§14.4** — the between-wave step gains the `brief.md`-detection branch + the `autonomyPolicy` invocation
  table (§9.6) + the `boundary:"wave"` `decisions[]` entry at the checkpoint.
- **§9 (prompt runners)** — the reserved `breakdown` profile (full authoring tool set; `overheadCostUsd`;
  integration-worktree `--add-dir` injection, §9.2/§9.3).
- **§8 (log layout)** — the `logs/<runId>/<wave-dir>/breakdown/` transcript sub-tree (§9.5).
- **`WaveHaltKind`** (harness enum, not SSOT) — add `BreakdownComplete` / `BreakdownFailed` (§9.5).

**Implementation handoff (after this DoR's #106 draft-PR review; sequencing per doc 12 §11 Phase 1):**
1. `guardrails-harness-developer` — Phase 0 (`brief.md` detection in `BuildUnauthoredWaveHalt`, the two
   `WaveHaltKind` stub values, the checkpoint `decisions[]` entry) then Phase 1 (the `breakdown` profile,
   the between-wave actor in `Scheduler.RunWavedAsync`, the `guardrails validate` gate, `overheadCostUsd`
   charging, the breakdown log site). `filesTouched: src/Guardrails.Core/Execution/**, src/Guardrails.Cli/**,
   docs/plans/02-schemas-and-contracts.md` (the §14.10/§14.4/§9/§8 deltas, same change).
2. `guardrails-skill-author` — `plan-breakdown` Step 9: `brief.md` as the JIT-wave input;
   `example-breakdown-waved.md` gains an example `brief.md`. `filesTouched: .claude/skills/plan-breakdown/**`.
3. `guardrails-test-author` — breakdown-invoked → valid wave → `BreakdownComplete` halt; invalid wave →
   `BreakdownFailed` with validate errors; `autonomyPolicy: prompt` non-interactive → honest-halt regardless
   of `brief.md`; `auto` invokes but still review-halts. `filesTouched: tests/**`.
