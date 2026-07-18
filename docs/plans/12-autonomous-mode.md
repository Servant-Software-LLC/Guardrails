# 12 — Autonomous mode (the criticality dial) — design of record (issue #361)

> **Status: DRAFT — design-of-record for the #106 draft-PR loop; implementation milestones do NOT start
> until the maintainer has reviewed inline and comments are addressed.** This is a **v2 bet** (it realizes
> the autonomy slice of roadmap bet #6 and lights up the overwatcher's deferred `auto` tier for the
> action/budget layer). It composes with — and never redefines — the shipped `autonomyPolicy` (SSOT §2.1),
> the overwatcher (`docs/plans/11-overwatcher.md`), and the wave loop (`docs/plans/10-multi-wave-plans.md`).
>
> **Companion:** the inter-wave breakdown invocation (#360 Phase 1/2) is designed in
> `docs/plans/11-overwatcher.md` §9 (the between-wave actor), which this document's dial governs at the JIT
> wave checkpoint. Read the two together. Where a wire contract is stated here it is a **proposed** SSOT
> delta (§"SSOT deltas") — this doc authors NO change to `02-schemas-and-contracts.md`; a parallel worktree
> owns §14 there.

---

## 1. What it is, and the pain it removes

**Autonomous mode is the unattended sibling of the interactive `guardrails run`** — a complete
non-interactive run for a firstmate crewmate, where **no human is present** to review a wave, answer a
`needsHuman`, or run `/guardrails-review`. Today an unattended run **honest-halts** at every one of those
gates (invariant 5, correct and load-bearing): `autonomyPolicy: prompt` degrades to halt when stdin is
redirected, `needsHuman` short-circuits to halt unconditionally, and the review gate is a human action the
harness never performs. That is exactly right for a supervised run and exactly useless for a firstmate crew
that wants a plan driven to green (or to a *recorded, forensic halt*) without a babysitter.

The pain: a crewmate cannot hand Guardrails a multi-wave plan and walk away. Every judgment gate is a full
stop that requires a human at the keyboard, even when the decision is trivial (which library flavor to
import) and even when the human, six hours later, would have rubber-stamped it.

**Autonomous mode removes that stop — responsibly — with a criticality dial.** At every gate the
interactive flow would surface to a human, an unattended run:

1. **classifies** the stop (judgment call vs. hard blocker — §4);
2. for a **judgment call**, **assesses** its criticality/risk/confidence and compares it to a configured
   **dial**: **criticality ≥ dial → escalate** (surface to firstmate + record the question and full
   context); **criticality < dial → proceed with a recorded best-guess** (§3);
3. for a **hard blocker**, ignores the dial entirely: retry-with-backoff if transient (bounded), else halt
   unconditionally with full failure context (§4).

The dial spans **cautious** (escalate ~everything = the interactive halt-at-every-gate flow) →
**fully autonomous** (best-guess ~everything, escalate only the most critical). It is configurable per run
and overridable per gate type.

**The north star and the guard rail are the same sentence as the overwatcher's:** *a prompt may propose,
only a deterministic gate may certify.* The dial can authorize **proceeding past a gate that has no
deterministic answer** — it can NEVER soften a deterministic guardrail's verdict, auto-approve a
verdict-surface change, or self-attest a `/guardrails-review`. A wrong best-guess produces a task that then
**fails its own deterministic guardrails** → honest halt anyway. The deterministic floor is the backstop
under every dial setting.

**Autonomous mode is a SIBLING, not a replacement.** Interactive stays the default; the dial is inert
unless the operator opts in (`--autonomous`, or a config value). An existing run's behavior is byte-for-byte
unchanged.

## 2. Placement and invariants

### Placement

| Slice | Placement |
|---|---|
| The **criticality dial** field + per-gate overrides | **schema** — a NEW `autonomy` config block (§3), composing with the shipped `autonomyPolicy` (§2.1) |
| **Gate classification** (judgment vs hard-blocker) + the **threshold compare** | **harness** (`Guardrails.Core`) — deterministic, invariant 1 |
| **Criticality assessment** (the judge) | **harness** — a constrained prompt behind `IPromptRunner` (the reserved `overwatch`/`assess` profile), advisory exactly like the overwatch diagnose |
| The **firstmate escalation seam** | **harness** — an `IEscalationSink` seam over the shipped honest-halt + `decisions[]` + `IRunObserver` machinery (plain files, invariant 6) |
| The **inter-wave breakdown invocation** (#360) | **harness + skill** — designed in `docs/plans/11-overwatcher.md` §9; the dial governs its checkpoint |
| **Forensic contract** (`decisions[]` deltas + `autonomy.jsonl`) | **schema** — additive §7/§8 records |
| CLI `--autonomous` / `--unattended` / `--dial` | **CLI** (`Guardrails.Cli`) |
| Silent verdict-surface auto-application | **out of scope, permanently** — the overwatcher denylist (SSOT §9.2, doc 11 §3.1) is unchanged |

This is a **v2 bet built entirely on shipped seams** — one new orthogonal config block, extended durable
records, and wiring over `autonomyPolicy` / the overwatcher classifier / `PromptFailureKind` / the
honest-halt path / `IPromptRunner`. No new architecture.

### Invariants in play

1. **#1 Deterministic over prompt-judges — the sharpest strain, and how it is respected.** The criticality
   assessment is a **judge**, and a judge is never the verdict authority. The design keeps it honest three
   ways: (a) the gate is classified **deterministically** (judgment vs hard-blocker — §4); (b) the judge
   runs **only** at a gate that has **no deterministic verdict** (the agent asked a design question; a wave
   needs breakdown) and can ONLY authorize *proceeding past that gate*, never clearing a guardrail; (c) the
   threshold comparison is a **deterministic** harness compare, and a malformed/absent/errored assessment
   **escalates** (the safe default, verdict-from-files). The dial NEVER touches the verdict surface or the
   review gate (those are deterministic floors — §5).
2. **#5 Honest halts — the spine.** An escalation IS an honest halt, enriched with a machine-readable
   question. The review gate is never machine-self-attested. A terminal-exhaustion `needs-human` (a task
   that could not converge to green) is never best-guessed past. A best-guess that proceeds is recorded as a
   best-guess — the run can never report a best-guessed or unreviewed wave as fully green-and-reviewed.
3. **#2 Single writer.** The breakdown invoker *authors* files into a `pending` wave folder (not merged
   state); the harness remains the single writer of `state/state.json`; escalation and decision records are
   written by the harness. No child writes merged state.
4. **#3 Verdicts from files.** The assessment is advisory (a file/stream the harness parses, never an exit
   code); the breakdown output is gated by `guardrails validate` (deterministic); escalations are files.
