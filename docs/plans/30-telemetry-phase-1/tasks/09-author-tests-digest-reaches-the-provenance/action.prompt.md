## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `09-author-tests-digest-reaches-the-provenance`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "09-author-tests-digest-reaches-the-provenance": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task authors the failing half of the *delivery* leg of section 3.3 of
`docs/plans/30-telemetry-phase-1.md`: capturing the digest off the wire (tasks 07/08) is worthless if
it never reaches `run.json`. Read section 3.3, including its `DECIDED 2026-09-01` block. Where this
prompt and the plan disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Do not touch provenance-on-failed-attempts; this task is about a NEW member riding the provenance that
section already ships.

## Task

Author **one** file, and only this one:
`tests/Guardrails.Core.Tests/Execution/ModelDigestProvenanceTests.cs`.

Class **`ModelDigestProvenanceTests`**, `public sealed`, carrying `[Trait("Category", "ModelEvidence")]`
on the class — the convention every shipped telemetry suite in this project uses (see
`tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs:15`).

Both shapes these tests need already exist when this task runs:
`Journal.AttemptProvenance.ModelDigest` (task 03) and `PromptResult.ModelDigest` /
`ActionRun.ModelDigest` (task 04). Nothing carries the value between them. **These tests must COMPILE
and FAIL at runtime**; not compiling is a mistake to fix, not the intended TDD red.

### The mechanism under test — described as authoring-time state, to be verified

Everything in this section was read off the tree while this prompt was written, and
`04-extend-the-transport-record-shape` edits two of the same files before you run. **Grep for the
markers named below; do not trust a line number, including the ones quoted here.**

- `ActionRun.FromPrompt` (grep `FromPrompt` in `src/Guardrails.Core/Execution/ActionRunner.cs`) copies
  `CostUsd`, `Usage` and `ObservedModel` off the `PromptResult` and, at authoring time, copies no
  digest.
- `TaskExecutor` then folds the observed model onto the attempt's provenance. **Grep for
  `ObservedModel is { } observedModel`** — the block reassigns `provenance` through a `with`
  expression setting `Model` and `RequestedModel`, and re-mirrors the result through
  `AttemptArtifacts.WriteProvenance`. At authoring time the digest is not part of that expression.

So the datum's whole route is `PromptResult` → `ActionRun` → the provenance `with` → the journal. Two
of the four pinned tests exist to prove that route end to end; the other two pin the shape decisions
that make it reach both settle paths.

### How to drive it — the seam, and why this one

Run a **real serial run** and read the journal, using a stub `IPromptRunner` as the only fake.

- `PromptRunnerRegistry.Build(RunConfig config, Func<PromptRunnerConfig, IPromptRunner> factory)` takes
  the runner factory as a parameter. `tests/Guardrails.Core.Tests/Journal/ExecutedDefinitionHashTests.cs`
  is the precedent for the whole fixture — grep its `RunSerialAsync` helper: a temp plan folder, a
  `StateManager`, `RunJournal.LoadOrCreate(plan)`, a `TaskExecutor`, and a `Scheduler` with
  `maxParallelism: 1` and no worktree provider. It passes a factory that throws because every fixture
  action there is a script; **you pass one that returns your stub instead**.
- `IPromptRunner` has exactly two members (`Name`, `RunAsync`), so the stub is a few lines. Have it
  return a `PromptResult` carrying whatever the test needs — `ModelDigest`, `ObservedModel`, or
  neither.
- Your fixture plan must declare a `promptRunners` block (the factory is called once per declared
  block) and its task must be a PROMPT action, so `ActionRun.FromPrompt` is on the path.

