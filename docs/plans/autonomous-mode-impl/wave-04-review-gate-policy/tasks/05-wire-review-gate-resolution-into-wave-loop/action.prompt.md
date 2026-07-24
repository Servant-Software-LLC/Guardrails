## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/05-wire-review-gate-resolution-into-wave-loop` — NOT the stableId. (This
  task publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

**Wire the unattended review-gate resolution into the wave loop** (issue #361 Phase 4, doc 12 §5.2
Option E/Option P + §5 floor 3). The review-gate config surface, the `ReviewGateDecision` enum, the GR2040
compound-config check, the `IEscalationSink`, and the `proceeded-unreviewed` decision token ALL already
exist (waves 2–3). Your slice is the missing RESOLUTION in `Scheduler.RunWavedAsync`: consult the
review-gate threshold at an unreviewed wave and act. Make the authored `SchedulerReviewGateTests` pass by
WIRING (do NOT weaken them).

**Architecture caveat (#203) — verify before you build on it.** Cite DURABLE markers (grep the symbols),
never line numbers. Confirm each still holds in the materialized tree before assuming its shape:
- `Scheduler.RunWavedAsync` (`src/Guardrails.Core/Execution/Scheduler.cs`) — the wave loop. Grep the JIT
  banner `// --- #360 Phase 1: the between-wave breakdown actor at the JIT checkpoint` and the
  `WaveHaltKind.BreakdownComplete` handling: that halt (the run stops for review after an auto-breakdown)
  is TODAY the review gate's only enforcement (doc 12 §5 floor 3(a)). Your resolution consults the
  configured threshold at the unreviewed-wave boundary and either keeps that halt (Option E, now recorded
  as a `review-gate` escalation) or proceeds (Option P).
- `plan.Config.Autonomy?.GateThresholds?.ReviewGate` → the `ReviewGateDecision` enum
  (`src/Guardrails.Core/Model/AutonomyConfig.cs`): `Escalate` (default / null) vs `ProceedUnreviewed`.
- The shipped classify-then-act machinery already threaded into the Scheduler by wave 3 — grep
  `FileEscalationSink` / `IEscalationSink` / `escalationSink` and the `RunJournal` decision-recording path
  (grep how a `DecisionEntry` is appended today, e.g. `RecordDecision` / `decisions`). REUSE them; do not
  re-create a sink or a decision writer.
- `DecisionTokens.ProceededUnreviewed` (`= "proceeded-unreviewed"`) — the token to record on Option P.
  The `review-gate` escalation uses gate value `"review-gate"` (the sink already maps it to the `wave`
  boundary — grep `"review-gate" => "wave"` in `FileEscalationSink.cs`).

Wire it (design of record doc 12 §5.2):
- At an **unreviewed** wave under `autonomyPolicy: auto` with an `autonomy` block (non-interactive),
  consult `plan.Config.Autonomy.GateThresholds?.ReviewGate`:
  - **`ProceedUnreviewed` (Option P)** → record a `DecisionTokens.ProceededUnreviewed` decision (boundary
    `wave`, subject = the wave dir) via the shipped journal decision path, then RUN the wave (skip the
    review halt). The run can never be reported fully-reviewed-green (the recorded decision is indelible).
  - **`Escalate` / null (Option E, the default)** → record a `review-gate` escalation via the shipped
    `IEscalationSink` and HALT the wave (the existing BreakdownComplete-style halt), so a human runs
    `/guardrails-review`. Later waves stay blocked behind the barrier (shipped semantics).
- **NEVER write a review marker on a human's behalf** (doc 12 §5 floor 3 — the harness never self-attests
  and never forges an attestation, #375). Do NOT call `ReviewMarker.Write` / `MarkReviewedCommand.WriteMarker`
  and do NOT write `state/guardrails-review.json` from any code path you add. (A guardrail enforces this
  as a fail-on-present check scoped to `Scheduler.cs`.)
- Stay consistent with the settled compound-config clamp (already shipped): `proceed-unreviewed` +
  `dial: critical` is a GR2040 load-time error, and under `proceed-unreviewed` the in-wave dial clamps so
  `high`/`critical` hard calls escalate non-answerably (`CriticalityJudge` + `AnswerFileConsumer` already
  enforce this — do NOT duplicate or weaken it).

Do NOT change the escalation sink / judge / config types (they are done + tested). This task is the wave-loop
resolution only.

**In-attempt regression check (issue #253 + #374 — do NOT skip, and run it PLAINLY):** run ONLY your
targeted filter, via the **Bash tool**, as a **plain** command — no `&` call-operator, no pipe, no
`2>&1 |`, not the PowerShell tool (issue #374 blocks those as "multiple operations requiring approval"):

    dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~SchedulerReviewGateTests"

(and `dotnet build Guardrails.sln -c Debug` to confirm the whole solution still compiles). Do NOT run the
full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop
`outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs`. The
harness runs a post-action `git diff` membership check and REJECTS any edit outside this path — including
the authored test, the sink/judge/config types, `RunReport.cs` (task 09), or `RunCommand.cs`/`ExitCodes.cs`
(task 10). An out-of-scope edit fails the task immediately and consumes a retry. If the resolution genuinely
needs a change to another file (e.g. the journal exposes no decision-recording seam you can reuse), do NOT
edit it — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Completion criteria (your guardrails check these): `Scheduler.cs` references `ReviewGate` and records
`ProceededUnreviewed`; it never writes a review marker; and the authored `SchedulerReviewGateTests` pass.
