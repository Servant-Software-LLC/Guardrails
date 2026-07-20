## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/15-wire-classify-then-act-into-scheduler` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

**Wire classify-then-act into the production run path** (issue #361 Phase 3; the #120 composition-root
lesson) so the escalation machinery is reachable from the real CLI, not only from unit tests. Make the
authored `SchedulerEscalationWiringTests` (task 14) pass by WIRING — never by weakening the test. The
design of record is `docs/plans/12-autonomous-mode.md` §4 (classify-then-act), §5.1 (which gates the dial
may clear), §7.2 (the sink behaviour), §7.6 (resume consumption). All the component types already exist
on the branch (from the ancestor tasks); this task CONSTRUCTS and INJECTS them.

**Architecture caveat (#203) — verify before you build on it.** Cite DURABLE markers (grep the symbols),
never line numbers. Anchor on: `SchedulerFactory.Create` + the reserved profile const
`SchedulerFactory.OverwatchRunnerProfile` (`src/Guardrails.Core/Execution/SchedulerFactory.cs`); the
needs-human settle path and the JIT `wave-checkpoint` block (grep the banner `// --- #360 Phase 1: the
between-wave breakdown actor at the JIT checkpoint` in `src/Guardrails.Core/Execution/Scheduler.cs`);
`ActionRunner` (the SOLE caller of `PromptComposer.ComposeAction`, grep it) for threading the injected
answer/best-guess text; `RunAsync`'s autonomy resolution + `ExitCodes` in the CLI. These reflect the
plan-authoring-time state — confirm each still holds in the materialized tree before assuming its shape.

Wire (each closes a live-from-CLI gap; the terminal whole-suite build/test does NOT cover any of this):
- **`SchedulerFactory.Create`**: when the plan carries an `autonomy` block AND the run is non-interactive
  under `autonomyPolicy: auto`, CONSTRUCT `GateClassifier`, `BlockerRetry`, `CriticalityJudge` (given the
  resolved `overwatch`-profile runner), `FileEscalationSink`, and `AnswerFileConsumer`, and inject them
  into the `Scheduler`/`TaskExecutor` it builds. When the `autonomy` block is ABSENT, construct NONE of
  them — behaviour is byte-identical to today (the inert-by-default guarantee).
- **`Scheduler`**: at a `needsHuman` gate (and the JIT `wave-checkpoint`), run the deterministic
  `GateClassifier` → for a judgment-call, run `CriticalityJudge` → escalate (via `FileEscalationSink`) or
  proceed-best-guess (append the best-guess to the next attempt through `ActionRunner`); for a class-(b)
  transient, run `BlockerRetry`; for class-(c) / floor, halt-and-escalate. Independent branches keep
  running (shipped semantics). Record every auto-cleared gate (decisions[] + `autonomy.jsonl`) — the
  forensic-non-lossy contract (§6).
- **Resume**: before a unit re-hits an escalated gate, run `AnswerFileConsumer` (§7.6) — inject a valid
  answer via `ActionRunner` → `PromptComposer.ComposeAction`'s delimited-untrusted section; otherwise
  re-escalate.
- **`RunCommand` / `ExitCodes`**: add a NEW constant `ExitCodes.EscalationsPending = 4` (the next free
  value after the shipped `Success=0`/`HarnessError=1`/`TaskFailed=2`/`Cancelled=3`) and return it when
  the run ends with **unresolved escalations** (an answer-required halt). Do **NOT** reuse `2`
  (`TaskFailed`/needs-human) — the whole point (§7.1) is that a firstmate consumer can tell an
  answer-required halt apart from a plain needs-human and never read either as clean green.

Do NOT change the component types' internal logic (they are done + tested); this task is construction +
injection + gate dispatch + exit-code surfacing only.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~SchedulerEscalationWiringTests"`
(and any Core filter you need). Do NOT run the full unfiltered
`dotnet test tests/Guardrails.Integration.Tests`: its fixture-leaking classes (`HarnessWriteRunTests`,
`RetrySalvageTests`, `ScriptActionReproductionShortCircuitTests`, `WriteScopeCheckTests`) drop
`outside.txt` / `src/output.txt` / `docs/outside.txt` into the worktree → write-scope false-positive
rollback (#253).

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/SchedulerFactory.cs`, `src/Guardrails.Core/Execution/Scheduler.cs`,
`src/Guardrails.Core/Execution/ActionRunner.cs`, `src/Guardrails.Cli/Commands/RunCommand.cs`, and
`src/Guardrails.Cli/ExitCodes.cs`. Do NOT edit the authored wiring test or the component types' files —
if the wiring genuinely needs a component-type change, emit `{"needsHuman": "<why>"}` rather than editing
out of scope (an out-of-scope edit fails the write-scope check and burns a retry).

Completion criteria (your guardrails check these): `SchedulerFactory.Create` constructs the escalation
machinery under an `autonomy` block, and `SchedulerEscalationWiringTests` pass (the real factory
escalates/best-guesses/answer-injects observably).
