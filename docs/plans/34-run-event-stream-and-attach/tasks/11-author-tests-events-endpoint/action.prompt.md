## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `11-author-tests-events-endpoint`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "11-author-tests-events-endpoint": { "someKey": "someValue" } }`.
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

Create `tests/Guardrails.Integration.Tests/RunEvents/EventsEndpointTests.cs`, class
**`EventsEndpointTests`**, every test carrying `[Trait("Category", "RunEvents")]`.

The reviewed plan settled that v1 includes this endpoint, not just the durable file: an agent-side monitor
takes a stream source natively, so a subscribable endpoint removes grep from the supervision path
entirely rather than mitigating it. The log server already runs headless since #552, so this is an
addition to something that exists.

The server is `src/Guardrails.Cli/Ui/LogServer.cs` - a raw `HttpListener` whose `Handle` method routes on
`context.Request.Url.AbsolutePath` segments (find it by grepping for `AbsolutePath`; do not cite a line
number, it moves). `/diagram.html` is the existing example of a top-level non-`tasks` route.

Pin these test METHOD names, driving the REAL `LogServer` over loopback:

- `EventsEndpoint_StreamsExistingEventsToALateSubscriber` - a consumer attaching mid-run receives the
  events already written, not only future ones
- `EventsEndpoint_StreamsSubsequentEventsAsTheyAreAppended`
- `EventsEndpoint_EmitsOneParseableEventPerMessage`
- `EventsEndpoint_OnAMissingEventsFile_ReturnsAnEmptyStreamNotAnError` - a run that has emitted nothing
  yet is a healthy run, and must not look like a broken endpoint
- `LogServer_StillServesItsExistingRoutes` - the existing `/tasks/...` and `/diagram.html` routes are
  unaffected

These MUST COMPILE and FAIL - the route does not exist. Do NOT edit `LogServer.cs`; that is task 12.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RunEvents/EventsEndpointTests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
