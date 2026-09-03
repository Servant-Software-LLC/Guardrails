## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `05-author-tests-events-projection`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "05-author-tests-events-projection": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

The first of the plan's TWO projections off the one emission seam: the **semantic, low-frequency,
agent-facing** stream. A supervising agent filters on FIELDS, so an unrecognised event is still a visible
row rather than an invisible one - which is the property that would have prevented all three of the
stdout-grep failures in issue #585.

Two artifacts, both inside your writeScope:

1. **The stub** - create `src/Guardrails.Core/Execution/RunEventStream.cs`: a public sealed
   `RunEventStream : IRunObserver` DECORATOR (constructor takes the inner `IRunObserver` plus the
   directory it writes into), whose members all throw `NotImplementedException` for now. It appends one
   JSON object per line to `events.jsonl` in that directory.

2. **The tests** - create `tests/Guardrails.Core.Tests/RunEvents/RunEventStreamTests.cs`, class
   **`RunEventStreamTests`**, every test carrying `[Trait("Category", "RunEvents")]`. Write into a temp
   directory and delete it in a `finally`; never write into the repo.

   Pin these test METHOD names:

   - `AttemptFinished_AppendsOneJsonLine_CarryingTaskIdAttemptAndOutcome`
   - `AttemptFinished_OutcomeTokenMatchesTheTelemetryCorpusVocabulary` - the `outcome` value is the
     `AttemptOutcome` token the journal and the telemetry corpus already use (`max-turns`,
     `guardrail-failed`, ...), NOT a second vocabulary invented here. Assert on the exact token text.
   - `EveryLine_IsIndependentlyParseableJson` - append several events, then parse each line on its own;
     a consumer attaching late must be able to read any single row
   - `UnrecognisedConsumer_StillSeesTheRow` - a row whose `kind` a consumer does not know is still a
     well-formed, parseable line carrying `runId`, `taskId` and `attempt`
   - `Decorator_ForwardsEveryObservedCallToTheInner` - it is a decorator, and a decorator that swallows
     a member is the exact trap `IRunObserver` documents four times

   These MUST COMPILE and FAIL against the throwing stub. Do NOT implement the behaviour; that is task 06.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/RunEvents/RunEventStreamTests.cs` and
`src/Guardrails.Core/Execution/RunEventStream.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
