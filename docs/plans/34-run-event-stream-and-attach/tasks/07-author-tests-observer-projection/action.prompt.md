## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `07-author-tests-observer-projection`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "07-author-tests-observer-projection": { "someKey": "someValue" } }`.
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

The SECOND projection off the same seam - the one `guardrails attach` replays. It is deliberately NOT the
same file as `events.jsonl`: the agent-facing stream is semantic and low-frequency, while a renderer needs
the live-only fields that make the table worth watching (elapsed time per task, which guardrail is
executing right now, the cost ticking up). A single stream would serve one of those two badly.

Two artifacts, both inside your writeScope:

1. **The stub** - create `src/Guardrails.Core/Execution/ObserverProjection.cs`: a public sealed
   `ObserverProjection : IRunObserver` DECORATOR (constructor takes the inner `IRunObserver` and the
   directory it writes into), members throwing `NotImplementedException`. It appends one JSON line per
   observed `IRunObserver` call to `observer.jsonl` in that directory.

2. **The tests** - create `tests/Guardrails.Core.Tests/RunEvents/ObserverProjectionTests.cs`, class
   **`ObserverProjectionTests`**, every test carrying `[Trait("Category", "RunEvents")]`. Temp directory,
   cleaned up in a `finally`.

   Pin these test METHOD names:

   - `EveryObservedCall_AppendsOneLine_NamingTheMember`
   - `Replay_ReproducesTheObservedCallSequence_InOrder` - the property attach depends on: reading the
     file back yields the same calls, in the same order, with the same arguments
   - `AttemptFinished_IsProjected_WithItsOutcome`
   - `Decorator_ForwardsEveryObservedCallToTheInner`
   - `TwoConcurrentReaders_BothReadEveryLine` - the issue's acceptance requires attaching twice
     concurrently without either watcher perturbing the run

   These MUST COMPILE and FAIL against the throwing stub. Do NOT implement; that is task 08.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/RunEvents/ObserverProjectionTests.cs` and
`src/Guardrails.Core/Execution/ObserverProjection.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
