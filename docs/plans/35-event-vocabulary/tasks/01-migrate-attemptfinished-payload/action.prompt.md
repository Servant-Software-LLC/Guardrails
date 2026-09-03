## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `01-migrate-attemptfinished-payload`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-migrate-attemptfinished-payload": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "01-migrate-attemptfinished-payload": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

## Task

Make ONE atomic change to `IRunObserver` and migrate every site so the solution compiles again.

**This task is deliberately large and MUST NOT be split.** Changing the signature breaks every
declaration and every raise site simultaneously — that is the point: the compiler enumerates the sites
for you, which is safer than an additive overload that lets one site keep the old shape silently. The
build is RED from your first edit until your last. Do not stop early; do not add a temporary overload.

### 1. `src/Guardrails.Core/Execution/IRunObserver.cs`

ADD one member, with a default empty body like every other optional member on this interface:

```csharp
void RunFinished(int? exitCode, string? faultKind) { }
```

CHANGE one member:

```csharp
// was: void AttemptFinished(TaskNode task, int attempt, Journal.AttemptOutcome outcome) { }
void AttemptFinished(TaskNode task, Journal.AttemptRecord record) { }
```

Document on `RunFinished`, in its XML doc:
- `exitCode` is the `Guardrails.Cli.ExitCodes` vocabulary: 0 green, 1 harness error, 2
  needs-human/gate failure, 3 cancelled, 4 escalations pending, 5 proceeded unreviewed. It is
  **null** when the run is unwinding on an unhandled fault and no exit code was ever determined — null
  is honest; a fabricated code would claim a verdict the run never reached.
- `faultKind` is the unhandled exception's **TYPE NAME**, null on every non-fault path, and **NEVER the
  exception message**. #585 layer 3 will POST these rows to an operator-supplied URL, and the message is
  the one value on the row that can carry an absolute path, a token, or a fragment of source. Same
  posture as `WaveBreakdownFinished`'s `failureKind`.
- It carries no `runId`: the composition root already holds it.

**Do NOT add a `RunStarting` / `run-started` member.** It was designed and rejected — see
`docs/plans/595-event-vocabulary-contract.md` section 1a. Do not add it, and do not add a field to
another member to compensate for its absence.

### 2. Migrate every RAISE site

**Count them yourself before you edit anything. Grep for `.AttemptFinished(` in
`src/Guardrails.Core/Execution/AttemptJournaler.cs` and `src/Guardrails.Core/Execution/TaskExecutor.cs`.**
At authoring time that returned **9 hits in AttemptJournaler and 2 in TaskExecutor**. Every one already
has the record in a local (named `record`, or `failedRecord` in one TaskExecutor site) and already reads
`.Outcome` off it, so each edit is mechanical:

```csharp
_observer.AttemptFinished(task, attemptNumber, record.Outcome);   // before
_observer.AttemptFinished(task, record);                          // after
```

**If your grep returns a different number, trust the grep**, migrate what it found, and say so in your
summary. Migrate from your own grep, not from this paragraph.

### 3. Migrate every DECLARATION — signature only, behaviour UNCHANGED

**Grep for `void AttemptFinished` across `src/` and `tests/` and cover every hit.** At authoring time
that was 12 files: the interface, 6 implementations, and 5 test doubles.

For `RunEventStream` and `ObserverProjection` this is a **signature-only** change: they must write
**exactly the same row bytes they write today**, now reading `record.Outcome` instead of the `outcome`
parameter and `record.Attempt` instead of the `attempt` parameter. Widening those rows is tasks 05 and
07 — not this task. The existing RunEvents tests assert the current bytes and must keep passing; that is
this task's behaviour-preservation proof.

For `LiveRunObserver`, `ConsoleRunObserver`, `OnTheFlyDiagramObserver`, `OnTheFlyLogSiteObserver`:
signature update only, same behaviour, reading what they need off the record.

**Do NOT declare `RunFinished` in ANY implementation in this task.** Leaving it undeclared everywhere is
what makes the next task's forwarding tests fail for the right reason.

### 4. `src/Guardrails.Cli/Commands/AttachCommand.cs`

Its replay dispatcher constructs the `AttemptFinished` call. Make it compile against the new signature by
rebuilding a minimal `AttemptRecord` from the fields the observer line already carries. Full round-trip
fidelity is task 07 — here you need only the solution to build and the existing attach tests to pass.

### 5. Test doubles

The 5 test files in your `writeScope` implement `IRunObserver`. Update their `AttemptFinished` signature.
Where a double records the outcome, read it off `record.Outcome` so its assertions still hold. **Do not
change what any existing test asserts** — if an existing assertion looks genuinely wrong, write
`{"needsHuman": …}` rather than weakening it.

### Done when

`dotnet build Guardrails.sln` succeeds and the existing `Category=RunEvents` tests still pass in both
test projects. No behaviour changed anywhere: this is a payload migration.
