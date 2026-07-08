# 11 — The Overwatcher (active AI run supervisor) — design of record (issue #269)

> **Status: PLANNED (design draft, in maintainer review).** v1 is specified in present tense (the
> design-draft convention). **v1 = the active *diagnose* + *propose* supervisor** (§9); **bounded
> auto-heal (`auto`-tier application of authoring-defect fix classes) and the inter-wave role are v2
> bets** (§10) that only need the seam defined in v1 — the same seam the #254 multi-wave skeleton
> already opens (`10-multi-wave-plans.md` §14.9, roadmap bet #6).

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
*when* it fires. v1 fires **conservatively**, only at transitions the harness already classifies with a
distinct outcome (reusing existing signal points, not adding an eager per-attempt probe that would
multiply cost and Claude-contract surface):

- terminal exhaustion → `needs-human` (today's §9.2 trigger — now one overwatcher case);
- `max-turns` outcome (§7 / #94);
- the no-op-deadlock short-circuit about to fire (#174/#182), or an identical deterministic-`script`
  failure (#264);
- the permission-wall early halt (§9.3 / #266);
- a write-scope violation on ≥2 attempts (§3.4 loop).

It fires **at most once per task per distinct trigger transition** (as §9.2 fires once), **never**
mid-retry while budget remains and nothing has changed. A blanket `attempt ≥ 2` trigger is deliberately
**rejected for v1** on cost grounds (Open Decision D).

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

All grants are bounded by the existing hard caps (#94's escalation cap, `maxCostUsd`, the retry budget
ceiling). The overwatcher can never exceed them.

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

## 9. The inter-wave role (v2, couples to #254)

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

## 10. Phasing

- **v1 (fast-follow, ships under #269):** the diagnosis core (always on) + the `prompt`/`halt` decision
  paths + the allowlist's **ephemeral guidance and budget levers**; the mechanical classifier for the
  allowlist/denylist boundary (v1 exercises the allowlist's ephemeral+budget members and the denylist's
  propose-only routing); the shared reporting (`decisions[]` + `overwatch.jsonl`); honest-halt-with-rich
  diagnosis. Low risk: advisory + honest-halt + no authored-file mutation.
- **v2 bet #6 (auto-heal):** the `auto` value grants silent overwatcher auto-application; the persistent
  authoring-defect fix classes (#259 dep-restore, `action.prompt.md` edits); the `auto`-tier
  block-until-review hardening (Open Decision A). Depends on #260 (shipped).
- **v2 bet #6 (inter-wave):** the wave-boundary application (§9). Ships with/after the waves design.

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

## 12. Open decisions (for the maintainer)

- **A — the action-side-gaming residual:** accept-by-design (same as the product's guardrail-strength
  dependency), or add the **v2 `auto`-tier hardening** that an overwatcher-edited `action.prompt.md`
  **blocks** settle-`succeeded` until re-review (stronger than the GR2025 warn)?
- **B — v1 default posture:** is the overwatcher's diagnosis core **on by default** (it is essentially
  richer active triage, but adds a bounded prompt cost per struggling task), or opt-in? The *policy*
  default is already `prompt` (§2.1); this is about whether the *diagnosis* runs when the shipped
  terminal triage would not have.
- **C — trigger set:** conservative (existing typed transitions, recommended) vs. eager (`attempt ≥ 2`)?
- **D — `overwatch.jsonl`:** keep the per-task detail stream (recommended, mirrors the multi-fire
  nature) or fold everything into `decisions[]` alone?
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
