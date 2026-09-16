## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `08-author-tests-resource-supply-brief`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-author-tests-resource-supply-brief": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author failing tests AND the minimal stubs for the overwatcher's **missing-resource brief** — design 41
§2.3 — plus the **drive-the-real-seam proof** (#382) that task 09 is gated on.

**Test file:** `tests/Guardrails.Core.Tests/OverwatchResourceSupplyBriefTests.cs` (new)
**Test class:** `OverwatchResourceSupplyBriefTests`
**Stub files:** `src/Guardrails.Core/Execution/OverwatchTrigger.cs` and
`src/Guardrails.Core/Execution/Overwatch.cs`

Every test you write carries `[Trait("Category", "OverwatchSupply")]`. That value is **new**: the existing
Supply-area tests carry `Category="Supply"` (11 files, measured), and the plan's baseline preflight excludes
the plan trait by EXACT `Category!=` match, so the two must not be confused.

### Stub 1 — the trigger (enum member ONLY)

Add to `OverwatchTrigger` in `src/Guardrails.Core/Execution/OverwatchTrigger.cs`:

```csharp
/// <summary>
/// A task settled <c>needs-human</c> with a question naming a workspace path that is committed in the
/// operator's checkout but missing from the run's base (design 41 §2.1). The ONE consult on an
/// agent-emitted <c>needsHuman</c>, and only at an effective <c>needs-human</c> threshold of
/// <c>critical</c> in worktree mode.
/// </summary>
MissingResource
```

**Add the member and NOTHING else in that file.** Do **not** add a case to `OverwatchTriggers.Token` — its
`_ => throw new ArgumentOutOfRangeException(...)` default IS the stub, and it is what makes
`TheMissingResourceTriggerTokenIsMissingResource` red. Task 09 adds the case mapping it to
`"missing-resource"`.

Nothing switches exhaustively over this enum, so the new member cannot break another file: the only readers
are `OverwatchTriggers.Token` (a `switch` with a `_` default), `Overwatch.IsFloorBoundary` (a `!=`
comparison) and `GateClassifier` (an `or` pattern list) — all measured.

### Stub 2 — the proposal entry point

Add to `Overwatch` in `src/Guardrails.Core/Execution/Overwatch.cs`, beside `EvaluateAsync`, `internal` like
its neighbours (both test projects have `InternalsVisibleTo`):

```csharp
internal async Task<OverwatchProposal?> ProposeResourceSupplyAsync(
    TaskNode task,
    PlanDefinition plan,
    int attempt,
    string needsHumanQuestion,
    IReadOnlyList<MissingResourceCandidate> candidates,
    string taskLogDir,
    RunJournal journal,
    IRunObserver observer,
    CancellationToken ct) => throw new NotImplementedException();
```

**`MissingResourceCandidate` is NOT yours to declare.** Task 03 landed
`src/Guardrails.Core/Execution/MissingResourceFacts.cs`. **Read that file** and use the candidate record
type it exposes — it carries at least the workspace-relative path, the checkout `HEAD` sha the blob was read
at, and the checkout branch. Adjust the parameter type to whatever that file actually names. Do **not**
declare a copy in `Overwatch.cs`: two shapes for one fact is how the Scheduler ends up converting between
them. If the file does not exist, or exposes no such type, stop with
`{"needsHuman": {"question": "...", "kind": "blocked-work"}}`.

Add nothing else to `Overwatch.cs`. In particular do not write the brief builder — that is task 09.

### The brief's first line is PINNED, exactly

```
# Overwatch resource supply: task '<id>' (attempt <n>, trigger: missing-resource)
```

It is distinct from `# Overwatch diagnose:` (`Overwatch.cs:560`) and `# Criticality assessment:`
(`CriticalityJudge.cs:362`), and that distinctness is load-bearing: all three share the `overwatch` runner
profile, and the §7 wiring proof's fake CLI ROUTES ON THIS LINE. A mistyped heading makes every "no brief
was sent" control in that proof pass while checking nothing.

### Pin these behaviours to these EXACT method names

- `TheBriefsFirstLineIsThePinnedResourceSupplyHeading` — assert the composed prompt's FIRST line equals the
  pinned heading for a known task id and attempt number. Assert the whole line, not a `Contains` on a
  fragment: the fake CLI matches a prefix, so a heading that is merely *similar* fails in the proof and
  nowhere else.
- `TheBriefStatesHarnessFactsFirst_AndDelimitsTheAgentsQuestionAsUntrusted` — #709's rule, applied from the
  start. The agent's `needsHumanQuestion` appears inside a delimited block marked UNTRUSTED, the harness
  facts come first, and the brief carries the instruction that the model must not assert anything about
  files, tests, other tasks or plan-level gates beyond them. Assert the question text does not also appear
  OUTSIDE that block — model-authored text presented as harness fact is exactly what #709 cost.
- `TheBriefTablesEveryCandidateWithItsSourceShaAndBranch` — pass TWO candidates and assert BOTH appear, each
  with its path, its "absent at run base" fact, and the checkout sha and branch it would be read from.
  Two, not one: a builder that renders only `candidates[0]` passes a single-candidate test, and design 41
  §2.5 lets the model propose any subset of several.
- `TheBriefOffersOnlyTheResourceSupplyFixVocabulary` — the brief admits
  `{"kind":"resource-supply","path":"<a path from the candidate table>"}` and offers NONE of the four
  shipped op kinds (`guidance`, `budget`, `file-edit`, `task-field`). A brief that offers a budget bump
  invites a fix this gate can never certify.
- `TheMissingResourceTriggerTokenIsMissingResource` — `OverwatchTriggers.Token(OverwatchTrigger.MissingResource)`
  is `"missing-resource"`. Red on the stub because the `Token` switch has no case and throws.
- `TheResourceSupplyFixKindTokenIsResourceSupply` — the **fix-kind** token. This is a DIFFERENT member from
  the trigger token above, and it is the one design line 349 calls out: `Overwatch.FixKindToken` must map
  `OverwatchFixKind.ResourceSupply` to `"resource-supply"` instead of falling to today's `_ => "unknown"`
  arm. **`FixKindToken` is `private static`, so do NOT call it directly and do NOT widen it to test it.**
  Assert the **observable** instead: drive a proposal carrying one `resource-supply` op and assert the
  `overwatch.jsonl` detail record's `fixes[]` entry has `Kind == "resource-supply"`. That record is built
  through `FixKindToken(c.Fix.Kind)`, so the observable transitively proves the mapping — and it is the
  assertion that actually matters, because `fixes[].kind` is what a human and #529's repair loop read.
  Red on the current tree, where the op falls to the `unknown` arm. **Grep for `FixKindToken` yourself to
  find that arm; do not trust any line number quoted at you, including one in the design document.**
- `AnUnparseableVerdict_IsRecordedAsNoVerdict_NotSilence` — a runner that returns a body which is not a
  parseable verdict must produce a recorded `no-verdict` decision and an observer line, exactly as the
  §9.2 diagnose already does (#452). The diagnose spend is charged BEFORE the parse, so a billed
  supervisor failure is never silent. This is the row that stops the new consult becoming a second silent
  capability — which is what #712 is.
- `ProposeResourceSupply_DrivesTheRealClaudePromptRunner_WritingItsStreamLogAndReturningARealVerdict` —
  **the drive-the-real-seam proof.** See below; it is the row task 09's `03-real-seam-tests-pass.ps1` gates.

**One row is DECLARED EXEMPT from the red census:**

- `TheGenericDiagnoseBriefIsUnchanged` — drive `EvaluateAsync` with `OverwatchTrigger.EagerAttempt` through
  a capturing runner and assert the generic brief still begins `# Overwatch diagnose:`, still offers the
  four shipped op shapes, and contains no `resource-supply`. **It is green on arrival** — the generic brief
  is shipped code this task does not touch — so demanding red would demand a correct test fail. It must
  still EXIST: the census asserts that, and task 09's forward census requires it observed `Passed`. Write it
  correctly; do NOT make it fail to please the census.

### The drive-the-real-seam proof (#382, bucket E, seam `Overwatch -> IPromptRunner`)

Every other row above proves the brief's TEXT against a fake `IPromptRunner`. That is exactly the
substitution that blinds the guardrail: a component whose tests inject a fake `IPromptRunner` goes green
while it throws through the real `ClaudePromptRunner` — the measured `CriticalityJudge` empty-`StreamLogPath`
bug, where a blanket catch turned the crash into a safe default and the judge escalated 100% of the time
with nothing saying so.

So this row constructs the **REAL** `ClaudePromptRunner` over a **fake CLI PROCESS**:

```csharp
IPromptRunner realRunner = new ClaudePromptRunner("overwatch", fakeCliPath, new ProcessRunner());
var overwatch = new Overwatch(realRunner, terminalTriage: null, AutonomyPolicy.Auto, autonomyBlockPresent: true);
```

- **Never a fake `IPromptRunner` in this row.** That is the substitution the proof exists to defeat.
- Write the fake CLI with the proven pattern in
  `tests/Guardrails.Core.Tests/ClaudePromptRunnerStreamLogTests.cs` (`WriteFakeCli`): on Windows a `.cmd`
  shim invoking a `.ps1`, elsewhere a `.sh` with the executable bit set. It must (a) drain stdin to a log
  file the test reads, and (b) emit ONE stream-json terminal line —
  `{"type":"result","is_error":false,"result":"<the JSON verdict>","total_cost_usd":<n>,"num_turns":1}` —
  whose `result` is a `resource-supply` verdict for one of the candidates.
- Build the plan/task/journal fixture the way `OverwatchNoVerdictTests` does (a temp plan dir, a
  `PlanDefinition` whose `Workspace` and `PlanDirectory` are that dir, `RunJournal.LoadOrCreate`, and a
  `taskLogDir` under `logs/<runId>/<taskId>`).

**Assert effects only the REAL runner emits:**

1. the stream log `overwatch-stream-attempt-<n>.jsonl` EXISTS under `taskLogDir` and contains the line the
   fake CLI emitted — a file no fake `IPromptRunner` would ever write;
2. the returned proposal is a real parsed verdict — non-null, `Retryable`, carrying a `ResourceSupply` fix
   for the candidate path — i.e. NOT the catch-and-safe-default no-verdict the blanket `catch` produces;
3. the fake CLI's recorded stdin begins with the pinned `# Overwatch resource supply:` heading, so the
   composed brief provably crossed the process boundary;
4. the diagnose spend reached the journal's cumulative cost (the real runner parses `total_cost_usd`).

**These do NOT qualify and must not be what the row rests on:** `Assert.NotNull` on the runner, a recording
double's `Assert.Single(recorder.Calls)`, `Assert.True(wasCalled)`, or any form of *"the seam was called"*.
That assertion is how both motivating bugs shipped green.

The pinned tests MUST COMPILE and FAIL. Do NOT implement `ProposeResourceSupplyAsync` or the `Token` case.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/OverwatchResourceSupplyBriefTests.cs`, `src/Guardrails.Core/Execution/OverwatchTrigger.cs`, and `src/Guardrails.Core/Execution/Overwatch.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
