# Wave 4 — review-gate policy + `proceed-unreviewed` + overwatcher auto-tier (#361 Phase 4)

> Auto-seeded brief for JIT breakdown at this wave's checkpoint. Authored from the parent plan
> (`autonomous-mode-impl.md`, Wave 4). Break this wave down against Wave 3's **materialized** code in the
> integration worktree — the `AutonomyConfig` dial, the classify-then-act + escalation sink, and the
> answer-injection reply channel Wave 3 shipped are the real symbols this wave builds on.

## What this wave must accomplish

The final #361 phase: the review-gate policy and the `proceed-unreviewed` opt-in, plus gating the
overwatcher's `auto`-tier. Authoritative design: `docs/plans/12-autonomous-mode.md` §5.2 (the review-gate
resolution + the compound-config clamp), §5 floor 3 (the review gate is never a runtime boundary / never
forged), §9 Phase 4, §10 K.

- **`proceed-unreviewed` — the named opt-in.** When a run records any `proceeded-best-guess` /
  `proceeded-unreviewed` decision, **`mergeOnSuccess` defaults OFF** (machine-decided work is never
  auto-delivered — #340 reconciliation), the run exits with a **distinct non-zero code** (a firstmate
  consumer can never read it as clean green), and it is permanently flagged "ran with N unreviewed waves."
  The harness **never writes a review marker on a human's behalf** and never forges an attestation.
- **`gateThresholds.review-gate` handling** — `escalate` (default) vs the explicit `proceed-unreviewed`
  acknowledgment; consistent with the settled compound-config gate (`proceed-unreviewed` + `dial: critical`
  is a GR2040 load-time error; under `proceed-unreviewed` the in-wave dial clamps so `high`/`critical` hard
  calls still escalate and are non-answerable — Wave 3's non-answerable clamp).
- **Overwatcher `auto`-tier auto-apply gated on the PRESENCE of the `autonomy` block**, NOT
  `autonomyPolicy: auto` alone — with a byte-identical back-compat test (an `autonomyPolicy: auto` consumer
  with no `autonomy` block must behave exactly as before).

## Upstream this wave builds on (materialized by Waves 1–3)

- Wave 1: the review-attestation `attestation` block + `mark-reviewed` (#366) — the marker the review-gate
  policy governs.
- Wave 2: the `AutonomyConfig` dial (`escalationThreshold`/`gateThresholds`/`blockerRetry`), GR2039/GR2040,
  `--autonomous`/`--dial`.
- Wave 3: classify-then-act, the escalation sink + `decisions[]` tokens, the answer-injection reply channel,
  the non-answerable clamp, `ExitCodes.EscalationsPending`.

## Verification intent (guardrails the breakdown should author)

Deterministic-first: a `mergeOnSuccess`-defaults-OFF-when-best-guess-recorded test; a distinct-non-zero-exit
test for a `proceed-unreviewed` run; the overwatcher auto-tier back-compat test (auto + no `autonomy` block
⇒ unchanged); and a "the harness never writes a review marker" assertion. Lean on the targeted `--filter`
for the in-attempt regression check (never the full integration suite — #253).
