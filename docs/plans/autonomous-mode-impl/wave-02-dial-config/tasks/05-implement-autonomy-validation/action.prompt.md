## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-02-dial-config/05-implement-autonomy-validation` — NOT the stableId. (This task publishes nothing
  to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the autonomy-block **validation** (GR2039 + GR2040) in
`src/Guardrails.Core/Loading/PlanValidator.cs`, using the `DiagnosticCodes` constants the previous task
added. The design of record is `docs/plans/12-autonomous-mode.md` §3.4/§3.5/§5.2 (read first). Make the
authored `AutonomyValidatorTests` pass WITHOUT editing them.

Implement:
- **GR2039 (invalid value, ERROR)** — emit when `autonomy.escalationThreshold` is present but not one of
  `low`/`moderate`/`high`/`critical`, OR when a `gateThresholds` value is invalid (a `needs-human` /
  `wave-checkpoint` that is not a criticality level; a `review-gate` that is neither `escalate` nor
  `proceed-unreviewed`).
- **GR2040 (compound-config, ERROR)** — emit when `gateThresholds.review-gate == "proceed-unreviewed"`
  AND the reachable end-state best-guesses a hard call: `escalationThreshold == "critical"` OR any in-wave
  `gateThresholds` value (`needs-human` / `wave-checkpoint`) `== "critical"`. Key on the REACHABLE
  END-STATE (Finding 3) so a per-gate `{ "needs-human": "critical", "review-gate": "proceed-unreviewed" }`
  under `escalationThreshold: "high"` is caught — a per-gate override must not route around it.
- Do NOT emit GR2040 when `proceed-unreviewed` sits at the cautious/`high` dials with no reachable
  `critical` (it is allowed there). Follow the existing validator's diagnostic-emission style
  (severity, message shape) — see how neighbouring GR20xx checks are written.
- **Factor the GR2040 core into a REUSABLE predicate (B1 — load-bearing).** Implement the GR2040 check
  as a `public static` predicate/method on `PlanValidator` (e.g.
  `bool ViolatesCompoundConfig(AutonomyConfig effective, out string diagnostic)` or equivalent) that
  takes an ARBITRARY effective autonomy config and returns whether it hits the forbidden
  `proceed-unreviewed` + reachable-`critical` end-state, plus the GR2040 diagnostic string. Load-time
  validation calls this predicate — its load-time behaviour is UNCHANGED — but the core MUST also be
  callable on an effective config that a later stage produces. This exists because `--dial`/`--autonomous`
  (task 07) mutate the config AFTER load-time validation runs, so GR2040 must be re-checkable on the
  effective config, not inline-only; task 07 `dependsOn` this task and CALLS this predicate.

Do NOT change the autonomy PARSE (a prior task owns it) or `DiagnosticCodes.cs` (the constants already
exist). Validation only.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Loading/PlanValidator.cs`. Do NOT edit the authored tests or `DiagnosticCodes.cs` —
if a test is genuinely wrong, emit `{"needsHuman": "<why>"}` rather than changing it (an out-of-scope
edit fails the write-scope check and burns a retry).

Completion criteria (your guardrail checks these): `AutonomyValidatorTests` and the existing
`PlanValidatorTests` in `tests/Guardrails.Core.Tests` all pass.
