# Autonomous mode + evidence-hygiene — implementation plan (dogfood)

> **This is a reviewed implementation plan for `/plan-breakdown` (waved).** It carries NO new design —
> the design of record is already ratified and merged. Each wave's tasks are AUTHORED against the design
> sections named below; the breakdown must not re-decide anything settled there. Lean deterministic
> (build + `dotnet test` + specific assertions) over prompt-judges; every prompt-action task gets authored
> tests as its guardrail.

## Design of record (authoritative — do not re-litigate)

- `docs/plans/12-autonomous-mode.md` — the criticality dial, classify-then-act, the escalation + reply
  (answer-injection) seam, `proceed-unreviewed`, the forensic contract. **§9 phasing** and **§11 SSOT
  deltas + implementation handoff** are the per-task source of truth (filesTouched, sequencing, GR codes).
  Status: APPROVED; C–N decided (§10).
- `docs/plans/11-overwatcher.md` §9 — the between-wave actor (shipped in Phase 1). The dial reuses the
  read-only `overwatch` profile for criticality assessment (§10 H).
- `docs/plans/16-review-attestation-provenance.md` — the #366 evidence-hygiene design (audit trail, NOT a
  boundary). §4/§5/§12 are the per-task source of truth.
- Shipped seams to extend (already on master): `Overwatch`/`OverwatchFixClassifier`, `WaveBreakdownInvoker`
  (#360 Phase 1), `AutonomyPolicy`, `DecisionEntry`/`RunJournal` (`decisions[]` + overhead cost),
  `ReviewMarker`/`MarkReviewedCommand`, `Scheduler.RunWavedAsync`, `SchedulerFactory` reserved profiles.

## Load-bearing invariants (every wave preserves these — they are the point)

1. **Deterministic over judges** — triggers/classification are deterministic; an LLM assessment is NEVER the
   verdict authority; malformed/absent ⇒ escalate.
2. **The dial never softens a deterministic guardrail verdict, never auto-approves a verdict-surface change,
   never self-attests `/guardrails-review`.**
3. **Compound-config gate (settled, GR2040):** `proceed-unreviewed` + `dial: critical` is a load-time error;
   under `proceed-unreviewed` the clamped `high`/`critical` hard calls escalate AND are **non-answerable**.
4. **The review marker is never a runtime boundary** (#366): audit trail only; `mark-reviewed` bare still
   works; the harness never writes a marker on a human's behalf.
5. **Orthogonality/back-compat:** the new `autonomy` block is inert by default; existing `autonomyPolicy`
   runs stay byte-identical; new `DecisionEntry` fields/tokens are additive.

## Waves (strict order; each wave's exit gate = clean Debug build + full `dotnet test` green)

### Wave 1 — Review-attestation evidence hygiene (#366) — INDEPENDENT of the dial
Authoritative: `16-*.md` §4/§5/§12; SSOT §13 delta.
- The additive `attestation` block on `ReviewMarker` (`source: review-artifact | bare | machine`, read-time
  `legacy`; non-authoritative `actor`/`tool`; `evidence{reportPath, reportDigest}`), `version:2`,
  `WhenWritingNull`, symmetric newline-normalized digest.
- `/guardrails-review` leaves a durable report under `state/reviews/` (hash-excluded).
- `mark-reviewed` records `source` additively — **bare still stamps + clears GR2025, never refused**;
  F2 stamp-time checks (report embeds `planHash` == marker's; `reportPath` under `state/reviews/`; else
  downgrade to `bare`).
- New read-only `guardrails plan-hash <folder>` command (OD-3) — prints `PlanDefinitionHash`.
- Amend `docs/plans/12-autonomous-mode.md` §10 K → "resolved by #366: close/rescope to audit-only" (the
  verbatim note in `16-*.md` §6).
- Guardrails: marker round-trip + back-compat (old tool reads v2; no re-stale; legacy clears GR2025),
  digest symmetry, F2 downgrade classification, `plan-hash` determinism.

### Wave 2 — The criticality dial: config surface (#361 Phase 2) — a NEW `autonomy` block
Authoritative: `12-*.md` §3.3/§3.4/§3.5, §10 F/G/I/M/N; SSOT §2.1/§14 deltas.
- The `autonomy` block composing with (never redefining) `autonomyPolicy`: `escalationThreshold`
  (`low<moderate<high<critical`), `gateThresholds` (`needs-human`/`wave-checkpoint`/`review-gate`),
  `blockerRetry {maxAttempts:5, totalWaitSeconds:900}`, `maxJudgeWidenings:3`.
- **GR2039** (invalid `escalationThreshold`/`gateThresholds` value) and **GR2040** (compound-config:
  `proceed-unreviewed` + a `critical` end-state — run-wide OR any per-gate `needs-human`/`wave-checkpoint`
  == `critical`).
- `--autonomous` (defaults `escalationThreshold: high`; **REQUIRES an effective `maxCostUsd`** — a built-in
  `$20` default with a loud warning if unset) and `--dial <level>`.
- Guardrails: config parse/validate tests; GR2039/GR2040 matrix incl. the per-gate route-around; the
  inert-by-default back-compat test (no `autonomy` block ⇒ byte-identical behavior).

### Wave 3 — Classify-then-act + the escalation & reply channel (#361 Phase 3) — JIT stub (`brief.md`)
Authoritative: `12-*.md` §4, §5.1/§5.2, §6.2, §7 (all subsections). Break down against Wave 2's shipped
config once it is materialized.
- Deterministic gate classification (judgment-call / hard-blocker-retryable / hard-blocker-permanent) mapped
  to shipped signals; criticality assessment via the read-only `overwatch` profile (never the verdict
  authority; malformed/absent ⇒ escalate; `maxJudgeWidenings` cap; widening rationale is advisory
  self-report).
- `blockerRetry` bounded wait/backoff (floored by `transientPauseBudgetSeconds`).
- File-based `IEscalationSink` (`logs/<runId>/escalations/<seq>-<gate>.json` + `decisions[]` + observer).
- The v1 answer-file contract (`…​.answer.json` co-located; verbatim `{runId,seq,gate,subject}` +
  dual-`definitionHash` binding; monotonic `seq`; CAS-guarded once-only consume; cross-`runId` bookkeeping);
  resume consumption injects for **`needs-human` + `wave-checkpoint` ONLY** (no `review-attested`);
  malformed/absent ⇒ safe re-escalate.
- The **non-answerable clamp**: under `proceed-unreviewed`, `high`/`critical` hard calls escalate and are
  non-answerable by fiat. `answer.text` injected as delimited UNTRUSTED data; verdict-surface denylist is the
  backstop.
- New `decisions[]` tokens (`escalated`, `proceeded-best-guess`, `answer-injected`, `blocker-retried`) +
  `autonomy.jsonl`.
- Guardrails: classification determinism matrix; answer-binding/stale-replay/CAS/cross-runId tests;
  non-answerable-clamp test; malformed-answer ⇒ re-escalate.

### Wave 4 — `proceed-unreviewed` + review-gate policy + overwatcher auto-tier (#361 Phase 4) — JIT stub (`brief.md`)
Authoritative: `12-*.md` §5.2, §5 floor 3, §9 Phase 4, §10 K. Break down against Wave 3.
- `proceed-unreviewed` named opt-in: **`mergeOnSuccess` defaults OFF** when any `proceeded-best-guess`/
  `proceeded-unreviewed` decision is recorded; a **distinct non-zero exit code**; the review gate is never
  auto-satisfied and never forged (no marker written).
- `gateThresholds.review-gate` handling (`escalate` default / `proceed-unreviewed` opt-in), consistent with
  the GR2040 clamp.
- Overwatcher `auto`-tier auto-apply **gated on the PRESENCE of the `autonomy` block** (not `autonomyPolicy:
  auto` alone) + the byte-identical back-compat test.
- Guardrails: `mergeOnSuccess`-OFF-on-best-guess test; distinct-exit-code test; auto-tier-gating back-compat
  test; "no marker ever written by the harness" assertion.

## Dogfood execution note

Author Waves 1–2 up front (their inputs are stable — the design is named). Leave **Wave 3** as the one
JIT stub with a `wave-03-*/brief.md`, and Wave 4 stubbed after it — so `guardrails run` honest-halts at
each checkpoint and (with `autonomyPolicy` permitting) the #360 Phase 1 auto-breakdown authors the next
wave against Wave 2/3's *materialized* code, which is exactly why later waves are JIT'd (their tasks
reference the real config/seam the prior wave ships). The human reviews each freshly-authored wave
(`/guardrails-review`) before resuming — the honest gate stays a human.
