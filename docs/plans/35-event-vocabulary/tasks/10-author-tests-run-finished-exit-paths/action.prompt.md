## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `10-author-tests-run-finished-exit-paths`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "10-author-tests-run-finished-exit-paths": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "10-author-tests-run-finished-exit-paths": { "someKey": "someValue" },
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

Author the matrix that proves `run-finished` fires on **every** way a run can end - and reaches
`events.jsonl` through the real composed chain, not a writer in isolation.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RunEvents/RunFinishedExitPathTests.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Class name is pinned: **`RunFinishedExitPathTests`**, `[Trait("Category", "RunEvents")]`. Read
`tests/Guardrails.Integration.Tests/RunEvents/RunCommandObserverWiringTests.cs` first - it already
drives `RunCommand.BuildObserverChain` and is the idiom to follow for the wiring test.

### Why a matrix and not a single case

A `run-finished` that fires only on the green path is worse than none: an unattended supervisor would
treat its absence as "still running" on exactly the runs that ended badly. Each row is a different
`return` or unwind, and they do not share a code path.

### The behaviours - these exact method names

**1. `RunFinished_FiresOnAGreenRun`** - `exitCode` is 0.

**2. `RunFinished_FiresOnANeedsHumanHalt`** - the row exists and carries the needs-human exit code.

**3. `RunFinished_FiresOnATerminalGateFailure`**
The terminal-gate-failure branch returns an exit code that **differs from what `Finish` returns**, so
the row must carry the code the run actually exited with. A test that reads `Finish`'s value cannot
tell the two apart.

**4. `RunFinished_FiresWhenExecuteAsyncThrows_WithNullExitCodeAndTheTypeName`**
The most important row. Force an unhandled throw out of `ExecuteAsync` and assert three things: the
row exists; `exitCode` is **absent** (the run never reached a verdict, and null is honest where a
fabricated code would claim one); `faultKind` is the exception's **type name**.

**5. `RunFinished_OnAFault_CarriesNoExceptionMessage`**
The negative, and a **security** property rather than tidiness. Construct a fault whose *message*
contains a recognisable secret-shaped string, and assert that string appears **nowhere** in
`events.jsonl`. #585 layer 3 will POST these rows to an operator-supplied URL, and the message is the
one value on the row that can carry an absolute path, a token, or a fragment of source.

**6. `TheThrownExceptionStillPropagates_Unchanged`**
The `catch` that records `faultKind` must **rethrow bare**. A catch that swallows or wraps the exception
would convert a crash into a silent wrong answer - a far worse bug than the one being fixed.

**Assert the STACK TRACE too, or this test does not test what it is named for.** `throw ex;` propagates
the same instance, the same type and the same message as `throw;` - it differs only in RESETTING
`StackTrace` to this frame. So assertions on type and message pass against the one implementation the
plan explicitly forbids, and the diagnostics for every unhandled fault in the process are destroyed
silently. Assert the propagated exception's `StackTrace` still names the original throwing frame.

**7. `BuildObserverChain_WiresTheEventStream_SoRunFinishedReachesEventsJsonl`**
The composition-root proof. Drive the **real** `RunCommand.BuildObserverChain`, raise `RunFinished` on
the returned chain, and assert the row lands in `events.jsonl`. **Do NOT construct `RunEventStream`
yourself in this test** - a unit test against the writer in isolation passes while the composed chain
swallows the event, which is precisely the defect this asserts against.

### Done when

The project compiles and all seven tests **fail**. Failing is intentional; not compiling is a mistake
to fix. Do NOT touch `RunCommand.cs` - that is task 11.
