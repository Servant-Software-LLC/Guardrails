## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `07-implement-webhook-dispatcher`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "07-implement-webhook-dispatcher": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "07-implement-webhook-dispatcher": { "someKey": "someValue" },
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

Fill real logic over the dispatcher stubs task 06 left in
`src/Guardrails.Core/Execution/WebhookEventSink.cs`. `WebhookEventSinkTests` is the specification —
read the whole class first, and read `docs/plans/36-onevent-webhooks.md` §3.2, §3.3, §5.2, §5.3 and
§5.4 before writing anything, because the ORDER of the teardown steps is the entire point of this
task.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/WebhookEventSink.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT edit the tests authored upstream.** They are the specification. If one is genuinely wrong,
write `{"needsHuman": "<why>"}` to the state-out path and stop rather than changing it — an
out-of-scope edit to a test file fails the task immediately and consumes a retry.

Leave `IsRetryable`, `RedactUrl` and the §5.2 bounds constants exactly as tasks 04 and 05 shipped
them. The constants keep their §5.2 values; the `timeScale` seam multiplies at the **use site**, never
by rewriting a constant — two tests assert those values directly.

### 1. The queue and the emit path (§3.2)

- A **bounded `Channel<EventDelivery>`**, capacity **1024**, **`FullMode = DropOldest`**, created with
  the `Channel.CreateBounded<T>(BoundedChannelOptions, Action<T> itemDropped)` overload **so every
  displaced row is counted rather than vanishing**. `Emit` calls `TryWrite`, which never blocks.
  `DropOldest` rather than `DropWrite` or `Wait`, and the reason is `run-finished`: with any
  newest-loses policy the queue is full exactly when the terminal row arrives, so the single row a CI
  wrapper exists to receive is the one guaranteed to be dropped. Dropping from the head leaves the
  tail in order, and a gap is already the documented meaning of a drop.
- **`Emit` also stores the delivery in `_lastEnqueued`.** Record the last row `Emit` **saw** — whether
  or not the circuit or a full queue kept that row out of the pump's path. This is what §3.3's
  guaranteed terminal attempt reaches for, and it is why the dispatcher needs **no** knowledge of the
  event vocabulary to protect the terminal row: `run-finished` is by construction the last row a
  process emits, so "the last enqueued row" *is* it on every normal path, with no `kind` comparison
  anywhere. Skipping the `_lastEnqueued` write when the circuit is open would evaporate the guarantee
  in exactly the scenario it exists for.
- **The `itemDropped` callback runs on the RUN's thread, inside `RunEventStream`'s append lock**, so
  it may do exactly two things: `Interlocked.Increment` a counter, and record the delivery id in a
  field. **No file IO and no console write ever happens on the run's thread** — that is the one way
  this design could measurably slow a run.
- **The whole of `Emit` sits inside a `catch (Exception)`** that increments the drop counter. It is
  invoked from inside `RunEventStream`'s append lock on a Scheduler worker thread; a throw there would
  propagate while holding `_gate`. Belt as well as the braces `AppendLine` already puts around the
  callback.
- **One** background pump task started in the constructor, reading `ReadAllAsync`. **One pump, not a
  fan-out:** serial delivery is what keeps arrival in `seq` order, and a retrying row delays later
  rows rather than being overtaken by them. **Its `Task` is retained** — an unobserved pump that
  faults is the silent disappearance this feature exists to remove.
- A single `HttpClient` built on `new SocketsHttpHandler { AllowAutoRedirect = false }` (§6.5 — a
  redirect can move the POST, with its `Authorization` header and its payload, to a host the operator
  never named; .NET does strip `Authorization` cross-host in current versions, but relying on
  framework behaviour the code does not state is the silent-dependency pattern this repo keeps
  getting burned by). `Timeout` is set **per request via a `CancellationTokenSource`**, not on the
  client. Only `TryStart` builds this; the internal test constructor takes the handler it is given.

### 2. Retry and the circuit (§5.2, §5.3)

Per row: up to 4 attempts, backoff 1 s / 2 s / 4 s each multiplied by jitter in `[0.5, 1.5)`, a 10 s
per-attempt timeout, and a **hard 45 s per-row ceiling enforced by a per-row `CancellationTokenSource`**
so the schedule can never exceed it however the attempt timings fall. Classification is `IsRetryable`
— call it, do not re-derive it. Any 2xx is success; the response body is read to at most 8 KB and
**discarded**, which releases the connection without buffering a hostile response.

After **5 consecutive rows** exhaust their attempts the endpoint is marked failing for the rest of the
run: later rows are dropped on arrival, counted, no HTTP attempted. A delivered row resets the
counter — that is what "consecutive" means. **It never closes:** no half-open probe, no timer.

**The circuit's notice is BUFFERED, not printed when the circuit opens.** This is not a style
preference: the pump is a background thread, and `RunCommand` holds a Spectre `Live` region open
across the entire DAG — the constraint is stated in the code three lines above the region's
construction, *"any console write into an active Live region corrupts the table (#145 Bug 1)"*. So
buffer the line and flush it with the end-of-run summary, where the region is long gone:

```
Webhook: gave up at 14:22:07 after 5 consecutive delivery failures (last: HttpRequestException).
```

### 3. The summary — and it prints EVEN AT ZERO DROPS (§5.4)