5. **#4 SSOT.** This doc **proposes** every contract delta by name (§"SSOT deltas"); it edits no SSOT file.
6. **#6 Worktree isolation.** Autonomous mode changes nothing about isolation: the breakdown invoker reads
   the integration worktree via `--add-dir`; the user's checkout stays read-only.

## 3. The unified dial model — the core decision

### 3.1 The question: extend, compose, or reinterpret `autonomyPolicy`?

The shipped `autonomyPolicy` (SSOT §2.1, GR2031) is a 3-value enum — `prompt | halt | auto` — with crisp,
load-bearing per-value semantics consumed by wave-drift, task-drift (`SafeSuffixEvaluator`), and the
overwatcher. Its defining invariant is *`auto` authorizes application of a **provably SAFE** action, never
an UNSOUND one.* Crucially, `auto` today is **conservative at a judgment gate**: it applies only
deterministically-safe resolutions (a safe-suffix rewind; the overwatcher's allowlist, which in v1 further
degrades to propose). It has **no notion of criticality** and never best-guesses.

#361 wants something genuinely new: a **threshold on a criticality spectrum** that lets an unattended run
**proceed past a JUDGMENT gate on a best-guess**. Three options:

| Option | What it is | Verdict |
|---|---|---|
| **(a) Extend `autonomyPolicy` into a richer type** | Replace the 3-value enum with a struct/scale | **Rejected.** Breaks the crisp 3-value semantics GR2031 + every consumer depends on; a migration the roadmap explicitly wants to avoid. |
| **(c) Reinterpret `auto` as "dial-governed"** | Make the existing `auto` value best-guess judgment gates | **Rejected — dangerous.** A CI run that today sets `auto` purely for *safe drift auto-resolution* would silently start best-guessing past `needsHuman` questions. Conflates two orthogonal axes (how to resolve a *known-safe* decision vs. risk-appetite for an *unknowable* one). Violates SRP. |
| **(b) A NEW orthogonal `dial` field that COMPOSES with `autonomyPolicy`** | Keep `autonomyPolicy` exactly as-is; add an orthogonal criticality threshold | **Recommended.** |

**Recommendation: Option (b) — a new orthogonal field, `escalationThreshold` (the criticality dial),
composing with the unchanged `autonomyPolicy`.** Rationale:

- The two are genuinely **orthogonal axes**. `autonomyPolicy` answers *"how do I treat a decision that has a
  known-safe resolution, and am I allowed to prompt?"* The dial answers *"how bold am I about best-guessing
  a decision that has NO known-safe resolution, when no human is present to ask?"*
- **The endpoints coincide with existing behavior**, which is the tell that this is a refinement, not a new
  mechanism: dial = *cautious* (escalate everything) ≡ today's `prompt`-non-interactive / `halt` (halt at
  every judgment gate); dial = *fully autonomous* (escalate only the top) ≡ best-guess almost everything.
  The dial **interpolates** between the two.
- It is the clean realization of what the overwatcher deferred: *"silent auto-application of judgment
  decisions is v2 bet #6."* The dial is the safety mechanism that makes that responsible.

### 3.2 How the two compose (the crisp rule)

The dial has teeth **only** when the run is proceeding unattended under `auto`. Concretely:

- `autonomyPolicy` decides the **posture**: `halt` = always halt (dial inert); `prompt` = interactive
  prompt / non-interactive halt (dial inert — a human is either present to ask, or absent and we honest-halt
  as today); `auto` = the unattended posture where the **dial engages**.
- `escalationThreshold` (the dial) decides, **at a judgment gate under `auto` in a non-interactive
  context**, whether to **escalate** or **proceed-with-best-guess**.
- **The dial NEVER lowers a floor.** A denylist/verdict-surface change, an unsound drift rewind, the review
  gate, and a terminal-exhaustion `needs-human` **always halt/escalate regardless of the dial** (§5). The
  dial only ever converts an *honest-halt-at-a-soft-judgment-gate* into a *recorded-best-guess-below-
  threshold*.

Because the dial is inert unless `autonomyPolicy: auto` AND the run is non-interactive AND
`escalationThreshold` is set, **an existing run's behavior is unchanged** — the composition is strictly
additive.

### 3.3 The dial scale (KISS: a coarse ordered enum, not a float)

Criticality is a **coarse ordered enum** — a float would be false precision (§DA-2) and un-testable. Four
levels, ascending:

```
low  <  moderate  <  high  <  critical
```

The dial's **value is the lowest criticality that still escalates** (a threshold). The rule is literally
`escalate ⟺ assessedCriticality ≥ escalationThreshold`:

| `escalationThreshold` | Escalates | Best-guesses | Character |
|---|---|---|---|
| `low` | low, moderate, high, critical | (nothing) | **cautious** — escalate ~everything (≡ interactive halt-at-every-gate) |
| `moderate` | moderate, high, critical | low | conservative |
| `high` (**recommended default for `--autonomous`**) | high, critical | low, moderate | balanced |
| `critical` | critical | low, moderate, high | **fully autonomous** — best-guess ~everything |
| `never` | (nothing) | everything | **maximally autonomous** — best-guess even critical judgment calls (still never a floor) |

The value-vs-behavior inversion (`low` = most cautious) is documented explicitly and rendered as this table
wherever the field appears, because it *is* a threshold and any other framing would be a lie.

### 3.4 The config schema (proposed — a new `autonomy` block in `guardrails.json`)

