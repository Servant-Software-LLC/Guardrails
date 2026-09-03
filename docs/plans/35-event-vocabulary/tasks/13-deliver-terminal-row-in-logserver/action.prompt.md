## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `13-deliver-terminal-row-in-logserver`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "13-deliver-terminal-row-in-logserver": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "13-deliver-terminal-row-in-logserver": { "someKey": "someValue" },
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

Make `GET /events` deliver a row appended immediately before shutdown.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/Ui/LogServer.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT edit the tests authored upstream.** They are the specification. If one is genuinely wrong,
write `{"needsHuman": "<why>"}` to the state-out path and stop rather than changing it - an
out-of-scope edit to a test file fails the task immediately and consumes a retry.

### The change - three lines

In `WriteEventsStream`'s tail loop, the shutdown signal currently returns **without a final read**. Do
one final read-and-flush before returning, so a row appended in the last poll interval still reaches
the subscriber.

### Do NOT change the empty-200 for a missing `events.jsonl`

It is deliberate, documented in this file, and pinned by
`EventsEndpoint_OnAMissingEventsFile_ReturnsAnEmptyStreamNotAnError`. Holding the connection open
instead would make that test **hang** rather than fail. The window it appears to cover does not exist:
the log server does not start until after every pre-DAG phase has run.

### The honest limitation - leave it in place

`_listener.Stop()` runs first in `DisposeAsync` and can abort an in-flight response, so even with this
fix delivery is **best-effort**. `run-finished` is a durable FILE event first; a subscriber whose
connection closes re-reads the file. Guaranteeing delivery would mean the run waiting on its own HTTP
clients, which is a worse trade. Do not attempt to close that gap here.

### Done when

`ASubscriberReceivesARowAppendedJustBeforeShutdown` passes, the existing `EventsEndpointTests` still
pass, and nothing else regresses.