One line at the end of every run that used `--on-event`, emitted through `onNotice` from
`DisposeAsync` so it fires on the normal path, the halt path and the unwinding-fault path alike:

```
Webhook: 211 delivered, 0 dropped -> https://hooks.example.com/…
Webhook: 197 delivered, 14 dropped -> https://hooks.example.com/…
Webhook: gave up at 14:22:07 after 5 consecutive delivery failures (last: HttpRequestException).
Webhook: delivery stopped early (TaskCanceledException); 6 row(s) never attempted.
```

**The zero-drop line is not noise: silence on success is the exact defect this issue is about, and a
line that always prints is proof the mechanism ran at all.** The fourth form matters for the same
reason — `Task.WhenAny(pump, delay)` does not throw on a faulted pump, so a summary reading "0
dropped" while rows sit in a dead channel would be a silent disappearance.

**Every notice and the summary print the exception's TYPE NAME and the HTTP status code only — never
`ex.Message`, and never the full URL.** An `HttpRequestException` message routinely contains the whole
request URI, and for Slack and webhook.site **the URL path IS the credential**. Render the URL with
`RedactUrl`. The auth value is never logged, echoed, journaled or written to any file.

### 4. `DisposeAsync` — six steps, in this order, and every one is load-bearing (§3.3)

| # | Step | Why it is where it is |
|---|---|---|
| 1 | Complete the channel writer and set `_draining` | Signals wind-down. From here the pump makes **one attempt per row** — it abandons the retry budget entirely. |
| 2 | **Backlog phase**: keep delivering in `seq` order, one attempt each, until the queue empties or the backlog drain budget expires (**10 s**, and **0 s when the run was cancelled**). Anything left is counted as a drop. | Retrying during teardown is what starves the terminal row. |
| 3 | **Terminal phase, which ALWAYS runs**: if `_lastEnqueued` was not delivered, make exactly **one** attempt at it, bounded by the terminal delivery timeout (**10 s**, **500 ms** when cancelled), **ignoring the circuit and ignoring the backlog**. | This is the guarantee the whole feature exists for. |
| 4 | Cancel the pump's token, then **`await` the pump, bounded** | `LogServer` documents the trap verbatim: *"disposing that CancellationTokenSource while a wait is outstanding on it is undefined behaviour, so nothing may touch it until every dispatched request has returned."* A cancelled token does not mean `SendAsync` has returned. |
| 5 | Dispose the `HttpClient`, then the `CancellationTokenSource` | The transport goes **last**, after the pump has provably returned. |
| 6 | Emit the buffered notices + the summary through `onNotice` | Last, so the counts are final and no console write races the live table. |

Worst-case teardown is **20 s** normally and **~500 ms** on a cancelled run.

**Put a comment on `DisposeAsync` citing plan 35 §9.3 and the corrected rule**, because the inverted
version of this lesson caused two of this design's blockers:

> **Signal wind-down first. Drain second. Tear the transport down last.**

Plan 35 §9.3 is the recorded cost of getting exactly this wrong one surface over: `LogServer` drained
in-flight requests three lines **too late**, after the listener had already torn down the shared
request queue, so the best-effort final delivery of `run-finished` failed *every single time* across
~10 measured variants. The verdict there: **"A 'best-effort' mechanism that is 0% effective is not
best-effort; it is dead code."** `LogServer` always cancelled first; what moved was the drain. So the
rule is not "cancel last". Layer 3's transport is the `HttpClient`, so that is what goes last.

**The run's `CancellationToken` selects the budget and NOTHING ELSE.** The drain itself must never
observe it — a token that is already cancelled would otherwise skip the drain entirely, which is the
bug this shape exists to avoid. The reason the cancelled budget is ~500 ms: `Program.cs` passes no
`InvocationConfiguration`, so System.CommandLine's default ~2 second `ProcessTerminationTimeout` bounds
the whole Ctrl-C unwind — Scheduler cancellation, journal writes, `RunFinished`, this drain, and the
log server's own 5 s drain. Loopback RTT is routinely sub-millisecond, so 500 ms is a real chance
rather than a gesture.

**`DisposeAsync` MUST NOT THROW. The WHOLE BODY sits inside a `catch (Exception)`.** `await using`
puts this dispose in a compiler-emitted `finally` that spans past `return exitCode;` in
`RunCommand.RunAsync`, **so an exception thrown here replaces the in-flight return and turns a wholly
green run into an unhandled exception** — and on the fault path it replaces the original exception and
destroys the diagnosis. The repo already knows this shape: `RunCommand` wraps a journal write in a
narrow `catch` for exactly this reason. A delivery failure — a timeout, any status, a full queue, a
drain with rows still pending — **can never change the run's exit code, its verdict, its journal, or
its timing** beyond the bounded drain.

**Post-dispose emissions.** After `DisposeAsync` returns, `RunEventStream` still exists; a late row
calls `Emit` on a completed channel. `TryWrite` returns false there, so it is a counted drop and a
no-op — **never a throw**. There is no known path that emits after `RunFinished`, and the sink is
nonetheless required to survive one.

### Done when

Every test in `WebhookEventSinkTests` passes, and nothing outside
`src/Guardrails.Core/Execution/WebhookEventSink.cs` changed. `WebhookPolicyTests` must still pass —
it lives in the same production file and the plan-level all-tests guardrail will catch it if it does
not.