```jsonc
{
  "autonomyPolicy": "auto",                 // UNCHANGED (§2.1). Autonomous mode requires "auto"; the dial is
                                            //   inert under "prompt"/"halt". --autonomous sets this.
  "autonomy": {                             // OPTIONAL, NEW. Absent ⇒ the dial is inert (current behavior).
    "escalationThreshold": "high",          // the run-wide dial (§3.3). Default "high" when the block is
                                            //   present. GR2039 if unrecognized (proposed code).
    "gateThresholds": {                     // OPTIONAL per-gate overrides (§3.5). Any gate key absent ⇒ the
                                            //   run-wide escalationThreshold applies.
      "needs-human":     "moderate",
      "wave-checkpoint": "high",
      "review-gate":     "escalate"         // SPECIAL — a floor, NOT a criticality level (§5). "escalate"
                                            //   (default) or the explicit acknowledgment "proceed-unreviewed".
    },
    "blockerRetry": {                       // OPTIONAL bounded wait for a hard-blocker-retryable (§4.2).
      "maxAttempts": 5,                     // ceiling on retries before escalating a retryable blocker
      "totalWaitSeconds": 900               // ceiling on cumulative wait; reuses the transient-pause budget
                                            //   discipline (transientPauseBudgetSeconds is the floor)
    }
  }
}
```

- **Every field is optional.** The whole block absent ⇒ the dial is inert ⇒ the run behaves exactly as
  today. This is the backward-compatibility guarantee.
- `--autonomous` (alias `--unattended`) is CLI sugar that sets `autonomyPolicy: auto` + an `autonomy` block
  with `escalationThreshold: high` if the config omits one. `--dial <level>` overrides the run-wide
  threshold. Combining `--autonomous` with an interactive-only expectation is a usage error.
- **A conservative default is deliberate.** `--autonomous` alone escalates high + critical (best-guesses
  only low + moderate). Reaching *fully autonomous* requires the operator to type `--dial critical` (or
  `never`) — an explicit, deliberate act, never a default.

### 3.5 Per-gate overrides — the three gate types

`gateThresholds` keys are the three judgment-gate types the dial governs:

