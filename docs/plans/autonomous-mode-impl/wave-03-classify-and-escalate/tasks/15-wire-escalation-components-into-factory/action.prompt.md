## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/15-wire-escalation-components-into-factory` — NOT the stableId. (This
  task publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

**Wire the escalation machinery at the production composition root — PART 1 of 3** (issue #361 Phase 3;
the #120 composition-root lesson). This split re-authors the over-sized former task 15 (it timed out at
`maxTurns:75`); tasks 16 and 17 finish the reply channel and the exit code. Your slice: the
**SchedulerFactory.Create ↔ Scheduler classify-then-act wire**. All component types already exist on the
branch (from the ancestor tasks 05/07/09/11/13); you CONSTRUCT the run-level ones and dispatch to them.

**Architecture caveat (#203) — verify before you build on it.** Cite DURABLE markers (grep the symbols),
never line numbers. Confirm each still holds in the materialized tree before assuming its shape:
- `SchedulerFactory.Create` and `SchedulerFactory.CreateExecutor` (`src/Guardrails.Core/Execution/SchedulerFactory.cs`);
  note the reserved profile const `SchedulerFactory.OverwatchRunnerProfile` (`= "overwatch"`) — the
  overwatch-profile `IPromptRunner` the `CriticalityJudge` needs is resolved there already (grep
  `ResolveOverwatchRunner`). The factory ALREADY constructs run-level collaborators (`Overwatch`,
  `GitWorktreeProvider`, `AiMergeWorker`, `WaveBreakdownInvoker`) and passes them to `new Scheduler(...)` —
  mirror that exact pattern.
- `Scheduler` constructor (grep `public Scheduler(`) — you ADD new optional params (default `null`) for the
  injected components, following the existing `aiMergeWorker`/`breakdownInvoker` optional-param style.
- The needs-human settle path and the JIT `wave-checkpoint` block in `Scheduler.cs` (grep the banner
  `// --- #360 Phase 1: the between-wave breakdown actor at the JIT checkpoint`).
- The static classifier `GateClassifier.Classify(GateSignal)` (grep `static class GateClassifier`) — it is
  a STATIC class, so the Scheduler CALLS `GateClassifier.Classify(...)` directly; you do NOT `new` it.

**Component construction reality (READ THIS — it is why your structural guardrail lists 3, not 5):**
- Construct in the **factory** and inject into the `Scheduler` (these are RUN-LEVEL — one instance per run,
  because they hold run-scoped state): **`new FileEscalationSink(...)`** (monotonic never-reused seq),
  **`new CriticalityJudge(...)`** (the run-level `WideningLedger` maxJudgeWidenings cap; give it the
  resolved `overwatch`-profile runner), **`new BlockerRetry(...)`** (the run-level ceilings/budget/delay).
- `GateClassifier` is a **static class** — never constructed; the Scheduler dispatch calls
  `GateClassifier.Classify(signal)`.
- `AnswerFileConsumer` is **NOT constructed here** — it is `new`ed at its resume use-site in task 16
  (constructing + injecting it here would leave an unused field until task 16's resume path lands, and this
  repo builds with `TreatWarningsAsErrors=true`, so an unused field FAILS the build).
- Construct each with an **explicit `new <Type>(...)`** (the structural guardrail greps for
  `new FileEscalationSink` / `new CriticalityJudge` / `new BlockerRetry` in `SchedulerFactory.cs` — do NOT
  use a target-typed `new(...)`).

Wire it (each closes a live-from-CLI gap; the terminal whole-suite build/test does NOT cover any of this):
- **`SchedulerFactory.Create`**: when the plan carries an `autonomy` block AND the run is non-interactive
  under `autonomyPolicy: auto`, construct the 3 run-level components above and inject them into the
  `Scheduler` (new optional constructor params). When the `autonomy` block is ABSENT, construct NONE of
  them — behaviour is byte-identical to today (the inert-by-default guarantee).
- **`Scheduler`**: at a `needsHuman` gate (and the JIT `wave-checkpoint`), run the deterministic
  `GateClassifier.Classify` → for a judgment-call, run `CriticalityJudge` → escalate (via
  `FileEscalationSink`) or RECORD proceed-best-guess (the `decisions[]` entry + the best-guess text; the
  actual injection of that text into the next attempt is task 16); for a class-(b) transient, run
  `BlockerRetry`; for class-(c) / floor, halt-and-escalate. Independent branches keep running (shipped
  semantics). Record every auto-cleared gate (decisions[] + `autonomy.jsonl`) — the forensic-non-lossy
  contract (§6). The RESUME answer-consumption path (`AnswerFileConsumer`) and the ActionRunner→PromptComposer
  best-guess/answer INJECTION are task 16 — do NOT build them here (leaving a component's field unused would
  break the `TreatWarningsAsErrors` build).

Do NOT change the component types' internal logic (they are done + tested); this task is construction +
injection + gate dispatch only. Design of record: `docs/plans/12-autonomous-mode.md` §4 (classify-then-act),
§5.1 (which gates the dial may clear), §7.2 (sink behaviour).

**In-attempt regression check (issue #253 + #374 — do NOT skip, and run it PLAINLY):** run ONLY your
targeted filter, via the **Bash tool**, as a **plain** command — no `&` call-operator, no pipe, no
`2>&1 |`, not the PowerShell tool (issue #374 blocks those as "multiple operations requiring approval"):

    dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~GateClassifierTests"

(and `dotnet build Guardrails.sln -c Debug` to confirm the whole solution still compiles). Do NOT run the
full unfiltered `dotnet test tests/Guardrails.Integration.Tests`: its fixture-leaking classes
(`HarnessWriteRunTests`, `RetrySalvageTests`, `ScriptActionReproductionShortCircuitTests`,
`WriteScopeCheckTests`) drop `outside.txt` / `src/output.txt` / `docs/outside.txt` into the worktree →
write-scope false-positive rollback (#253). If a `git checkout <ref> -- <path>` salvage recovery is blocked
by the permission wall (#374), do NOT fight it — re-author the file directly with `Write` instead.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/SchedulerFactory.cs` and `src/Guardrails.Core/Execution/Scheduler.cs`. The
harness runs a post-action `git diff` membership check and REJECTS any edit outside these two paths —
including `ActionRunner.cs` / `PromptComposer.cs` (task 16), `RunCommand.cs` / `ExitCodes.cs` (task 17), the
authored wiring test, or any component type's file. An out-of-scope edit fails the task immediately and
consumes a retry. If the wire genuinely needs a change to a component type or another file, do NOT edit it —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Completion criteria (your guardrails check these): `SchedulerFactory.cs` constructs `FileEscalationSink`,
`CriticalityJudge`, and `BlockerRetry`; `Scheduler.cs` calls `GateClassifier` and references the sink at the
classify-then-act gate; and the whole solution builds.
