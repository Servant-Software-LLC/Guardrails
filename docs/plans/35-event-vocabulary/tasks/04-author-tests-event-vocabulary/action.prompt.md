## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `04-author-tests-event-vocabulary`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "04-author-tests-event-vocabulary": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "04-author-tests-event-vocabulary": { "someKey": "someValue" },
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

Author the tests that specify what `events.jsonl` carries after this plan.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/RunEvents/RunEventVocabularyTests.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Class name is pinned: **`RunEventVocabularyTests`**, every test carrying `[Trait("Category", "RunEvents")]`.
This task's guardrails filter on that class name, so it must match exactly. Read
`tests/Guardrails.Core.Tests/RunEvents/RunEventStreamTests.cs` first and follow its fixtures and idiom
(`NewTempDirectory`, `ReadEventLines`, `IRunObserver.Null`); do not duplicate what it already covers.

### The behaviours - these exact method names

**1. `RunFinished_AppendsARunFinishedRow_CarryingExitCode`**
Raise `RunFinished(0, null)`; assert one row with `kind` = `run-finished` and `exitCode` = 0.

**2. `RunFinishedRow_HasNoTaskId_BecauseItIsRunScoped`**
`run-finished` is the only kind with no `taskId`. Assert the property is **absent**, not null.

**3. `RunFinishedRow_CarriesFaultKindButNeverAMessage`**
Raise `RunFinished(null, "InvalidOperationException")`; assert `faultKind` is that type name and
`exitCode` is **absent** - a null exit code means the run never reached a verdict, and null is honest
where a fabricated code would claim one. Then assert the row does **not** contain a message-shaped
payload: build the fault kind from an exception whose *message* contains a recognisable secret-shaped
string, and assert that string appears nowhere in the row. This is a security property, not tidiness -
#585 layer 3 will POST these rows to an operator-supplied URL, and the message is the one value that
can carry an absolute path, a token, or a fragment of source.

**4. `EveryRow_CarriesAStrictlyIncreasingSeq`**
Every kind carries `seq`: monotonic, 1-based, per-process. Raise several events; assert `seq` is
1, 2, 3 ... in file order.

**5. `Seq_IsUniqueAndOrdered_UnderConcurrentWriters`**
Drive appends from several threads at once; assert every `seq` is unique and file order agrees with
`seq` order. `seq` and the `at` stamp are both assigned **inside** the append lock. Today `At` is built
outside it, so under parallel workers `at` order can disagree with file order, and on Windows its
~15.6 ms tick resolution makes concurrent rows share an `at` outright. **`seq`, not `at`, is the
ordering key** - #585 layer 3 will key retry and ordering on it.

**6. `AttemptFinishedRow_CarriesTheFieldsThatDecideAResponse`**
Build a fully-populated `Journal.AttemptRecord` (including `Provenance`) and assert the row carries
each field, named for its `TelemetryRow` twin verbatim: `costUsd`, `turns`, `model`, `tier`, `runner`,
`startedAt`, `endedAt`, `needsHumanKind`.

**7. `AttemptFinishedRow_OmitsFieldsTheRecordDoesNotHold`**
With a record whose `Provenance` is null, assert `model` / `tier` / `runner` are **absent**, not null.
The stream reports exactly what the journal holds - four of `FailedAttempt`'s call sites pass no
provenance, and papering over that in the projection would make it a second owner of the fact.

**8. `RunIdComesFromTheConstructor_NotTheDirectoryName`**
Construct `RunEventStream` with a `runId` that deliberately **differs** from the directory's name and
assert rows carry the constructor value. Today it is derived by `Path.GetFileName(directory)`; a test
whose runId happens to equal the directory name cannot tell the two apart.

### What NOT to assert

- **No `elapsedSeconds`.** It has no `TelemetryRow` counterpart; `endedAt` minus `startedAt` is the
  same fact in the corpus's own terms, and shipping it would be the forked vocabulary #585 forbids.
- **No `attemptsMax`.** It is already on the `attempt-started` row as `budget`; a consumer correlates
  on `(taskId, attempt)`.
- **No `run-started`.** It was designed and rejected - see `docs/plans/595-event-vocabulary-contract.md`
  section 1a. Do not test for it.

### Done when

`Guardrails.Core.Tests` **compiles** and all eight tests **fail** against the current writer. Failing is
intentional; not compiling is a mistake to fix. Do NOT implement the writer - that is task 05.