| Gate key | Fires at | Dial-eligible? |
|---|---|---|
| `needs-human` | an agent-emitted `{"needsHuman": "..."}` (SSOT §9) | **Yes** — a best-guess answers the question and continues (§5). |
| `wave-checkpoint` | the JIT between-wave checkpoint, next wave unauthored (SSOT §14.4, #360) | **Yes** — a best-guess auto-invokes `plan-breakdown` (doc 11 §9). |
| `review-gate` | a freshly-authored/edited wave that is unreviewed | **NO — a floor.** Its `gateThresholds` value is not a criticality level; it is the escalate/`proceed-unreviewed` acknowledgment (§5). |

The `review-gate` key deliberately lives in the *same* per-gate map so the operator sees all three gates in
one place — but its value space is different, precisely so that setting the run-wide dial to `critical`
(or `never`) can **never accidentally clear the review gate**. Clearing it requires typing the named
acknowledgment `proceed-unreviewed` against that specific key (§5).

## 4. Classify-then-act — the model

Every stop an unattended run hits is **classified first**, then acted on. Classification is
**deterministic wherever the harness already has the signal** (invariant 1); a judge only ever *widens* the
retryable set, and its widening is recorded and bounded (§4.3).

| Class | Definition | Governed by | Action |
|---|---|---|---|
| **(a) Judgment call** | A gate with no deterministic answer where a best-guess exists (design question; wave breakdown) | **the dial** | assess criticality → escalate (≥ threshold) or proceed-with-recorded-best-guess (< threshold) |
| **(b) Hard blocker, retryable/transient** | An external/recoverable condition (rate limit, 503, a service momentarily down) | **NOT the dial** | bounded wait + backoff/retry (config ceiling), then re-evaluate |
| **(c) Hard blocker, permanent OR retry-exhausted** | No best-guess exists and no retry will clear it (missing credential, permission wall, DB unreachable after ceiling) | **NOT the dial** | **halt-and-escalate unconditionally** with full failure context |

**The dial governs judgment calls only.** A hard blocker has no best-guess to make — best-guessing a missing
credential is nonsensical — so the dial never applies to (b)/(c). This is the load-bearing separation #361
insists on.

### 4.1 Where classification is deterministic (the harness already knows)

The harness maps its **existing** signals onto classes with zero judge involvement:

| Existing signal | Class | Source |
|---|---|---|
| `PromptFailureKind.Transient` (429/503/529, overloaded, rate/session/usage limit) | **(b)** | `ClaudeSignalClassifier` (SSOT §9); already backs off via the transient-pause budget — reuse verbatim |
| Permission wall (`permission-denied`, missing granted path) | **(c)** | SSOT §9.3 (#266) — already halts unconditionally |
| Infrastructure fault / `RunAbort` (git unavailable, executor threw) | **(c)** | SSOT §7 (#150) — already an honest abort, exit 1 |
| Plan/wave **preflight failure** (dependency not materialized, env not ready) | **(c)** | SSOT §3.3/§14.3 — the environment is not ready; a best-guess cannot fix it → escalate |
| Terminal-exhaustion `needs-human` (task could not converge to green) | **floor (c)-like** | SSOT §9.2.1 — a doomed/exhausted task is NEVER best-guessed past (invariant 5) |
| No-op deadlock (#174/#264) / max-turns / write-scope loop | **floor** | the overwatcher's deterministic floor (doc 11 §8) — unchanged |
| Agent-emitted `{"needsHuman": "..."}` | **(a)** | SSOT §9 — the agent explicitly asked a human; a *judgment call* → dial applies |
| JIT wave checkpoint, next wave unauthored | **(a)** | SSOT §14.4 (#360) → dial applies |

The only genuinely NEW deterministic classification autonomous mode adds is recognizing `needsHuman` and the
JIT checkpoint as **dial-eligible judgment calls** instead of unconditional halts. Everything else is a
reuse.

### 4.2 The bounded wait/backoff for class (b)

Reuse the shipped transient-pause discipline (bounded by `transientPauseBudgetSeconds`, SSOT §9) and bound
it additionally with the `autonomy.blockerRetry` ceiling:

- Retry the same attempt with backoff (honoring any parsed reset hint) **until** either `maxAttempts` OR
  `totalWaitSeconds` is reached (whichever first).
- On success → continue as if the blocker never happened.
- On ceiling → **escalate to class (c)** (halt-and-escalate unconditionally) with the full failure context
  AND the retry ledger (how many attempts, how long waited).

This never consumes the task's retry budget (a transient failure is not a logic failure — the shipped rule).

### 4.3 "Record the classification is itself a judgment" — the honesty requirement

Classifying a hard blocker as *retryable* is a **judgment, not a fact** when the signal is ambiguous. The
design keeps invariant 1:

- **Known signals are deterministic** and need no judge (the table in §4.1). A KNOWN-transient failure is
  class (b) as a fact.
- **An UNKNOWN/ambiguous failure is NOT silently treated as retryable.** It defaults to `Error` → class
  (c) → escalate. The safe default is escalate, not spin.
- A judge MAY *widen* the retryable set (classify an unknown failure as transient-retryable) **only** with
  an explicitly recorded rationale — and that record carries **why it was judged retryable, how many
  attempts were made, and how long was waited before escalating**. This is a `decisions[]` +
  `autonomy.jsonl` record (§6), so the classification judgment is auditable exactly like a best-guess.

The result: the deterministic classifier is the authority for the dangerous cases (a missing credential is
never spun on); a judge can only ever make the harness *more patient* with an ambiguous transient, bounded
and recorded.

## 5. The verdict-surface / review-gate floor — what the dial may NEVER clear

The overwatcher's mechanical asymmetry (doc 11 §3) is preserved **verbatim and extended to the dial**: a
machine may never soften a deterministic guardrail's verdict or self-attest a `/guardrails-review`. Under
autonomous mode this becomes three hard floors the dial cannot lower **at any threshold, including
`never`**:

1. **A deterministic guardrail/preflight verdict.** Untouched — the dial governs gates, not guardrails. A
   best-guess that produces wrong work fails the deterministic guardrails → honest halt. The dial cannot
   even *see* a guardrail verdict.
2. **A verdict-surface change** (the overwatcher DENYLIST: any guardrail/preflight body, or `writeScope` /
   `scope` / `dependsOn` / `integrationGate`). Classified by the SAME `OverwatchFixClassifier`; always
   routes to human + `/guardrails-review`, never auto-applied — dial or no dial.
3. **The review gate** (`mark-reviewed`). **The harness NEVER writes a review marker on a human's behalf, at
   any dial setting.** Marking a wave reviewed when no human ran the adversarial pass would claim verified
   output is verified when it is not (invariant 5 + the #360 review-gate invariant).

### 5.1 Which gates the dial MAY clear vs. MAY NOT — the explicit list

| Gate | Dial may clear? | Best-guess behavior when below threshold |
|---|---|---|
| Agent `needsHuman` question | **YES** | inject a recorded best-guess answer into the next attempt's composed prompt; continue. The task's deterministic guardrails still gate the result. |
| JIT wave checkpoint (invoke breakdown) | **YES** | auto-invoke `plan-breakdown` for the next wave (doc 11 §9); `guardrails validate` gates the output. |
| `mark-reviewed` (review gate) | **NO — floor** | the harness never self-attests; see §5.2 for the unattended resolution. |
| A denylist verdict-surface change | **NO — floor** | always propose-to-human (doc 11 §3.1). |
| An unsound drift rewind | **NO — floor** | always halts regardless of policy (SSOT §7.2). |
| Terminal-exhaustion `needs-human` | **NO — floor** | a task that could not reach green is never best-guessed past. |

### 5.2 The unattended review-gate resolution (#360 Q4, reconciled)

In a fully-unattended firstmate run, the harness will auto-invoke `plan-breakdown` for a JIT wave (§5.1) and
then reach the **review gate** for that freshly-authored wave. It cannot self-attest (§5, floor 3). What
happens? Two honest options, and I recommend one with the tradeoff stated:

- **Option E (default) — escalate to firstmate.** The harness halts the wave, records a `review-gate`
  escalation ("wave-NN authored but unreviewed; a human must run `/guardrails-review` before it runs") with
  full context, and surfaces it via `IEscalationSink`. Safe, honest, but **stalls the unattended run at
  every JIT wave** — limiting for a JIT-waved plan.
- **Option P (opt-in) — proceed-with-recorded-unreviewed-risk.** The harness runs the auto-broken-down wave
  **without a review marker**, recording a prominent, durable `decisions[]` entry
  (`decision: "proceeded-unreviewed"`) and flagging the run's final verdict as *"ran with N unreviewed
  waves."* The wave's tasks still pass their **deterministic** guardrails (the real verification); only the
  **adversarial review pass** — which hunts the cheapest wrong implementation that passes those guardrails —
  is skipped, and that skip is **indelible** in the trail. The run can NEVER be reported as
  fully-reviewed-green.

**Recommendation: default to Option E (`review-gate: "escalate"`); offer Option P ONLY as an explicit,
named per-gate acknowledgment (`gateThresholds.review-gate: "proceed-unreviewed"`).**

Rationale and tradeoff, stated not hidden:

- Option E preserves the honest-halt floor absolutely and is the correct default: a firstmate crew that
  wants review keeps it.
- Option P **never marks a wave reviewed** — it proceeds *explicitly unreviewed*, recorded as such. It does
  not violate "never self-attest"; it declines to attest and says so, loudly and durably. The risk it
  transfers to the operator is precisely the adversarial review's value (weak guardrails let a wrong
  implementation through, uncaught). It is a deliberate, recorded risk transfer — the honest form of an
  escape hatch, versus the dishonest alternative a crew would otherwise reach for (hand-writing a review
  marker, i.e. a **forged attestation**, which is strictly worse).
- Making Option P a *named acknowledgment* rather than a dial level means turning the run-wide dial to
  `critical`/`never` can never accidentally skip review — the operator must specifically type
  `proceed-unreviewed` against the `review-gate` key. Deliberate, not incidental.

This is the resolution to #360's Q4 open question ("escalate vs. proceed-with-recorded-risk"): **both,
escalate by default, proceed only under an explicit named opt-in.**

## 6. The forensic contract — an explicit, testable guarantee

**Autonomous mode is FORENSICALLY NON-LOSSY.** The contract:

> For **every gate** the interactive flow would have surfaced to a human, an unattended run leaves a durable
> on-disk record sufficient to reconstruct: **(a)** that the gate occurred; **(b)** how it was classified
> (§4); **(c)** the assessed criticality + confidence (for a judgment call); **(d)** what was decided and
> the rationale/best-guess; **(e)** for a hard-blocker-retryable, the attempts + wait before
> resolution/escalation. A green autonomous run whose trail is missing any auto-cleared gate's record is a
> **harness bug** — a test asserts this invariant. Autonomous mode MUST NOT reduce the trail the interactive
> flow already persists (run journal, `state/run.json` + `state/state.json`, per-task/attempt logs with
> cost/provenance/segment-branch/worktree-path, each wave's diagram).

### 6.1 What lands on disk after every auto-cleared gate

1. **A `decisions[]` entry** in `state/run.json` (the durable audit) — the extended shape below.
2. **An `autonomy.jsonl` record** in `logs/<runId>/` (the detail behind the audit entry) — the full
   assessment: rationale, confidence, best-guess text, the classification and its own rationale (§4.3).
3. **The gate-specific artifact** the decision produced:
   - `needs-human` best-guess → the best-guess answer is injected into the next attempt's **composed
     prompt** (so the attempt transcript shows exactly what was decided);
   - wave-checkpoint best-guess → the breakdown transcript under `logs/<runId>/<wave-dir>/breakdown/`
     (doc 11 §9) + `guardrails validate` output;
   - class-(b) retry → the retry ledger (attempts, cumulative wait) in the `autonomy.jsonl` record;
   - escalation → an escalation record under `logs/<runId>/escalations/` (§7).
4. **Everything the interactive flow already writes**, unchanged (the non-reduction clause).

### 6.2 The `decisions[]` schema deltas (proposed)

The shipped `DecisionEntry` (SSOT §2.1/§7) is `{ boundary, policy, decision, at, subject, headline,
detail }`. Autonomous mode **reuses the existing `boundary` discriminator** (`task` for a `needsHuman` gate;
`wave` for a JIT-checkpoint or review-gate gate — no new boundary needed) and **adds optional fields**:

| New field (optional) | Type | Meaning |
|---|---|---|
| `gate` | string | the specific gate: `needs-human` \| `wave-checkpoint` \| `review-gate` \| `blocker` |
| `classification` | string | `judgment-call` \| `hard-blocker-retryable` \| `hard-blocker-permanent` |
| `criticality` | string | the assessed level (`low`\|`moderate`\|`high`\|`critical`); null for a hard blocker |
| `confidence` | string | the judge's confidence (`low`\|`moderate`\|`high`); null for a hard blocker |
| `threshold` | string | the `escalationThreshold` in force at this gate (after per-gate override) |
| `bestGuess` | string | the recorded best-guess taken when `decision = proceeded-best-guess`; null otherwise |
| `blockerAttempts` | int | class-(b) retries before resolution/escalation; null otherwise |
| `blockerWaitedSeconds` | int | class-(b) cumulative wait before resolution/escalation; null otherwise |
| `assessmentRef` | string | relative path to the `autonomy.jsonl` record backing this entry |

New **`decision`** tokens (extending `halted | prompted-approved | prompted-declined | auto-applied`):

| New token | When |
|---|---|
| `escalated` | criticality ≥ threshold (judgment call) OR a hard-blocker escalation |
| `proceeded-best-guess` | criticality < threshold; best-guess recorded and taken |
| `proceeded-unreviewed` | the review-gate opt-in (§5.2, Option P) |
| `blocker-retried` | a class-(b) transient resolved within the ceiling (may be recorded once per gate, resolved) |

All additions are **optional/additive** — an existing `decisions[]` consumer (the CLI renderer, the log
viewer) ignores unknown fields; the shipped `drift`/`task`/`wave` entries are unchanged.

### 6.3 `autonomy.jsonl` (proposed — §8 log layout addition)

Run-level (gates span tasks AND waves, unlike the per-task `overwatch.jsonl`):
`logs/<runId>/autonomy.jsonl` — an append-only stream, one compact JSON object per gate assessment:

```jsonc
{ "at":"2026-07-18T14:03:11Z", "gate":"needs-human", "boundary":"task", "subject":"03-wire-config",
  "classification":"judgment-call", "criticality":"low", "confidence":"high",
  "threshold":"high", "decision":"proceeded-best-guess",
  "question":"Use System.Text.Json or Newtonsoft for the config reader?",
  "bestGuess":"System.Text.Json — it is the repo's existing serializer (see src/**/*.csproj).",
  "rationale":"Low blast radius; reversible; the deterministic guardrails gate the result." }
```

The durable *audit* is `decisions[]`; this is the multi-fire *detail* — the exact overwatcher pattern
(`decisions[]` + `overwatch.jsonl`) one level up. An escalation, a best-guess, AND a class-(b) retry each
append one record.

## 7. The firstmate escalation seam

#361 requires surfacing an escalation to firstmate "as a real decision" without over-coupling to firstmate's
internals. **Recommendation: an `IEscalationSink` seam whose production implementation is FILE-BASED —
the harness records the question + full context to a well-defined path and signals an escalation; firstmate
(or any orchestrator) polls those files.** This mirrors the shipped `IOverwatchInteraction` /
`IRunObserver` seams and honors invariant 6 (plain files, no daemon/SaaS).

### 7.1 The seam (Core)

```csharp
public interface IEscalationSink
{
    void Escalate(EscalationRequest request);      // record + signal; never blocks, never waits for a reply
    static IEscalationSink File { get; }           // the default: write to logs/<runId>/escalations/ + decisions[]
}

public sealed record EscalationRequest
{
    public required string Gate { get; init; }          // needs-human | wave-checkpoint | review-gate | blocker
    public required string Subject { get; init; }       // task id / wave dir
    public required string Question { get; init; }       // the human-answerable question
    public required string Context { get; init; }        // full context (logs pointers, failure detail, best-guess considered)
    public string? Criticality { get; init; }            // assessed level; null for a hard blocker
    public required DateTimeOffset At { get; init; }
}
```

### 7.2 What the file-based sink does (the minimal recommendation)

An escalation **is an honest halt enriched with a machine-readable question** — so the sink is a thin layer
over shipped machinery, NOT a new transport:

1. Write a structured record to `logs/<runId>/escalations/<seq>-<gate>.json` — a well-defined, pollable
   location (invariant 6). This is the machine-readable question + context.
2. Append a `decisions[]` entry (`decision: "escalated"`, the §6.2 fields) — the durable audit.
3. Emit `IRunObserver.DecisionRecorded` — so a live UI / stdout shows the escalation as it happens.
4. Behave like a per-unit `needs-human`: the escalated unit halts and its dependents `block`; **independent
   branches keep running** (the shipped semantics). A `review-gate` escalation on a waved plan halts the
   wave (the barrier stops later waves). The run ends **exit 2** if any escalation is unresolved.

firstmate consumes escalations by polling `logs/<runId>/escalations/` (or reading `decisions[]` / watching
stdout). **The harness never knows firstmate exists** — it writes files and sets an exit code. That is the
whole seam. It is the `needsHuman`-to-disk pattern shipped today, generalized and given a well-known
directory. Do not invent a socket, a queue, or a callback — YAGNI and it would violate invariant 6.

### 7.3 What the seam deliberately does NOT do

- It does **not** block waiting for firstmate to answer (that would make the harness a server — a daemon,
  forbidden by invariant 6). An escalation is fire-and-halt; resolution is a **resume** after a human/
  firstmate acts (edits the plan, answers via a resume input, runs `/guardrails-review`), which the existing
  resume machinery already handles.
- It does **not** encode a reply channel in v1. If firstmate needs to *inject* an answer for a resumed run,
  that is a follow-up (a resume-time input file), flagged as an open decision (§Open F).

## 8. Devil's-advocate self-critique

**DA-1 (strongest) — the criticality judge is exactly the "prompt-judge as verdict authority" the product
exists to forbid.** A firstmate run best-guessing past a `needsHuman` on an LLM's *self-assessment of its
own risk* is the fox guarding the henhouse. *Response:* three things keep it from being that. (1) The judge
**never clears a deterministic verdict** — every task still passes its own guardrails; the dial only decides
whether to *proceed past a gate that has no deterministic answer*. The worst case of a wrong best-guess is a
task that then **fails its deterministic guardrails → honest halt/needs-human anyway.** The deterministic
floor catches a bad best-guess. (2) The default dial is conservative (escalate high+critical); fully
autonomous requires a deliberate `--dial critical`. (3) Everything is recorded and the run's work sits on
the plan branch **undelivered until green** — a wrong best-guess is auditable and reversible. **Accepted
residual:** a best-guess that happens to satisfy *weak* guardrails is the same action-side-gaming residual
the overwatcher already accepts (doc 11 §11) — the mitigation is the product's existing one (strong
deterministic guardrails + the review pass), and review-skipping is exactly what the review-gate floor
(§5.2 default E) prevents.

**DA-2 — "criticality" is unmeasurable; the dial is a false sense of control.** An LLM cannot reliably rank
its own decisions' criticality; the threshold is theater. *Response:* real tension, partially conceded. The
dial is **coarse** (4 levels), the default escalates the top half, and — critically — the genuinely
dangerous stops (missing credentials, permission walls, unreachable services) are classified
**deterministically** and **never dialed** (§4). So the dial only governs the soft "should I ask a human
about this design choice" gates, where separating *trivial* from *consequential* is something an LLM does
adequately, and low-confidence assessments **fail safe to escalate**. We are not asking for precision — just
a coarse, fail-safe sort.

**DA-3 — YAGNI: this is a large surface for a v2 bet.** *Response:* it decomposes into thin seams over
shipped machinery — the dial composes with `autonomyPolicy`; classify-then-act reuses `PromptFailureKind` +
the transient-pause budget + the permission wall + `RunAbort`; escalation reuses honest-halt + `decisions[]`
+ `IRunObserver`; the breakdown invoker reuses `IPromptRunner` + the `overwatch`-profile pattern +
`guardrails validate`. The only genuinely new pieces are one orthogonal config block, extended records, and
an `IEscalationSink` seam. It is **phased** (§9) so each phase is independently valuable and independently
shippable.

**DA-4 — the review-gate opt-in (`proceed-unreviewed`) is a hole; a crew will set it and ship unreviewed
weak guardrails.** *Response:* accepted residual, named. Default is escalate. The opt-in **never marks
reviewed**, the run can **never** report fully-reviewed-green, and the skipped review is **indelible**. It is
a deliberate recorded risk transfer, honest and loud — strictly better than the dishonest workaround it
prevents (a hand-forged review marker). Removing it would make autonomous mode useless for JIT-waved plans
and push crews toward forgery. Better an honest escape hatch than a dishonest one.

**DA-5 — a best-guess that answers a `needsHuman` mid-attempt corrupts the attempt's semantics.** The agent
asked because it genuinely did not know; injecting a guessed answer may send it down a wrong path that burns
the whole retry budget. *Response:* true, and the mitigation is the budget itself — a wrong best-guess costs
at most the task's retry budget and then honest-halts (needs-human), fully recorded. The dial's default
(escalate high+critical) keeps the *consequential* questions human. A `needs-human` best-guess is only ever
taken for a *low/moderate* judgment the crew has explicitly opted to automate.

**DA-6 — two independent autonomy knobs (`autonomyPolicy` + `escalationThreshold`) is the exact policy
multiplication the unified-policy decision (SSOT §2.1) fought to avoid.** *Response:* they are **orthogonal
axes**, not two knobs for one decision — one governs *known-safe resolution + prompt posture*, the other
*risk appetite for unknowable judgment gates*. Collapsing them (Option a/c, §3.1) was evaluated and rejected
as either a breaking change or a dangerous silent semantic expansion. The composition is strictly additive
and inert by default, so it does not re-fragment the shipped policy — it *extends* `auto` along a new axis.

## 9. Phasing

Each phase is independently valuable and independently shippable; the draft-PR review (#106) gates the whole
DoR before Phase 1 starts.

- **Phase 1 — inter-wave breakdown invocation (#360 Phase 1/2).** The `breakdown` prompt-runner profile, the
  between-wave actor in `Scheduler.RunWavedAsync`, `guardrails validate` as the deterministic gate, the
  `BreakdownComplete`/`BreakdownFailed` halts, the transcript log site. Designed in doc 11 §9. **Governed by
  the shipped `autonomyPolicy` alone** (the design-360 table) — Phase 1 needs NO dial. Ships first; delivers
  auto-breakdown-then-review-halt (Option E) with zero dial dependency.
- **Phase 2 — the dial for `needs-human` + JIT checkpoint.** The `autonomy` config block, the deterministic
  gate classifier, the criticality assessment prompt (advisory), the threshold compare, the `decisions[]`
  deltas + `autonomy.jsonl`. Lights up `needs-human` best-guess and dial-governed auto-invocation of
  breakdown. This is the core of #361.
- **Phase 3 — classify-then-act hard-blocker handling + the escalation seam.** `blockerRetry` bounds, the
  class-(b)/(c) wiring over the existing transient/permission-wall/abort signals, `IEscalationSink` +
  `logs/<runId>/escalations/`, the run-level exit-2-with-pending-escalations semantics.
- **Phase 4 — the review-gate policy + overwatcher `auto`-tier allowlist.** The `review-gate` per-gate
  acknowledgment (Option P opt-in), and — as a natural consequence — the overwatcher's **allowlist**
  (guidance/budget) levers become dial-governed silent auto-apply under `auto` (realizing the *action/budget*
  half of overwatcher v2 bet #6; the DENYLIST stays permanently human-only).

## 10. Open decisions — for maintainer

Carried forward from `design-360-auto-wave-breakdown.md` (still open, now reframed under the dial):

- **A. `autonomyPolicy: "auto"` and the review pause (design-360 Q1, now §5.2).** Confirmed recommendation:
  `auto` governs breakdown *invocation*; the review gate is a **floor** the dial clears only via the explicit
  named `gateThresholds.review-gate: "proceed-unreviewed"`. Does the maintainer accept Option E as the
  default and Option P as a named opt-in — or should review be an **absolute** floor with no opt-in at all
  (autonomous mode simply cannot run a JIT-waved plan end-to-end unattended)?
- **B. `wave.json` metadata alongside `brief.md` (design-360 Q2).** Still recommend `brief.md` only. Does
  the criticality assessment or the breakdown-prompt composition need structured per-wave metadata (target
  stack, parent-plan pointer) that would justify a `wave.json`? Current answer: no.
- **C. Breakdown transcript log location (design-360 Q3).** Recommend `logs/<runId>/<wave-dir>/breakdown/`
  (doc 11 §9). Confirm vs. a plan-level location.

New for #361:

- **D. Dial granularity + names.** Recommend the coarse ordered enum `low < moderate < high < critical`
  (+ `never`) with the "value = lowest criticality that still escalates" threshold semantics (§3.3).
  Confirm the level names (alternatives: `trivial/minor/major/severe`) and whether `never` (best-guess even
  critical judgment calls) should exist at all.
- **E. The dial field name + shape.** Recommend a new `autonomy` block with `escalationThreshold` +
  `gateThresholds` + `blockerRetry` (§3.4), composing with the unchanged `autonomyPolicy`. Confirm the block
  name (alternatives: `unattended`, `criticality`) and that a NEW orthogonal field (Option b) — not
  extending/reinterpreting `autonomyPolicy` — is the chosen model.
- **F. Criticality-assessment authority.** Recommend a hybrid: deterministic gate-classification + a
  constrained advisory LLM assessment (the reserved `overwatch`/`assess` profile) that is NEVER the verdict
  authority; malformed/absent ⇒ escalate (§2 invariant 1, §4.3). Confirm the profile name and whether the
  assessment should reuse the `overwatch` profile or get its own `assess` profile.
- **G. Hard-blocker retry-ceiling defaults.** Recommend `blockerRetry: { maxAttempts: 5, totalWaitSeconds:
  900 }`, floored by the shipped `transientPauseBudgetSeconds` (§4.2). Confirm the defaults.
- **H. Per-gate override syntax.** Recommend the `gateThresholds` map with keys `needs-human` /
  `wave-checkpoint` / `review-gate` (§3.5), the `review-gate` key taking the escalate/`proceed-unreviewed`
  acknowledgment rather than a criticality level. Confirm.
- **I. The escalation seam shape.** Recommend a file-based `IEscalationSink` writing
  `logs/<runId>/escalations/<seq>-<gate>.json` + `decisions[]` + observer, fire-and-halt, no reply channel
  in v1 (§7). Confirm — and decide whether a resume-time answer-injection file (§7.3) is in scope or a
  follow-up.
- **J. The GR code for an invalid dial value.** The next-free code is **GR2038** (GR2035 DuplicateCheckName,
  GR2036 ExpectedDurationNonPositive, GR2037 BannedGuardrailPattern are taken); `design-360-auto-wave-
  breakdown.md` earmarks GR2038 for a *deferred* "warn on wave stub without `brief.md`". Recommend the dial
  takes **GR2039** and leaves GR2038 for that earmark — confirm, or reassign if the earmark is dropped.
- **K. `--autonomous` default dial.** Recommend `--autonomous` alone sets `escalationThreshold: high`
  (conservative — best-guess only low/moderate); *fully autonomous* requires an explicit `--dial
  critical`/`never`. Confirm the default is conservative, not fully-autonomous.

## 11. SSOT deltas + implementation handoff

### Proposed SSOT changes (named, NOT written — a parallel worktree owns §14/§2.1)

Land each in the SAME change that implements it (invariant 4). These are **proposals** for the maintainer to
approve; then the harness developer writes them into `02-schemas-and-contracts.md`:

- **§2 (`guardrails.json`)** — add the OPTIONAL `autonomy` block (`escalationThreshold`, `gateThresholds`,
  `blockerRetry`) with all-optional/inert-by-default semantics (§3.4). Note it composes with, and does not
  replace, `autonomyPolicy`.
- **§2.1 (`autonomyPolicy`)** — add a paragraph: the dial is a NEW ORTHOGONAL axis engaging only under
  `auto` in a non-interactive context; it NEVER lowers a floor (verdict surface / review gate / unsound
  rewind / terminal exhaustion). `autonomyPolicy`'s three values and GR2031 are **unchanged**.
- **§7 (`run.json`)** — extend `decisions[]` `DecisionEntry` with the OPTIONAL fields `gate`,
  `classification`, `criticality`, `confidence`, `threshold`, `bestGuess`, `blockerAttempts`,
  `blockerWaitedSeconds`, `assessmentRef`; add the `decision` tokens `escalated`, `proceeded-best-guess`,
  `proceeded-unreviewed`, `blocker-retried` (§6.2). Additive — existing entries unchanged.
- **§8 (log layout)** — add the run-level `logs/<runId>/autonomy.jsonl` detail stream (§6.3) and the
  `logs/<runId>/escalations/<seq>-<gate>.json` escalation records (§7.2); note `logs/<runId>/<wave-dir>/
  breakdown/` is defined by doc 11 §9.
- **§9 (prompt runners)** — reference the reserved `breakdown` profile (defined in doc 11 §9) and the
  criticality-assessment profile (reuse `overwatch` or a new `assess` — Open F). Note the assessment is
  advisory (verdict-from-files; malformed ⇒ escalate).
- **§9.2 (overwatcher)** — cross-reference: under autonomous mode the overwatcher's ALLOWLIST levers become
  dial-governed auto-apply (Phase 4, the action/budget half of v2 bet #6); the DENYLIST is unchanged.
- **§14.4 (JIT checkpoint)** — the dial governs the `wave-checkpoint` gate; the review half stays a floor
  (§5.2). (Proposed to the §14-owning worktree — this doc does not edit §14.)
- **New GR code GR2039** — invalid `escalationThreshold`/`gateThresholds` value (Open J).

### Companion doc change (this branch)

- **`docs/plans/11-overwatcher.md` §9** — flesh out the inter-wave role into the complete #360 Phase 1/2
  design (the between-wave actor, the `breakdown` profile, the review-gate invariant under `auto`), and
  cross-reference this doc for the dial. (Done in this same branch.)

### Roadmap change (this branch)

- **`docs/plans/03-roadmap.md` bet #6** — note that autonomous mode (#361) is the vehicle realizing the
  *autonomy* slice of bet #6 (the `auto`-tier allowlist auto-apply) and that #360 Phase 1/2 is the
  inter-wave slice; point to `docs/plans/12-autonomous-mode.md`. (Proposed; apply after maintainer approval.)

### Implementation handoff (agents + filesTouched + sequencing)

After this DoR is approved via the #106 draft-PR loop (this doc + the doc 11 §9 extension open as a draft PR
for inline review; implementation milestones do NOT start until comments are addressed):

**Phase 1 (#360 breakdown invocation) — sequenced first, no dial dependency:**
1. `guardrails-harness-developer` — the `breakdown` profile + the between-wave actor in
   `Scheduler.RunWavedAsync`; `WaveHaltKind.BreakdownComplete`/`.BreakdownFailed`; `guardrails validate`
   gate; `overheadCostUsd` charging; the `logs/<runId>/<wave-dir>/breakdown/` log site. **SSOT edits land in
   the same change** (§9, §8, §14.4 breakdown-invocation half). `filesTouched: src/Guardrails.Core/**,
   src/Guardrails.Cli/**, docs/plans/02-schemas-and-contracts.md`.
2. `guardrails-skill-author` — `plan-breakdown` Step 9 `brief.md` input path; `guardrails-domain-knowledge`
   execution-semantics update. `filesTouched: .claude/skills/**`.
3. `guardrails-test-author` — breakdown-invoked-then-review-halt (Option E) integration test;
   `BreakdownFailed` path; validate-gate-on-output. `filesTouched: tests/**`.

**Phase 2 (the dial) — after Phase 1:**
1. `guardrails-harness-developer` — the `autonomy` config block + loader/validator (GR2039); the
   deterministic gate classifier; the criticality-assessment prompt (advisory); the threshold compare; the
   `decisions[]` deltas + `autonomy.jsonl`. **SSOT §2/§2.1/§7 edits in the same change.** `filesTouched:
   src/Guardrails.Core/**, src/Guardrails.Cli/**, docs/plans/02-schemas-and-contracts.md`.
2. `guardrails-test-author` — the decision-table matrix (dial × criticality × interactive/non-interactive);
   malformed-assessment ⇒ escalate; the forensic-non-lossy invariant test (every auto-cleared gate leaves a
   record); backward-compat (block absent ⇒ behavior unchanged). `filesTouched: tests/**`.

**Phase 3 (classify-then-act + escalation seam) — after Phase 2:**
1. `guardrails-harness-developer` — `blockerRetry` bounds over the transient/permission-wall/abort signals;
   `IEscalationSink` + `logs/<runId>/escalations/`; the exit-2-with-pending-escalations semantics.
   `filesTouched: src/Guardrails.Core/**, src/Guardrails.Cli/**, docs/plans/02-schemas-and-contracts.md`.
2. `guardrails-test-author` — classify-then-act decision table (known-transient → retry-bounded → escalate;
   unknown → escalate; permission wall → escalate); escalation-record-on-disk; independent-branches-continue.
   `filesTouched: tests/**`.

**Phase 4 (review-gate policy + overwatcher auto-tier) — after Phase 3:**
1. `guardrails-harness-developer` — the `review-gate: proceed-unreviewed` opt-in + the never-report-green
   verdict flag; the overwatcher allowlist dial-governed auto-apply (via the existing
   `IOverwatchInteraction` seam). `filesTouched: src/Guardrails.Core/**, src/Guardrails.Cli/**,
   docs/plans/02-schemas-and-contracts.md, docs/plans/11-overwatcher.md`.
2. `guardrails-test-author` — proceed-unreviewed never writes a marker; run verdict flags unreviewed waves;
   overwatcher allowlist auto-applies under `auto`, denylist still halts. `filesTouched: tests/**`.

### Testing strategy (qa-standards)

- **P0 (exhaustive) — the honest-halt floors.** Decision-table + negative tests that the dial at `never`
  still: never writes a review marker; never auto-applies a denylist op; never best-guesses past a
  terminal-exhaustion needs-human; never spins a permission wall. These are the invariant-5/asymmetry
  guarantees — the highest-risk code.
- **P0 — the forensic-non-lossy invariant.** A property/integration test: for every auto-cleared gate in a
  scripted autonomous run, assert the `decisions[]` entry + `autonomy.jsonl` record + gate artifact exist.
- **P1 — the dial decision table.** `escalationThreshold × criticality × {interactive, non-interactive} →
  {escalate, best-guess, prompt, halt}` (equivalence partitioning over the 4 levels + boundary at the
  threshold).
- **P1 — classify-then-act.** State-transition tests: known-transient → bounded-retry → resolve OR escalate
  at ceiling; unknown → escalate; each hard-blocker signal → correct class.
- **P1 — backward compat.** The `autonomy` block absent ⇒ every existing run behaves byte-identically
  (the composition is inert).
- Unit-heavy (the classifier + threshold compare are pure functions — ideal for the pyramid's base);
  integration tests for the breakdown invocation + escalation-on-disk; no E2E driver needed.
