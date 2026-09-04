## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `12-author-tests-terminal-row-delivery`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "12-author-tests-terminal-row-delivery": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "12-author-tests-terminal-row-delivery": { "someKey": "someValue" },
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

Author the test proving that a row appended immediately before shutdown still reaches a live
`GET /events` subscriber.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RunEvents/EventsStreamShutdownTests.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Class name is pinned: **`EventsStreamShutdownTests`**, `[Trait("Category", "RunEvents")]`. Read
`tests/Guardrails.Integration.Tests/RunEvents/EventsEndpointTests.cs` first for the established
server-lifecycle idiom.

### The defect

`WriteEventsStream`'s tail loop returns on the shutdown signal **without a final read**. `run-finished`
is appended microseconds before the log server is disposed, so the streaming loop is almost certainly
parked in its ~150 ms poll wait and returns having never read the terminal row. The row lands in the
file and never reaches the wire - which is the entire payoff of `run-finished`.

### The behaviours - these exact method names

**1. `ASubscriberReceivesARowAppendedJustBeforeShutdown`** - MUST BE RED.
Attach a live subscriber, append a row, then immediately shut the server down, and assert the
subscriber received that row. Make the timing deterministic rather than racing a real run.

**2. `AMissingEventsFileStillCompletesWithAnEmptyBody`** - will be GREEN, and must STAY green.
A run with no `events.jsonl` yet completes with an empty 200. This is deliberate, documented in
`LogServer`, and already pinned by `EventsEndpoint_OnAMissingEventsFile_ReturnsAnEmptyStreamNotAnError`.
**Do not change that behaviour and do not write a test that would require changing it** - a fix that
held the connection open would make the existing test **hang** rather than fail, which is the worst
kind of change to hand an implementer. This test is a declared exemption from the red census: it
asserts existing correct behaviour.

### Done when

The project compiles, test 1 **fails**, and test 2 executes and passes. Do NOT touch `LogServer.cs` -
that is task 13.
