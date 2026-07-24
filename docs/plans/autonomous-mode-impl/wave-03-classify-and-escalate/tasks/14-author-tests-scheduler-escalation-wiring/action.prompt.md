## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/14-author-tests-scheduler-escalation-wiring` — NOT the stableId. (This
  task publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING **composition-root wiring test** (issue #361 Phase 3; the #120 recurring lesson) that
proves the classify-then-act + escalation machinery is actually WIRED into the production run path — not
merely reachable from unit tests. The repo uses **xUnit (xunit.v3)**; this is an INTEGRATION test —
mirror `tests/Guardrails.Integration.Tests` (grep for how existing tests drive the scheduler in-process;
`AutonomousModeCliTests.cs` shows the CLI-in-process pattern, and the overwatch/needs-human integration
tests show the scheduler-in-process pattern).

**Authoring-time caveat (#203):** the NEXT task (`15-wire-classify-then-act-into-scheduler`) will wire
`SchedulerFactory.Create`; at THIS task's authoring time the factory is UNWIRED — that is exactly why the
test must FAIL now (RED against the unwired factory). Cite DURABLE markers (grep the symbols), never line
numbers. Anchor on: `SchedulerFactory.Create` and the reserved profile const
`SchedulerFactory.OverwatchRunnerProfile` (`= "overwatch"`) in
`src/Guardrails.Core/Execution/SchedulerFactory.cs`; the needs-human halt path in
`src/Guardrails.Core/Execution/Scheduler.cs`; the escalation record shape `logs/<runId>/escalations/<seq>-<gate>.json`
and the `decisions[]` tokens (`escalated`, `proceeded-best-guess`, `blocker-retried`, `answer-injected`)
from the sibling tasks. Verify the current shapes in the materialized tree before assuming them.

**The #120 rule — DRIVE THE REAL FACTORY, never inject the seam yourself:**
- Build a small plan with an `autonomy` block (dial set so a chosen gate lands above/below threshold) and
  register a **FAKE `overwatch`-profile prompt runner** via the plan's `promptRunners` config (this is
  the legitimate way to supply a deterministic assessment — it is NOT injecting the seam). Then call
  **`SchedulerFactory.Create(...)`** and run it in-process to a `needsHuman` gate.
- **FORBIDDEN:** do NOT `new Scheduler(..., new FileEscalationSink())` / do NOT construct the classifier /
  judge / sink / consumer directly in the test — that makes the test pass even when the factory is
  unwired, which is the exact anti-pattern #120 forbids. Drive the REAL `SchedulerFactory.Create`.

Assert the wired-only observable behaviour (each fails now, passes once task 15 wires it):
- **At/above threshold**: hitting a `needsHuman` gate under autonomous mode writes an escalation record
  to `logs/<runId>/escalations/…json` AND appends a `decisions[]` entry with `decision == escalated`.
- **Below threshold**: a low/moderate `needsHuman` records `decision == proceeded-best-guess` and the
  best-guess is appended to the next attempt's composed prompt (as delimited untrusted data).
- **Class-(b) transient**: a transient blocker records `decision == blocker-retried` (bounded) rather
  than escalating immediately.
- **Resume answer-injection**: a valid `…answer.json` on resume yields `decision == answer-injected`
  and the escalation `status` flips to `consumed`.
- **Exit code**: a run ending with an unresolved escalation exits with the NEW distinct code
  `ExitCodes.EscalationsPending` (`= 4`, §7.1) — assert on that constant, NOT `2`/`TaskFailed`
  (a plain needs-human) and NOT `0`. Task 15 adds the constant; assert the code so the wiring test
  proves the answer-required halt is distinguishable from needs-human.

Write ONE artifact: `tests/Guardrails.Integration.Tests/SchedulerEscalationWiringTests.cs`. It MUST
COMPILE (all referenced types exist from the ancestor tasks) and FAIL against the unwired factory (TDD
red = `tests-fail-on-current-code`).

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~SchedulerEscalationWiringTests"`.
Do NOT run the full unfiltered `dotnet test tests/Guardrails.Integration.Tests`: it contains
fixture-leaking classes (`HarnessWriteRunTests`, `RetrySalvageTests`,
`ScriptActionReproductionShortCircuitTests`, `WriteScopeCheckTests`) that drop `outside.txt` /
`src/output.txt` / `docs/outside.txt` into the worktree → write-scope false-positive rollback (#253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/SchedulerEscalationWiringTests.cs`. After this task the harness runs
a `git diff` check and rejects any edit outside this path — including `SchedulerFactory.cs` /
`Scheduler.cs` (task 15 wires those). An out-of-scope edit fails the task immediately and consumes a
retry. If wiring the test needs a production seam that does not exist, do NOT add it — write
`{"needsHuman": "<what is missing>"}` and stop.
