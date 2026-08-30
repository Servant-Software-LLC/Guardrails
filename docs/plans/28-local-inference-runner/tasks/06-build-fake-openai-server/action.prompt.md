## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "06-build-fake-openai-server": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements part of `docs/plans/28-local-inference-runner.md`. READ THE SECTION(S) NAMED BELOW before you start -
the plan carries the reasoning, the rejected alternatives, and the exact file:line evidence.
Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

Read: **plan section 8**.

## Task

### What to build

`tests/Guardrails.Integration.Tests/OpenAiCompat/FakeOpenAiServer.cs` - a loopback,
OpenAI-compatible HTTP server driven by a **scripted response plan**, because **its job is to
misbehave**. Authored BEFORE the runner, so the runner is written against a server that already
misbehaves.

Follow the repo's own precedent: `tests/Guardrails.Integration.Tests/LogServerTests.cs` runs a real
`HttpListener` on loopback. Read it first and match its lifecycle and teardown style. This is a real
socket, not a fake `HttpMessageHandler` - the seam under test is the OpenAI HTTP wire.

It must be scriptable to produce, at minimum, every row of the plan's section 8 table:

- a normal streamed completion with `usage`
- a response that silently TRUNCATES the prompt and answers confidently anyway
- a response OMITTING `usage` despite `stream_options.include_usage`
- a 404 `model not found`
- a 429
- `finish_reason: "length"`
- a final message with a ```json block that is NOT the last block, then prose
- prose with no JSON at all
- prose AROUND a valid JSON object
- a tool call requesting a file OUTSIDE the permitted roots
- three tool calls in a row (to drive the denial bound)
- **accepts a `tools` array and calls NOTHING, returning a well-formed `{"pass": true}`** (the
  section 6.6 false-green case)
- **rejects `tools` with a 400**
- `GET /models` returning a list, and separately returning **404** (the section 7 downgrade case)

Expose a way for a test to assert **how many connections were accepted** - the section 7 zero-cost
condition is proven by a listener that fails on ANY accepted connection, never by a counter the
production code increments.

It must be `IDisposable`/`IAsyncDisposable`, bind to a free loopback port, and tear down reliably so
a failing test cannot leak a listener into the next one.

Also write **`FakeOpenAiServerTests.cs`**, class **`FakeOpenAiServerTests`** (pinned - the guardrail
filters on it): a self-test proving the fixture is drivable end to end. **Pin these exact method
names** - the guardrail requires each to have executed, because three later task pairs build every
assertion on this fixture and a hollow self-test would let a broken one through:

- `NormalCompletion_IsReceivedOverTheLoopbackSocket`
- `ScriptedNotFound_ArrivesAs404`
- `ScriptedToolsRejection_ArrivesAs400`
- `AcceptedConnectionCount_ReportsWhatActuallyHappened`
- `ModelsEndpoint_CanBeScriptedToReturn404`

Each must drive a real `HttpClient` against the real socket and assert on the response - not on a
field the fixture set for itself.

This is test infrastructure with no behaviour of its own to TDD, so it is not split into a pair.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/OpenAiCompat/FakeOpenAiServer.cs`, `tests/Guardrails.Integration.Tests/OpenAiCompat/FakeOpenAiServerTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