This is deliberate and it is the point: a test that hand-builds an `ActionRun` and calls a journaller
method proves the journaller and nothing about `FromPrompt`, and `FromPrompt` is where the datum is
dropped today. That is exactly how `AttemptRecord.Usage` shipped structurally dead with every guardrail
green (#475), and `ObservedModelCaptureTests`' own header records the rule: the child process is faked;
the runner interface is where the fake stops.

**One thing to know before you assume production coverage.** At authoring time
`PromptRunnerKinds.ServesRoles(PromptRunnerKind.OpenAiCompat)` is `{ Guardrail, Advisory }`, and the
Claude CLI exposes no fingerprint at all — so on today's tree no shipped runner both serves the ACTOR
role and reports a digest. The fold is still the right place for it (it is where every runner-observed
fact lands), and a stub runner is the only way to exercise it. State that in your summary; do not try
to fix it, and do not weaken a test to route around it.

### The pinned behaviours

Encode **exactly these four**, each as a `[Fact]` with **exactly the method name given**. The names are
pinned because this task's guardrail binds each behaviour to its method name in the runner's TRX; a
differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | a prompt action whose runner reported a digest lands it on the attempt's journalled provenance | `AnActionRunCarryingADigest_LandsItOnTheProvenance` |
| 2 | a runner that reported no digest leaves `AttemptProvenance.ModelDigest` null — absent, never `""` | `ADigestlessActionRun_LeavesTheProvenanceDigestNull` |
| 3 | the digest survives the observed-model fold rather than being dropped by it | `TheDigestSurvivesBesideTheObservedModelFold` |
| 4 | `ModelDigest` is declared on `AttemptProvenance` and NOT on `AttemptRecord` — by reflection | `TheDigestRidesTheProvenance_SoItReachesBothSettlePaths` |

**Behaviour 3 is the discriminator, and it must assert the whole `with` expression's output at once.**
One run whose runner reports BOTH an observed model and a digest, and whose route asked for a
*different* model; assert all three of `Provenance.Model` (the observed one), `Provenance.RequestedModel`
(the route's, present because the two disagree) and `Provenance.ModelDigest` (the reported digest).
Written that way it fails if the digest is added as a SECOND, separate fold that discards the first
one's result — records are immutable and a `with` whose result is discarded changes nothing, which is
the exact mistake the existing fold's own comment warns about.

**Behaviour 4 is a reflection test and carries the reason placement is mechanical, not cosmetic.**
Cite `src/Guardrails.Core/Journal/JournalModel.cs` — grep `Placement is D32` — which documents it: a
member hung directly off `AttemptRecord` lands in serial mode and **silently vanishes in worktree
mode**, because `Scheduler.RecordSucceededSettle` builds its own record from `PendingAttempt` and
`AttemptProvenance` is the one member that already rides it. Assert `ModelDigest` is declared on
`AttemptProvenance` **and is not declared on `AttemptRecord`** — both halves, because "present on the
provenance" alone stays true if someone later duplicates it onto the record and creates two fields
claiming one fact.

**Behaviours 2 and 4 will be GREEN when you finish, and that is correct.** Nothing populates the digest
today, so the null case already holds; and task 03 already put `ModelDigest` on `AttemptProvenance`, so
the reflection assertion already holds. Their guardrail declares both as exemptions and asserts only
that they RAN. Do not "fix" them into failing, and do not mark either `[Fact(Skip=…)]` — a skipped
exemption is no coverage at all. They exist because the implementation task edits this exact fold: one
stops an absent digest being filled with a placeholder, the other stops the member being re-hung
somewhere that reaches serial mode only.

Assert on the journal document (`RunJournal.Document.Tasks[<id>].Attempts[…].Provenance`), which is the
durable surface. Asserting on `attempt-provenance.json` instead is acceptable only as an ADDITION, never
as the whole test: the mirror is best-effort by design.

**Do NOT implement the fold.** `src/Guardrails.Core/Execution/ActionRunner.cs` and
`src/Guardrails.Core/Execution/TaskExecutor.cs` are outside this task's writeScope and belong to
`10-fold-the-digest-into-the-provenance`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Execution/ModelDigestProvenanceTests.cs` — the stub runner and every
fixture helper live inside that one file. After this task completes, the harness runs a `git diff`
check and rejects any edit outside that path — including changes to other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes
a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
