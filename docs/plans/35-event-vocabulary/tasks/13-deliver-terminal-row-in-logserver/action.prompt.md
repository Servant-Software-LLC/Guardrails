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

### The change - TWO edits, and the second one is the load-bearing half

**Read this section carefully. An earlier version of this task described the fix as "three lines" in
`WriteEventsStream` and FORBADE touching `DisposeAsync`. That was wrong, and it cost three attempts to
establish. The correction is on the record because you should not have to re-derive it.**

**Edit 1 - the final read (necessary, not sufficient).** In `WriteEventsStream`'s tail loop, the
shutdown signal currently returns **without a final read**. Do one final read-and-flush before
returning, so a row appended in the last poll interval is at least attempted.

**Edit 2 - move the drain that already exists.** `LogServer.DisposeAsync` runs, in this order:

```csharp
_shutdown.Cancel();                       // wakes the polling /events loop
try { _listener.Stop(); }                 // DISPOSES the shared HTTP.sys request-queue handle
try { _listener.Close(); }
await _acceptLoop;
await Task.WhenAll(pending);              // "Wait for every dispatched request ... to notice
                                          //  _shutdown and return BEFORE disposing it out from
                                          //  under them."
```

That last wait's own comment says it runs *before disposing it out from under them* — and `Stop()`
disposed the handle three lines earlier. **The comment is already false today, independent of this
plan.** The consequence, established empirically over ~10 variants in a prior attempt: the final write
throws `System.ObjectDisposedException: ... 'System.Net.HttpRequestQueueV2Handle'`, and
`_listener.Close()`'s underlying `HttpCloseRequestQueueHandle` cancels outstanding I/O for the WHOLE
queue at the kernel level — so **no amount of in-process synchronization confined to `WriteEventsStream`
can work around it.** A prior attempt proved even a `Close(byte[], willBlock: true)` that completed
with zero exceptions before `Stop()` ran still lost the row.

**So: hoist the existing `await Task.WhenAll(pending)` to immediately after `_shutdown.Cancel()`, before
`_listener.Stop()`.** Do not invent a new mechanism; move the one that is already there, and make its
comment true.

**Bound the wait.** Give it a timeout (a few seconds is generous) so a pathological handler can never
hang shutdown, and proceed to `Stop()` when it expires. This is what makes the move safe: the handlers
have already been signalled by `_shutdown`, and this stream's own poll interval is 150 ms, so the
normal path costs milliseconds. **The plan's old worry that this means "the run waiting on its own HTTP
clients" was a misreading** — you are waiting for the server's own already-cancelled handlers to
return, not for a client to read.

**While you are in there: that `ObjectDisposedException` was being swallowed** by the tail loop's
catch-all, which is why this defect reads as "the row was silently dropped" rather than "shutdown threw".
Do not widen the catch. If a final write genuinely fails, it is fine for the row to be missed — it is
durable in the file — but the failure should not be indistinguishable from success.

### Do NOT change the empty-200 for a missing `events.jsonl`

It is deliberate, documented in this file, and pinned by
`EventsEndpoint_OnAMissingEventsFile_ReturnsAnEmptyStreamNotAnError`. Holding the connection open
instead would make that test **hang** rather than fail. The window it appears to cover does not exist:
the log server does not start until after every pre-DAG phase has run.

### The honest limitation that REMAINS after both edits

Delivery is still **best-effort in principle**: a subscriber whose connection drops for its own reasons
misses the row, and the bounded wait above can expire under a pathological handler. `run-finished` is a
durable **FILE** event first, and a consumer whose connection closes re-reads the file. Say that in the
SSOT; do not claim a guarantee the transport cannot make.

What is NO LONGER true is the older, stronger claim that the gap could not be closed at all. It can, for
the ordinary shutdown path, by moving a wait that already exists.

### Done when

`ASubscriberReceivesARowAppendedJustBeforeShutdown` passes, the existing `EventsEndpointTests` still
pass (including the empty-200 test, which must not hang), and nothing else regresses.

**Do not weaken, skip, or `[Fact(Skip=...)]` the target test to close this task.** A prior attempt was
right to refuse that and halt instead; the plan was wrong, not the test.
