## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/09-implement-criticality-assessment` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the **criticality-assessment decider** (issue #361 Phase 3) by filling REAL logic over the
`CriticalityJudge` stub the previous task authored. The design of record is
`docs/plans/12-autonomous-mode.md` §3.2/§3.3 (the threshold rule), §4.3 (the widening cap + the
verdict-from-files safe default), §5.2 (the clamp), §10 H (reuse the read-only `overwatch` profile). Make
the authored `CriticalityAssessmentTests` pass WITHOUT editing them.

Implement:
- **The advisory assessment**: run the constrained assessment prompt through the injected `IPromptRunner`
  (the reserved read-only `overwatch` profile — grep `SchedulerFactory.OverwatchRunnerProfile`; reuse the
  diagnose-class read-only shape, §10 H). Parse the advisory result into criticality + confidence +
  (when below threshold) a best-guess + rationale. **The judge is NEVER the verdict authority:** a
  malformed / absent / errored assessment ⇒ **escalate** (invariant 1, verdict-from-files, §4.3).
- **The deterministic threshold compare**: escalate ⟺ `assessedCriticality >= effectiveThreshold`, where
  `effectiveThreshold` applies a per-gate `gateThresholds` override over the run-wide
  `escalationThreshold` (`EscalationThreshold` order `Low<Moderate<High<Critical`). Below threshold ⇒
  proceed-best-guess (carry the best-guess text + rationale).
- **The `proceed-unreviewed` clamp (§5.2, Blocker 1)**: when `gateThresholds.review-gate ==
  ProceedUnreviewed`, an assessed `high`/`critical` **ALWAYS escalates**, overriding the run-wide dial and
  every per-gate override. (This is the runtime mirror of the load-time `PlanValidator.ViolatesCompoundConfig`
  GR2040 predicate — grep it; the clamp is the runtime half of the same "never best-guess a hard call
  under an unreviewed wave" invariant.)
- **The `maxJudgeWidenings` run-level cap (§4.3)**: the judge may reclassify an UNKNOWN failure as
  transient-retryable at most `AutonomyConfig.MaxJudgeWidenings` times across the run; once spent, a
  further unknown failure escalates deterministically. The widening rationale is recorded (advisory
  self-report), and the widening counts against the cap.

The decider DECIDES only — it returns a decision object; it does NOT call the escalation sink or inject
the best-guess into a prompt (the composition-root wiring task owns the act). Do NOT modify
`AutonomyConfig` or `PlanValidator`; REUSE their shipped semantics.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~CriticalityAssessmentTests"`. Do
NOT run the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes
drop `outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/CriticalityJudge.cs`. Do NOT edit the authored tests or the shipped types —
if a test is genuinely wrong, emit `{"needsHuman": "<why>"}` rather than changing it (an out-of-scope edit
fails the write-scope check and burns a retry).

Completion criteria (your guardrail checks these): `CriticalityAssessmentTests` pass.
