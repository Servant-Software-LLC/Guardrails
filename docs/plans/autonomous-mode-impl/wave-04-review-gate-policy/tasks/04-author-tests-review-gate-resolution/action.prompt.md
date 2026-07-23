## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/04-author-tests-review-gate-resolution` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit test (TDD red) for the **unattended review-gate resolution** (issue #361 Phase 4,
doc 12 §5.2 Option E/Option P + §5 floor 3 "never forged"). This test drives the REAL Scheduler wave loop
(the #120 composition-root discipline — do NOT test a hand-injected resolver in isolation); the resolution
is NOT wired yet, so the test must FAIL against current code (`tests-fail-on-current-code`).

The repo uses **xUnit (xunit.v3)**. **Mirror the shipped Core wave-loop test
`tests/Guardrails.Core.Tests/SchedulerWaveExecutionTests.cs`** for shape — how it builds a waved plan,
drives `Scheduler.RunWavedAsync` (via `SchedulerFactory.Create` / the shipped test harness), and asserts on
the journal / `RunReport`. **REUSE that class's existing temp-git-repo fixture** (grep how
`SchedulerWaveExecutionTests` / `WaveExecutionRunTests` create their repo — a Windows-safe disposable
fixture already exists; do NOT hand-roll a new temp-git helper, #116).

**Anchor on these REAL shipped symbols (grep them — durable markers, never line numbers):**
- `Scheduler.RunWavedAsync` (`src/Guardrails.Core/Execution/Scheduler.cs`) — the wave loop; grep the JIT
  banner `// --- #360 Phase 1: the between-wave breakdown actor at the JIT checkpoint` and the
  `WaveHaltKind.BreakdownComplete` handling to see where a freshly-authored/unreviewed wave is reached.
- `ReviewGateDecision` enum (`src/Guardrails.Core/Model/AutonomyConfig.cs`) — `Escalate` (default) /
  `ProceedUnreviewed`; reached via `plan.Config.Autonomy?.GateThresholds?.ReviewGate`.
- `DecisionTokens.ProceededUnreviewed` (`= "proceeded-unreviewed"`) and `IEscalationSink` /
  `FileEscalationSink` — already shipped (wave 3). The `review-gate` escalation `gate` value is
  `"review-gate"`.
- `ReviewMarker` (`src/Guardrails.Core/Review/ReviewMarker.cs`) — `ReviewMarker.PathFor(planDirectory)` ⇒
  `state/guardrails-review.json`; the marker file the harness must NEVER write on a human's behalf.

Write ONE artifact (in scope): `tests/Guardrails.Core.Tests/SchedulerReviewGateTests.cs` — tests that
FAIL against current code, asserting the review-gate resolution for an **unreviewed** wave:

1. **`proceed-unreviewed` ⇒ the wave RUNS + a `proceeded-unreviewed` decision is recorded.** With
   `gateThresholds.review-gate == proceed-unreviewed` (under `autonomyPolicy: auto` + an `autonomy` block,
   non-interactive), the unreviewed wave is NOT halted for review; it runs, and the run's `decisions[]`
   carries a `DecisionTokens.ProceededUnreviewed` entry (boundary `wave`).
2. **default (escalate) ⇒ the wave HALTS with a `review-gate` escalation + does NOT run.** With the
   review-gate at its default (`escalate` / absent), the unreviewed wave halts (Option E): a `review-gate`
   escalation is recorded (via the sink / `decisions[]` `escalated`), the wave's tasks do NOT run, and the
   run does not report the wave green-and-reviewed.
3. **Neither path writes a review marker (the §5 floor 3 invariant).** After BOTH resolutions, assert
   `ReviewMarker.PathFor(planDirectory)` (`state/guardrails-review.json`) does NOT exist — the harness
   never self-attests / forges an attestation, at any dial setting.

The tests MUST COMPILE (against the shipped symbols) and FAIL against current code (the resolution is
unwired). If you cannot deterministically reach the unreviewed-wave review boundary from a Core Scheduler
test (e.g. it only fires on the JIT `BreakdownComplete` path), do NOT guess a fragile setup — write
`{"needsHuman": "how should a Core test reach the unreviewed-wave review boundary in RunWavedAsync (JIT BreakdownComplete vs an unreviewed pre-authored wave)?"}` to the state-out path and stop. This is a real
design seam the wiring task (05) also depends on; surfacing it beats a flaky test.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~SchedulerReviewGateTests"`. Do NOT
run the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (fixture leak, #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/SchedulerReviewGateTests.cs`. After this task the harness runs a `git diff`
check and rejects any edit outside this path — including `Scheduler.cs` (task 05 owns the wiring), the
shipped symbol files, or the existing git fixture. An out-of-scope edit fails the task immediately and
consumes a retry. If a shipped type is missing a member you need, do NOT edit it — write
`{"needsHuman": "<what is missing>"}` and stop.
