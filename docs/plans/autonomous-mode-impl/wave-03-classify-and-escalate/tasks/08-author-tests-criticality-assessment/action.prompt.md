## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/08-author-tests-criticality-assessment` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the **criticality-assessment decider** (issue #361 Phase 3,
doc 12 §3.2/§3.3, §4.3, §5.2), plus the MINIMAL stub to compile. The repo uses **xUnit (xunit.v3)**.

**Anchor on REAL shipped types (grep — durable markers):** the decider reads
`AutonomyConfig` (`EscalationThreshold` enum `Low<Moderate<High<Critical`, `GateThresholds`,
`ReviewGateDecision.ProceedUnreviewed`, `MaxJudgeWidenings` — `src/Guardrails.Core/Model/AutonomyConfig.cs`,
all materialized by wave-02) and runs its advisory assessment through the shipped `IPromptRunner`
(`src/Guardrails.Core/Prompts/`) using the reserved read-only **`overwatch`** profile
(`SchedulerFactory.OverwatchRunnerProfile` = `"overwatch"`), exactly as the overwatcher's diagnose does
(§10 H). Your tests INJECT A FAKE `IPromptRunner` — never a real prompt call — so the decider's logic is
unit-tested deterministically.

Design the new `CriticalityJudge` to take an injected `IPromptRunner` (the fake) + an `AutonomyConfig` +
the gate context, and RETURN a decision object (escalate | proceed-best-guess, with criticality,
confidence, bestGuess, rationale). It DECIDES only — it does not itself call the escalation sink or
inject the best-guess (the wiring task does that).

Write two artifacts (both in scope):

1. **The test file** `tests/Guardrails.Core.Tests/CriticalityAssessmentTests.cs` — tests that must FAIL
   against the stub:
   - **Threshold compare (the boundary)**: with the fake runner returning criticality `C`, the decider
     escalates ⟺ `C >= escalationThreshold` (test the boundary: at threshold ⇒ escalate; one below ⇒
     proceed-best-guess). Cover a per-gate `gateThresholds` override applying over the run-wide dial.
   - **Malformed / absent assessment ⇒ escalate (invariant 1, §4.3)**: a fake runner that returns
     unparseable/empty output ⇒ the decider ESCALATES (the safe default — the judge is NEVER the verdict
     authority; verdict-from-files).
   - **The `proceed-unreviewed` clamp (§5.2, Blocker 1)**: under `review-gate: proceed-unreviewed`, an
     assessed `high` OR `critical` **ALWAYS escalates**, overriding the run-wide dial AND any per-gate
     override (e.g. even a per-gate `needs-human: low`). A `low`/`moderate` call is unaffected.
   - **`maxJudgeWidenings` run-level cap (§4.3)**: the judge may widen an UNKNOWN failure to retryable
     only up to `MaxJudgeWidenings` times across the run; once the cap is spent, a further unknown
     failure escalates deterministically. (Model the run-level counter as an injected/threaded state so
     the test can drive it to the cap.)
   - **Widening rationale is advisory self-report**: when the judge widens, the decision records the
     rationale text, but the record is advisory (not an independent check) — assert the decision carries
     the rationale AND that the widening still counted against the cap.

2. **The minimal stub**: `src/Guardrails.Core/Execution/CriticalityJudge.cs` — the `CriticalityJudge`
   type (injected `IPromptRunner`), the decision result record, and an `AssessAsync(...)`/`Decide(...)`
   method that is a THROWING stub so the tests COMPILE but FAIL (TDD red). Do NOT implement the real
   assessment/threshold logic.

   The tests MUST COMPILE and FAIL (not compiling is a mistake).

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~CriticalityAssessmentTests"`. Do
NOT run the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes
drop `outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/CriticalityAssessmentTests.cs` and
`src/Guardrails.Core/Execution/CriticalityJudge.cs`. After this task the harness runs a `git diff` check
and rejects any edit outside these paths — including `AutonomyConfig.cs`, `IPromptRunner`, or
`SchedulerFactory.cs`. An out-of-scope edit fails the task immediately and consumes a retry. If a shipped
type is missing a member you need, do NOT edit it — write `{"needsHuman": "<what is missing>"}` and stop.
