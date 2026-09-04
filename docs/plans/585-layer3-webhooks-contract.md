# Architecture: `--on-event <url>` webhook delivery — #585 layer 3

Design of record for the last of #585's three layers. Layers 1 (`events.jsonl`) and 2 (`GET /events`)
shipped in PR #599 / plan 35; this document decides what happens when the harness POSTs those same rows
to an endpoint it does not control.

**Status:** proposed. To be delivered as a **draft PR for inline review** before any implementation
milestone starts (#106). Implementation plan (`docs/plans/36-*.md` or equivalent) follows review.

**Binds to:** `RunEventStream`, `IRunObserver` and the §8.1 row shape as they stand on
`feat/585-layer3-webhooks` at `57887e5`. This design **defines no event vocabulary of its own** — it
adds exactly one field to the shipped one, and the reason it must is §4.2.

---

## Maintainer rulings applied (settled — not re-opened here)

> **1. Webhooks only. The `ws:` endpoint is SUPERSEDED, not deferred.** #585's stated rationale for
> layer 2 was *"the agent-side monitor takes a `ws:` source natively, so an agent subscribes with no
> grep anywhere in the path."* A webhook **inverts** that: the agent RECEIVES POSTs and never connects.
> It removes the same failure class more directly, so the `ws:` question is closed on the record, not
> left dangling. §2.1 states the closure and what it costs.
>
> **2. A failed delivery must NEVER affect the run's verdict.** Bounded retry with backoff, then give
> up and record the drop. `events.jsonl` stays the durable record a consumer re-reads. Same posture
> that made `run-finished` best-effort over the wire. §5 is that policy, concretely — and note the
> ruling is *"never affect the run"*, **not** *"never tell anyone"*: §5.4 is where a drop is recorded.

---

## What's being asked

Give the harness a `--on-event <url>` flag that POSTs each `events.jsonl` row to an endpoint as it is
written, so an external consumer — a supervising agent, a CI wrapper, a dashboard — is *told* rather
than having to connect, tail, poll, or grep. #585's measured defect is that every observability failure
in the current design produces **silence**, and silence is what a healthy quiet run also produces.
Layer 3's job is to make the harness the active party, and then to make *its own* failures loud.

**Ambiguity named, and how I narrowed it.** "Webhooks" spans two products that share a name:

1. **A fire-and-forget notifier** — the endpoint is a convenience; if it misses an event, nothing
   downstream is wrong, because the file is the record.
2. **A delivery guarantee** — the endpoint is the consumer of record, and the harness owes it
   at-least-once delivery with a durable outbound queue that survives process exit.

Ruling 2 chooses (1) and I am building (1) without hedging toward (2). Everything downstream follows
from that: no durable queue, no cross-run redelivery, no dead-letter replay, no acknowledgment
protocol. **The reconciliation mechanism is the consumer re-reading `events.jsonl`,** which layer 1
already provides and §8.1 already tells consumers to do. If a future consumer genuinely needs (2),
that is a different feature with a durable spool and its own crash semantics — named in §8.

**Second ambiguity: what "the payload" means.** #585 says "posts the events," but the rows now carry
free text (`detail`) that can hold absolute paths, model prose, and a guardrail script's raw stdout
(§6.2). Sending "the events" and sending "the events minus their free text" are materially different
products. I split them deliberately: **structured fields always, free text on request** (§4.3, §6.3).

---

## Placement

| Item | Placement |
|------|-----------|
| `IEventSink` seam + the sink call in `RunEventStream` | **harness** — `Guardrails.Core.Execution` |
| `WebhookEventSink` (queue, pump, retry, circuit, drop accounting) | **harness** — `Guardrails.Core.Execution` |
| `--on-event`, `--on-event-detail`, env fallback, URL validation, lifetime | **harness** — `Guardrails.Cli`, one command |
| `bracket` on the §8.1 row | **schema** — `02-schemas-and-contracts.md` §8.1 (§4.2 is why it lands here and not later) |
| The webhook wire contract, headers, failure policy, security posture | **schema** — a NEW `02-schemas-and-contracts.md` **§8.3** |
| The two new harness-process env vars | **schema** — `02-schemas-and-contracts.md` §5.1's closing paragraph |
| A `ws:`/SSE endpoint on the log server | **SUPERSEDED** (ruling 1) — §2.1, not a v2 bet |
| Batched delivery | **rejected outright** — §4.1, not deferred |
| A `webhook-dropped` event kind | **rejected** — §5.4, and the reason is the interesting part |
| A `guardrails.json` key for the URL | **rejected** — §6.4, and the reason is a security argument, not a taste one |
| Multiple endpoints | **follow-up issue** — §8, blocker named (per-endpoint credentials) |
| HMAC body signing | **follow-up issue** — §8 |
| Sender-side `kind` filtering (`--on-event-kinds …`) | **rejected** — §8, on #585's own rule |
| A durable outbound spool / cross-run redelivery | **out of scope** — §8, and it is a different product (above) |
| Webhooks from `guardrails logs` / `guardrails attach` | **out of scope** — §8 |
| Capping / relativizing `detail` **in `events.jsonl` itself** | **out of scope** — §6.3; the file keeps full fidelity by design |

---

## Invariants in play

1. **#4 — the SSOT is the schema SSOT, and a contract change lands in the SAME change that motivates
   it.** Two contract changes here: the new `bracket` field on §8.1's row, and the whole of §8.3. Both
   are written verbatim in §7. The `bracket` field is the exact `faultKind` precedent replayed: a
   property that was cosmetic while rows stayed on the box becomes load-bearing the moment they are
   POSTed to someone else's server (§4.2).
2. **#2 — the harness is the single writer of merged state.** This one *decides* the central seam.
   The tempting shape — a `WebhookProjection` decorator sitting beside `RunEventStream` in
   `BuildObserverChain` — makes the row shape have **two writers** and the `seq` counter have **two
   independent instances that provably disagree under parallel workers** (§3.1). The design instead
   feeds the sink from *inside* `RunEventStream`'s append lock, so one row, one `seq`, one
   serialization, two destinations.
3. **#5 — honest halts; nothing is marked done unverified.** Two applications. (a) A delivery that
   failed must never let the run *look* delivered: drops are counted and reported, and the run summary
   prints even at zero (§5.4). (b) The reverse — a delivery failure must never mark the RUN failed:
   the exit code is untouched on every path (ruling 2).
4. **#6 — plain files, light setup.** No outbound spool, no database, no daemon. The drop record is one
   plain text file next to `events.jsonl`, and the recovery mechanism is re-reading a file that already
   exists.
5. **#1 — deterministic over prompt-judges** — strained in one place worth naming: the temptation to
   *redact* `detail` with a secret-shaped-string heuristic. That is a prompt-judge in regex clothing —
   it certifies nothing and would give an operator false confidence. Rejected in favor of a
   deterministic rule an operator can reason about: withheld by default, included verbatim (capped)
   when a human passes a flag (§6.3).

**Where the design strains an invariant, stated plainly.** #6's spirit — *plain files, light setup* —
also carried an unstated security posture that this feature is the first thing in the harness to break.
`LogServer`'s own class comment (`src/Guardrails.Cli/Ui/LogServer.cs:13`) binds the log viewer to
`127.0.0.1` because **"logs may echo secrets — it is NEVER exposed off the local machine."** `--on-event`
is the first mechanism in the product that sends run content off the machine at all. That is not a
reason to refuse the feature; it *is* the reason §6 is a real audit and not a paragraph.

---

## 1. What layer 3 inherits (verified on this branch — not re-derived)

Stated here so the design's premises are checkable, per the issue's own "recording what layer 3
inherits" comment:

- `RunEventStream` (`src/Guardrails.Core/Execution/RunEventStream.cs`) and `ObserverProjection` are two
  projections off ONE emission seam, both constructed in `RunCommand.BuildObserverChain`
  (`src/Guardrails.Cli/Commands/RunCommand.cs:2382`), called from the live branch at `:537` and the
  `--no-ui` branch at `:542`. It already takes `runId`.
- Six kinds: `task-started`, `attempt-started`, `guardrail-finished`, `attempt-finished`,
  `task-settled`, `run-finished`.
- `seq` is monotonic per process, assigned inside the append lock (`RunEventStream.cs:254-264`), along
  with `at`. **Ordering and retry key on `seq`, never on `at`** — `at` is neither unique nor monotonic
  under parallel workers.
- `run-finished` carries `exitCode` (null on an unhandled fault) and `faultKind` (a **type name**,
  never a message).
- One `runId` can produce **more than one** `run-finished` — a resume, or a second concurrent process;
  nothing locks a plan folder.

---

## 2. Decision 0 — the `ws:` question, closed

### 2.1 `ws:` is superseded, not deferred

Layer 2 shipped as NDJSON over chunked HTTP rather than SSE or WebSocket, and #585 flagged rather than
quietly accepted the substitution: its stated rationale was *"the agent-side monitor takes a `ws:`
source natively, so an agent subscribes with no grep anywhere in the path."* That rationale is now
**satisfied by a different mechanism**, and the `ws:` endpoint should not be built.

The rationale was never about the WebSocket protocol. It was about **removing the consumer's obligation
to discover, connect to, and stay attached to something**. A `ws:` endpoint removes the grep but keeps
three obligations the webhook removes outright:

| Obligation | `ws://…/events` | `--on-event <url>` |
|---|---|---|
| Learn the port (ephemeral, printed as **prose on stdout**) | still required — and it is the same scraping problem #585 exists to delete, one layer down | gone: the operator supplies the address |
| Establish and hold a connection | required; a dropped socket is the consumer's problem to notice | gone: the harness dials out per row |
| Notice its own subscription died | **silent** if the consumer's reconnect logic is wrong — the #585 defect shape | the harness counts and reports failures (§5.4) |

A `ws:` endpoint would remove one of three; the webhook removes all three, and moves the "did it
arrive?" accounting to the side that can actually answer it. Building both would mean maintaining two
transports for one stream, with the weaker one's failures still silent.

**What is lost, stated plainly.** An agent monitor whose *only* input mode is a `ws:` URL still needs an
adapter — now a ~10-line HTTP receiver instead of a WebSocket client. That is a real cost and it is
smaller than the port-discovery problem it replaces. **#585 closes with layer 3; no `ws:` follow-up
issue is filed.**

### 2.2 The strongest argument for building nothing at all

Recorded because it is the honest competitor and it nearly wins: **a 20-line shell shim already does
this.** `curl -N http://127.0.0.1:PORT/events | while read -r line; do curl -sX POST -d "$line" "$URL"; done`
delivers the same rows with zero new harness surface.

**Why it loses, and every reason is a #585 reason:**

1. It needs `PORT`, which is ephemeral and reaches the operator **as prose on stdout** — the exact
   pattern-matching-over-console-text this issue was filed to eliminate.
2. If the shim dies — a broken pipe, a bad `read`, the terminal closing — **nothing anywhere records
   that**, and the consumer sees a quiet stream, which is what a healthy run also looks like. The
   failure mode of the workaround is the defect the issue is about.
3. It has no retry, no drop accounting, and no way to tell a slow endpoint from a dead one.
4. It is per-platform (that line is not a Windows line), and Windows is the maintainer's box.

The harness-side version exists precisely so the *delivery mechanism's own failures* are counted and
reported by something that cannot silently disappear.

---

## 3. Decision 1 — where the projection lives, and how the POST leaves the run's thread

### 3.1 The seam: fed by `RunEventStream`, NOT a sibling of it

**Decision: `RunEventStream` gains an optional `IEventSink`, and hands it the already-stamped,
already-serialized line from inside its append lock. The webhook dispatcher implements that sink.**

The obvious shape — a third decorator in `BuildObserverChain`, sibling to `RunEventStream` and
`ObserverProjection` — is **wrong, and it is wrong for a reason that is easy to miss.** A sibling
decorator would have to build its own `EventRow` from the `IRunObserver` call, which means:

- **Two writers of one row shape.** Every future kind and every future field would have to be added in
  two places and could disagree in one. That is the second-owner problem the #595 design rejects four
  separate times, and #585's own "do NOT invent a second vocabulary."
- **Two `seq` counters that provably disagree.** The premise is not hypothetical: `IRunObserver`'s own
  contract is *"Implementations MUST be thread-safe — M4 workers emit events concurrently"*
  (`src/Guardrails.Core/Execution/IRunObserver.cs:8-9`), and `RunEventStream.AppendLine` locks precisely
  because of it. Two threads are therefore inside the chain at once, and two decorators each holding
  their own lock can assign their counters in **opposite orders**. Consider chain `W → R → inner`:
  T1 takes W's lock and gets `seq=1`; T2 takes W's lock and gets `seq=2`; T2 reaches R first and gets
  `seq=1` there; T1 gets `seq=2`. The wire row's `seq` and the file row's `seq` for the same event are
  **swapped**. A consumer told to reconcile a webhook against `events.jsonl` on `(runId, seq)` would
  match the wrong rows — silently. Ruling 2's entire recovery story depends on those keys being the
  same key.

Feeding the sink from inside `AppendLine` makes the identity structural rather than coincidental: **one
`EventRow`, one `seq`, one `at`, one `bracket`, one `JsonSerializer.Serialize` call, two destinations.**

```csharp
// src/Guardrails.Core/Execution/IEventSink.cs — NEW
/// <summary>
/// A second destination for the §8.1 rows RunEventStream writes: the row leaves the process as well as
/// landing in events.jsonl. There is exactly one implementation (WebhookEventSink) and the seam exists
/// for two reasons that are not speculative: it keeps HttpClient and delivery policy out of
/// RunEventStream, and it is the ONLY way a test can prove the wire body and the file line are the same
/// bytes without standing up a server.
/// </summary>
public interface IEventSink
{
    /// <summary>
    /// Whether this sink receives the free-text <c>detail</c> field (§8.3). False = the wire copy
    /// carries a fixed withheld marker instead. The file row is NEVER affected either way.
    /// </summary>
    bool IncludeDetail { get; }

    /// <summary>
    /// Hand off ONE complete §8.3 JSON line. Called on the run's own thread, INSIDE
    /// RunEventStream's append lock. It MUST return in microseconds and it MUST NOT throw:
    /// a throw here propagates into the harness's hot path holding a lock, and a delivery
    /// mechanism is not permitted to affect the run (#585 layer 3 ruling 2). Implementations
    /// enqueue and return; a full queue is a recorded DROP, never a wait.
    /// </summary>
    void Emit(string jsonLine);
}
```

And the change inside `RunEventStream.AppendLine` — the whole of it:

```csharp
lock (_gate)
{
    EventRow stamped = row with { Seq = ++_seq, At = DateTimeOffset.UtcNow, Bracket = _bracket };
    string line = JsonSerializer.Serialize(stamped, LineOptions);

    Directory.CreateDirectory(_directory);
    File.AppendAllText(Path.Combine(_directory, "events.jsonl"), line + "\n", Utf8NoBom);

    if (_sink is not null)
    {
        // The wire copy differs from the file line in EXACTLY ONE field, and only ever `detail`
        // (§8.3). When the row has no detail — every kind but guardrail-finished and task-settled —
        // `with` produces an equal record and this serializes to the identical string.
        EventRow wire = stamped with
        {
            Detail = _sink.IncludeDetail ? CapDetail(stamped.Detail) : DetailWithheld
        };
        _sink.Emit(JsonSerializer.Serialize(wire, LineOptions));
    }
}
```

Three properties, each deliberate:

- **After the file append.** If the append throws (the known concurrent-run `IOException` hazard §8.1
  scopes), nothing is delivered either — the wire never carries a row the durable record does not have.
  Keeping those two consistent is what makes "re-read the file" a complete recovery.
- **Inside the lock.** Enqueue order therefore equals file order equals `seq` order, so the normal
  delivery path is strictly in-order without the receiver buffering and re-sorting. The cost is a
  lock-free `Channel.TryWrite` inside the gate — nanoseconds — and the contract above forbids anything
  slower.
- **Serialized twice per row.** Accepted. §8.1 calls this stream "semantic and low-frequency"; a
  15-task run emits a few hundred rows over hours.

### 3.2 Getting the POST off the run's thread

`WebhookEventSink` (`src/Guardrails.Core/Execution/WebhookEventSink.cs`):

- A **bounded `Channel<string>`**, capacity **1024**, **`FullMode = DropOldest`**, created with the
  `Channel.CreateBounded<T>(BoundedChannelOptions, Action<T> itemDropped)` overload so every displaced
  row is counted rather than vanishing. `Emit` calls `TryWrite`, which never blocks.

  **`DropOldest`, not `DropWrite` or `Wait`, and the reason is `run-finished`.** With any newest-loses
  policy, a stalled pump means the queue is full exactly when the terminal row arrives — so the single
  row a CI wrapper exists to receive is the one guaranteed to be dropped. `DropOldest` inverts that:
  the newest row always gets in, and what is lost is the stalest event, which is also what a
  late-attaching supervisor cares least about. Ordering is unaffected (dropping from the head leaves
  the tail in order); a gap is already the documented meaning of a drop (§4.4).

- **The `itemDropped` callback runs on the RUN's thread, inside the append lock**, so it may do exactly
  two things: `Interlocked.Increment` a counter and record the delivery id in a field. **Every
  `webhooks.log` write happens on the pump thread or during dispose — never on the run's thread.** File
  IO under the append lock would be the one way this design could measurably slow a run.
- The whole of `Emit` is inside a `catch (Exception)` that increments the drop counter — the seam's
  "must not throw" contract is enforced by the implementation, not merely asserted in a doc comment.
- **One** background pump task started in the constructor, reading `ReadAllAsync`. One pump, not a fan-out:
  serial delivery is what keeps arrival in `seq` order, and a retrying row delays later rows rather than
  being overtaken by them.
- A single `HttpClient` built on `new SocketsHttpHandler { AllowAutoRedirect = false }` (§6.5), with
  `Timeout` set per-request via a `CancellationTokenSource`, not on the client.

**Rejected alternatives:**

| Alternative | Rejected because |
|---|---|
| A sibling `WebhookProjection` decorator in `BuildObserverChain` | Two row writers and two disagreeing `seq` counters — §3.1. This is the shape #585's own implementation note suggests ("a webhook projection is a third sibling"), and it is the one thing in that note this design overrules, with the race written out. |
| A separate process/component **tailing `events.jsonl`** | Re-introduces everything layer 3 exists to remove: a poll interval, a file-position cursor to persist, a partial-line read at the tail, and — decisively — **its own failure is silent**, since nothing in the run can tell whether the tailer is alive. That is #585's defect relocated into the fix. |
| Subscribing internally to `GET /events` | Couples webhooks to the log server, which `--no-log-server` disables and whose bind can fail. A run that opted out of the viewer would silently get no webhooks. It also inserts a network hop between two objects in one process. |
| Blocking POST inline in the observer call | Stalls the run on someone else's server. Violates ruling 2 in the most direct way available. |
| `Task.Run(() => Post(row))` fire-and-forget per row | Unbounded concurrency (a slow endpoint spawns a task per row), no ordering, no back-pressure signal, and unobserved exceptions. A channel is strictly better and no larger. |
| `IProgressSink` / a new `IRunObserver` member | Wrong seam: this is not an observation, it is a second destination for an existing projection. Adding an interface member would put the swallow hazard (plan 34 §3) in play for zero benefit. |

### 3.3 Lifetime and teardown — the `LogServer` lesson, applied

The single most valuable delivery in the whole feature is the terminal `run-finished` row, because it
carries the `exitCode` a CI wrapper branches on. It is appended in the `finally` at the very end of
`RunCommand.RunAsync`. **If the dispatcher is torn down before or during that, the payoff is lost — and
lost silently.**

Plan 35 §9.3 is the recorded cost of getting exactly this wrong one surface over: `LogServer.DisposeAsync`
drained in-flight requests **three lines too late**, after `_listener.Stop()` had already torn down the
shared HTTP.sys request queue, so the "best-effort" final delivery of `run-finished` failed *every single
time* across ~10 measured variants. The finding, verbatim: **"A 'best-effort' mechanism that is 0%
effective is not best-effort; it is dead code."** The fix was to move the drain above the teardown, and
`LogServer` now additionally defers the listener stop behind a 250 ms linger
(`src/Guardrails.Cli/Ui/LogServer.cs:1147-1201`).

Layer 3 takes that lesson as a construction rule, not a hope:

1. **The sink is constructed BEFORE the observer chain and disposed AFTER the `RunFinished` bracket.**
   Concretely, in `RunCommand.RunAsync`, on the line after `diagramSeed` is read (`:505`) and before
   the `OnTheFlyDiagramObserver? diagramObserver = null;` bracket opens (`:518`):

   ```csharp
   await using var eventSink = WebhookEventSink.TryStart(
       onEventUrl, onEventAuth, onEventDetail, userAgent, logsRoot, io.Out.WriteLine);  // null when no URL
   ```

   **Verified against the real brace structure rather than assumed** — this is the claim whose failure
   would repeat plan 35 §9.3 exactly, so it was read out of the file, not reasoned about. `logsRoot`
   is in scope from `:499`. `await using var` compiles to an implicit try/finally whose try runs from
   `:506` to the close of the enclosing outer `try` block at `:747`; the explicit `RunFinished`
   bracket (`try` at `:521`, `catch` at `:730`, `finally` at `:741-746`) and the #333 block (`finally`
   at `:716-728`) are both **nested inside** it. So on the normal `return exitCode;` at `:714` — and
   identically on a halt return and on an unwinding exception — the unwind order is:

   > #333 final-sites `finally` (`:728`) → **`RunFinished` `finally` (`:745`)** → **`eventSink`
   > dispose** (implicit, at `:747`) → `logServer.DisposeAsync()` (`:752`).

   That is the only correct order, and nothing between `:506` and `:521` can return past the
   construction.

2. **`DisposeAsync` drains before it cancels.** In order: complete the channel writer; `await` the pump
   bounded by `Task.WhenAny(pump, Task.Delay(DrainTimeout))`; only then cancel the pump's token, count
   anything still unsent as a drop, dispose the `HttpClient`, and emit the summary line (§5.4). **The
   cancellation is the last step, not the first** — that inversion is the whole of the #35 bug.

3. **The drain does NOT observe the run's cancellation token.** On a Ctrl-C run, `run-finished` carries
   `exitCode: 3` and is precisely the event a supervisor most needs; cancelling the drain would drop it.
   The 30-second hard bound is what keeps Ctrl-C from being taken hostage.

4. **A test pins the ordering, not a comment.** An integration test runs a real plan against a real
   loopback `HttpListener` and asserts a `run-finished` body arrives. Per plan 35's own measurement,
   this is the assertion that would have caught the `LogServer` defect and did not exist.

**Post-dispose emissions.** After `DisposeAsync` returns, `RunEventStream` still exists; a late row
would call `Emit` on a completed channel. `TryWrite` returns `false` there, so it is a counted drop and
a no-op — never a throw. There is no known path that emits after `RunFinished`, and the sink is
nonetheless required to survive one.

---

## 4. Decision 2 — the wire contract

### 4.1 One POST per event. The body IS the row.

**Decision: `POST <url>`, `Content-Type: application/json; charset=utf-8`, body = exactly one §8.1 row —
the same bytes written to `events.jsonl` — with the single documented `detail` transformation of §4.3.**

| Rejected | Why |
|---|---|
| **Batching** (N rows per POST, or a flush interval) | Four independent reasons and any one is sufficient. (a) It needs a flush-interval knob and a "partial batch at shutdown" case, both of which are new ways to lose the terminal row. (b) A batch body is a NEW shape — an array, or an envelope with a `rows` key — i.e. the second vocabulary #585 forbids. (c) It optimizes a cost that does not exist: §8.1 declares this stream low-frequency. (d) A partially-accepted batch has no honest retry: re-POSTing duplicates the rows that landed. |
| **An envelope** (`{"event": {...}, "meta": {...}}`) | Every field an envelope would add — run id, sequence, kind, timestamp — is already on the row. And "the body is the `events.jsonl` line" is a property a reviewer can verify by diff, and a test can assert byte-for-byte. That is worth more than any envelope field. |
| **Form-encoded / a Slack-shaped `{"text": …}` body** | Would fork the vocabulary for one receiver's convenience and put prose where structure belongs. A Slack user puts a relay in front; the harness is not the place for per-vendor payload shapes. |
| **`application/x-ndjson`** | That is layer 2's stream media type. Here the body is exactly one JSON object, and saying so correctly is free. |

### 4.2 The idempotency key, and the one schema change layer 3 must make

**Decision: the delivery key is `(runId, bracket, seq)`. A new `bracket` field is added to the §8.1 row.**

This is the *only* change layer 3 makes to the shipped vocabulary, and it is not optional — without it,
layer 3 ships a silent-data-loss bug on its most important path.

**The bug, concretely.** §8.1 already states that `seq` "restarts at 1 for a resume, which appends a
fresh bracket to the same file," and that a `runId` can produce more than one `run-finished`. A **file**
reader survives this because rows arrive in file order and a bracket can be segmented positionally. A
**webhook receiver** cannot: it gets independent POSTs, and on a resume hours later it receives
`{"runId":"7cc3","seq":1,…}` again. A receiver that dedupes on `(runId, seq)` — which is what §8.1
currently tells it the key is — **silently discards the entire resumed run.** Nothing anywhere reports
it. That is #585's own defect shape, manufactured by layer 3.

**`bracket`** is a short random id (8 hex chars from a `Guid`) generated once in `RunEventStream`'s
constructor and stamped on every row inside the append lock, exactly like `seq` and `at`. Zero call-site
changes: one field on `EventRow`, one line in `AppendLine`, one line in the constructor.

| Rejected | Why |
|---|---|
| A **delivery-only header** (`X-Guardrails-Delivery-Id` with a per-process GUID, row unchanged) | Fixes the wire and leaves the file broken. Worse: §8.1 *tells the receiver to re-read the file* on a gap, so it would then need two different key schemes for the same rows. Decisive. |
| **Key on `(runId, seq, at)`** | `at` is neither unique nor monotonic (§1) and is explicitly ruled out. Two rows can share an `at` outright on Windows. |
| **Make `seq` durable across processes** | Would mean reading the file back to find the high-water mark — a reader inside the writer — for an ordering a bracket already gives. Rejected in the #595 design for the same reason; nothing here changes it. |
| **Document "a resume replays seq 1..n; tolerate it"** | Dishonest. The failure is silent and the receiver has no way to detect it. A contract that needs the consumer to guess is the thing #585 exists to delete. |
| Name it `epoch` / `processId` / `instance` | `epoch` reads as unix time. `processId` implies an OS pid, which is reused and would be a genuinely bad key. **`bracket` is the word §8.1 already uses for this concept** ("a monotonic counter within this PROCESS's bracket"; "`run-finished` is what closes one"), so the field names the thing the SSOT already defines. |

**Bonus the field buys.** "Which `run-finished` is mine?" becomes answerable rather than positional: it
is the one carrying the bracket whose rows you were receiving. §8.1's current guidance — "take the LAST
`run-finished` as current" — is a file-reader's rule that a webhook receiver cannot apply at all.

### 4.3 Headers

Three custom headers, each earning its keep, plus the two standard ones:

| Header | Value | Why it exists |
|---|---|---|
| `Content-Type` | `application/json; charset=utf-8` | one JSON object |
| `User-Agent` | `guardrails/<version>` | an operator reading their own access log must be able to tell what is calling. **The version string is INJECTED by the CLI, not read in Core:** `GuardrailsVersion` lives in `src/Guardrails.Cli/GuardrailsVersion.cs` and `Guardrails.Cli` references `Guardrails.Core`, not the reverse — so `WebhookEventSink` takes the finished `User-Agent` value as a constructor parameter. Reading the executing assembly from Core would silently report `Guardrails.Core`'s own version (`1.0.0`) instead of the tool's. |
| `X-Guardrails-Delivery-Id` | `<runId>:<bracket>:<seq>` | **the idempotency key, pre-assembled.** A receiver dedupes at the edge — a proxy, a Lambda, a queue — without parsing JSON. Stable across retries of the same row. |
| `X-Guardrails-Event-Kind` | the row's `kind` | route or ignore without parsing. Note this is a *receiver-side* convenience only; the sender never filters (§8). |
| `X-Guardrails-Delivery-Attempt` | `1`-based attempt number | the one fact NOT in the body, and exactly what an operator needs when they see the same delivery twice |

`Authorization` is added verbatim from `GUARDRAILS_ON_EVENT_AUTH` when set (§6.4).

**Rejected: `X-Guardrails-Signature` (HMAC over the body).** Real value — it proves the body was not
tampered with, where the auth header only proves the caller knew a secret. But it costs a shared-secret
derivation, a canonicalization rule, a scheme-version prefix, and a documented verification recipe, and
over TLS to the operator's own endpoint the auth header carries the same trust in one line. **v2, with
an issue** (§8). Rejected outright: separate `X-Guardrails-Run-Id` / `-Seq` / `-Bracket` headers — three
headers a receiver has to reassemble into the key it actually wants.

### 4.4 What a receiver is promised, and what it is not

Stated as the receiver contract because a webhook consumer has no other documentation:

- **Deliveries within one `(runId, bracket)` are ATTEMPTED in strictly increasing `seq` order.** One
  serial pump, enqueued under the append lock. A retry *delays* later rows; it never lets them overtake.
- **Arrival order is not guaranteed** if the receiver sits behind a load balancer or multiple workers.
  Order by `seq`, not by arrival.
- **`seq` is NOT contiguous.** A gap means a drop (§5), and a drop means: **the row is in
  `events.jsonl` and was never delivered.** A receiver that must be complete re-reads that file. This
  is the one sentence that makes ruling 2 safe.
- **More than one `run-finished` per `runId` is normal.** Each carries its own `bracket`.
- **`taskId` may contain exactly one `/`** on a waved plan (`wave-02-build/03-implement`,
  `src/Guardrails.Core/Loading/PlanLoader.cs:1026-1027`). It is never absolute and never contains `..`.
- **Any 2xx is success. The response body is ignored entirely** — read to at most 8 KB and discarded, so
  the connection is released. There is no acknowledgment protocol, no "please retry" reply, no
  "unsubscribe" reply. **A receiver cannot influence the run in any way**; that is a security property
  (§6.5), not an omission.
- **`detail` is withheld by default** and carries the fixed marker `(detail withheld; pass
  --on-event-detail)`. With `--on-event-detail` it is the file's value, truncated at 2000 characters
  with a `…[truncated]` suffix. **Every other field is byte-identical to the `events.jsonl` line.**

---

## 5. Decision 3 — failure policy, concretely

**Preamble, and it is the ruling: none of this can change the run's exit code, on any path.** Not a
timeout, not a 401, not a full queue, not a drained-with-pending shutdown. The exit code is computed
exactly as it is today; the dispatcher has no input to it.

### 5.1 What is retryable

| Condition | Retry? | Reason |
|---|---|---|
| `2xx` | — delivered | |
| `408 Request Timeout`, `429 Too Many Requests` | **yes** | the server explicitly said "later" |
| `5xx` (500–599) | **yes** | server-side, transient by definition |
| Connection refused, DNS failure, TLS handshake failure, socket error | **yes** | an endpoint may still be starting up; the common case for a sidecar |
| Per-attempt timeout (10 s) | **yes** | a slow receiver is not a wrong request |
| `3xx` | **NO — hard failure** | redirects are not followed (§6.5). Retrying reproduces the redirect. |
| Any other `4xx` (400, 401, 403, 404, 405, 413, …) | **NO** | the request is malformed, unauthorized, or aimed at nothing. A retry re-sends byte-identical content and fails identically; retrying a 401 against a misconfigured token is pure waste and hides the real problem. |
| Any other exception from the client | **yes, treated as transient** | conservative; the bounds below cap the cost of being wrong |

`static internal bool IsRetryable(HttpStatusCode? status, Exception? error)` is a pure function and is
unit-tested directly — no HTTP server needed to prove the classification.

### 5.2 The schedule

| Knob | Value | Why this value |
|---|---|---|
| Attempts per row | **4** (initial + 3 retries) | covers a receiver restart, the dominant transient |
| Backoff | **1 s, 2 s, 4 s**, each × jitter in `[0.5, 1.5)` | jitter so a parallel burst of failures does not resynchronize |
| Per-attempt timeout | **10 s** | matches `PlanPreflightPhase.EndpointProbeTimeout`; a receiver slower than this is not healthy |
| **Hard per-row ceiling** | **45 s** | enforced by a per-row `CancellationTokenSource`, so the schedule can never exceed it however the attempt timings fall. Stated as a number rather than left to be multiplied out of the schedule. |
| Queue capacity | **1024** rows, `DropOldest` | a run emits hundreds, so this only fills when the pump is stalled — which is exactly when dropping is correct, and `DropOldest` is what keeps the terminal row from being the one dropped (§3.2) |
| Shutdown drain bound | **30 s** | §3.3. Paid only in the narrow "endpoint died near the end" case; in the healthy case it costs milliseconds and with the circuit open it costs nothing |
| `detail` cap when included | **2000 chars** — `GuardrailFailureReason.MaxChars` (`src/Guardrails.Core/Execution/GuardrailFailureReason.cs:25`), **promoted from `private const` to `internal const`** so there is ONE owner of the number rather than a copy that drifts | an existing in-repo precedent for capping exactly this kind of text, and it stops one runaway compiler line becoming a multi-megabyte POST |
| Response body read | **≤ 8 KB, discarded** | releases the connection without buffering a hostile response |

### 5.3 The circuit: give up loudly, once

**After 5 CONSECUTIVE rows exhaust all their attempts, the endpoint is marked failing for the rest of
the run.** Subsequent rows are dropped on arrival — counted, no HTTP attempted — and one console line
prints at the moment it opens:

```
Webhook: giving up on https://hooks.example.com/… after 5 consecutive delivery failures (last: HttpRequestException). Events keep landing in events.jsonl; the run is unaffected.
```

**It never closes.** No half-open probe, no timer. Rationale: 5 rows × 4 attempts = 20 consecutive
failed POSTs is not a transient, a half-open state machine is real complexity for a case the durable
file already covers, and "when does it re-open?" is a question with no good answer that anyone would
have to keep answering.

**What it buys, and why one mechanism does two jobs:** it bounds wasted work on a dead endpoint, it
makes shutdown instant instead of a 30-second drain, and it is the natural place for the EARLY warning.
The alternative — no circuit, one warning on first exhaustion — gets the warning but keeps 800 pointless
POSTs and the full drain. One mechanism, three benefits, ~15 lines.

**The cost, stated plainly, and it is real: an open circuit drops `run-finished` too.** I considered a
special case — always attempt the terminal row once even when open — and rejected it, because it would
make the dispatcher read `kind`, i.e. know the vocabulary, which is the one thing §4.1 buys by keeping
the body opaque. The covering mechanism is the one already in the contract: a consumer that must not
miss the terminal event re-reads the file. **This is the design decision I hold least firmly — see
§9.**

### 5.4 Where a drop is RECORDED

Ruling 2 is *"never affect the run"*, not *"never tell anyone."* Two surfaces, chosen deliberately:

**1. One console line at the end of every run that used `--on-event`, INCLUDING at zero drops.** Emitted
by `WebhookEventSink.DisposeAsync` through the `onNotice` callback, so it fires on the normal path, the
halt path, and the unwinding-fault path alike, and after the final counts are known:

```
Webhook: 211 delivered, 0 dropped -> https://hooks.example.com/…
Webhook: 197 delivered, 14 dropped (endpoint failing since 14:22:07) -> https://hooks.example.com/…
```

The zero-drop line is not noise: **silence on success is the exact defect this issue is about.** A line
that always prints is proof the mechanism ran at all.

**2. `logs/<runId>/webhooks.log`** — a plain text file beside `events.jsonl`, one line per drop
(`<at> <deliveryId> <kind> dropped after <n> attempts: <reason token>`) plus a final summary line. It
sits in the run's own log directory, which is where a post-mortem already goes, and it costs no schema
change at all (invariant #6). **It is deliberately not exposed by the log server:** `LogServer`'s routes
are `/`, `/diagram.html`, `/events` and `/tasks/{id}/…` — it serves no arbitrary file under
`logs/<runId>/`, and adding a route for this one would be new surface on a listener whose class comment
is about *not* exposing things. A file the operator opens is enough.

| Rejected | Why |
|---|---|
| **A `webhook-dropped` event kind in `events.jsonl`** | Seductive and wrong. The row would itself be enqueued for delivery, so a drop-notice that fails emits a drop-notice: a loop, or a special case carved into the one path that must stay simple. Either way it makes the dispatcher self-referential, and a later refactor would re-introduce the loop. |
| **A `webhooks` section in `run.json`** | A journal schema change (§7) for a fact the consumer can compute *exactly* on its own: diff the `(bracket, seq)` set it received against `events.jsonl`. That diff is more accurate than any summary the harness could write, so the journal field would be a second, weaker owner of a fact the durable record already holds. |
| **Silence on success** | See above. |
| **Failing the run, or a nonzero exit code, on drops** | Ruling 2. |

**And a rule that is easy to get wrong:** the drop lines and the console summary print **the exception's
TYPE NAME and the HTTP status code only — never `ex.Message`, and never the full URL.** A
`HttpRequestException` message routinely contains the whole request URI, and for many webhook services
(Slack, webhook.site) **the URL path IS the credential**. This is the `faultKind` posture applied to the
harness's own error path — see §6.6, where the same rule governs how the URL is displayed.

---

## 6. Decision 4 + 5 — configuration and security

These two decisions are one argument, so they are answered together: **the configuration surface is
chosen by the threat model.**

### 6.1 The posture this feature changes

`src/Guardrails.Cli/Ui/LogServer.cs:13` states the harness's existing position in its own words:

> Bound to `127.0.0.1` on an ephemeral port (**logs may echo secrets — it is NEVER exposed off the local
> machine**).

`GET /events` serves this exact `events.jsonl` under that guarantee. `--on-event` is the **first
mechanism in the product that sends run content off the machine**, and it does so to an address the
harness does not control. That is not a reason to refuse it — it is the reason the rest of §6 is an
audit with findings rather than a paragraph of reassurance.

### 6.2 The row-by-row audit

Every field the six kinds can carry, assessed against the maintainer's own stated bar — *can it carry a
path, a token, or source text?*

| Field | Source | Verdict |
|---|---|---|
| `kind`, `seq`, `bracket`, `at`, `attempt`, `budget`, `passed`, `exitCode`, `costUsd`, `turns`, `startedAt`, `endedAt` | harness-generated literals and numbers | **safe** |
| `runId` | harness-generated short id | **safe** |
| `outcome` | closed token sets, both kinds: `JournalJson.OutcomeToken` (13 tokens) and `RunEventStream.TaskOutcomeToken` (10). **Both throw on an unmapped member** rather than inventing a token | **safe** |
| `needsHumanKind` | closed set — `blocked-work` \| `defective-guardrail` \| absent, canonicalized three times before the row (`src/Guardrails.Core/Execution/NeedsHumanKinds.cs:21-41`) | **safe** |
| `faultKind` | a .NET type name; pinned by an existing negative test | **safe** — and note this was already narrowed *for this feature* |
| `guardrail` | the guardrail FILE's basename without extension (`PlanLoader.GuardrailName`). The absolute path lives on a different field the row never reads | **safe** — a bare author-controlled segment |
| `taskId` | a task folder name, or `wave/task` on a waved plan. Never absolute, never `..` | **safe** — it does disclose the plan's task names, which is the point of the feature |
| `runner`, `tier` | `promptRunners` registry key and `easy`\|`medium`\|`hard` — both config-declared | **safe** |
| `model` | **provider-echoed and unvalidated**: `TaskExecutor.cs:839-849` overwrites the resolved model with `ObservedModel`, scraped verbatim from the provider's own JSON | **low risk, accepted residual.** It is a model id. A hostile local endpoint could put junk there — but that same string already lands in `run.json` and the telemetry corpus, so this is not a new exposure and the operator who pointed at a hostile endpoint has a larger problem. |
| **`detail` on `guardrail-finished`** | `GuardrailResult.Reason` — for a script guardrail, the **first non-empty line of the child's stdout, uncapped** (`GuardrailRunner.cs:384-404`); for a prompt guardrail, the judge's free prose read verbatim from the verdict file (`:283-293`) | **UNSAFE.** Routinely carries absolute paths (`C:\...\Foo.cs(42,13): error CS0103: …`) and **source fragments** — and #179 deliberately made generated test guardrails re-emit assertion text and stack traces at the end of stdout, so this is by design, not by accident. A guardrail script can echo anything, including an env var. |
| **`detail` on `task-settled`** | `TaskResult.Summary`, ~40 assignment sites | **UNSAFE.** Splices absolute paths (`Scheduler.cs:4384` and `:4341` embed the absolute `feedback.md` path; `AttemptJournaler.cs:646` embeds permission-wall paths) and **raw model prose** (`AttemptJournaler.cs:543` = `$"needs human: {question}"`, straight from the agent's own state fragment; `Overwatch.cs:381` = the overwatch model's diagnosis). Not capped anywhere. |

**Finding, and it corrects the issue's own framing.** #585's layer-3 comment says `faultKind` "is the
one value on the row that can carry an absolute path, a token, or a fragment of source." **That is no
longer true.** Since layer 1's vocabulary widened, `detail` is a strictly larger exposure than
`faultKind` ever was, on two of the six kinds, and it is unbounded. Applying the maintainer's own bar
consistently is what §6.3 does.

**There is no redaction or secret-scrubbing helper anywhere in `src/`.** Every `Sanitize` in the
codebase is filename-safety for log paths, not content.

### 6.3 Decision: `detail` is withheld by default; `--on-event-detail` opts in

`faultKind` was *narrowed* rather than shipped-with-a-warning. `detail` gets the same treatment, and the
default is the safe one:

- **Default:** the wire copy's `detail` is the fixed harness-authored string
  `(detail withheld; pass --on-event-detail)`. The field is **present**, so a receiver can never mistake
  "withheld" for "the guardrail had nothing to say" — that ambiguity would be the silent-failure shape
  again, in a single field.
- **`--on-event-detail`:** the file's value passes through, truncated at 2000 characters with a
  `…[truncated]` suffix.
- **`events.jsonl` is never affected.** The file keeps full fidelity in both modes; it is loopback-only
  by the §6.1 posture and is what a post-mortem and the log viewer read.

| Rejected | Why |
|---|---|
| **Send `detail` verbatim by default, with a documented warning** | Inverts the honest default. The operator who most needs protection — someone pasting a Slack webhook URL to try it — is exactly the one who has not read §8.3. |
| **Redact `detail` heuristically** (scan for secret-shaped strings, path prefixes) | A prompt-judge in regex clothing: it certifies nothing, misses the cases that matter, and hands the operator false confidence. Invariant #1's spirit. Path-relativizing alone would leave model prose and echoed secrets untouched. |
| **Drop the `detail` field entirely when withheld** | A receiver could not distinguish it from a passing guardrail. |
| **Cap only, no withholding** | Addresses volume, not disclosure. A 2000-char leak is still a leak. |

**The strongest objection to this, and it is genuinely strong:** *"You have designed a webhook that by
default tells me a guardrail failed but not why — the exact complaint #585's 'Second problem' section
makes."* **Response, and it is the reason this default is defensible:** #585's Second Problem is
answered by **`outcome`**, not by `detail`. Its worked example is `max_turns` (wait — the harness already
escalated the budget) versus a guardrail failure (stop and fix), and those are two distinct members of
the closed set `JournalJson.OutcomeToken` writes — **`max-turns`** and **`guardrail-failed`**, alongside
`timeout`, `output-cap`, `rate-limited`, `permission-denied`, `no-route`, `task-preflight-failed` and
the rest. Every one of them is **always sent**, in both modes. #585's own words are *"`reason` is not
optional… it is the difference between the two responses,"* and #595 settled that `outcome` **is** that
field. `detail` adds the guardrail's own prose on top of an already-actionable row. The default
therefore still answers the question the issue was filed over, and the flag is one word for the operator
who owns both ends.

### 6.4 The configuration surface

**Decision: CLI flag + env var. NOT `guardrails.json`. Exactly ONE endpoint.**

| Surface | Value | Notes |
|---|---|---|
| `--on-event <url>` | the endpoint | **Not repeatable.** Passing it twice is a validation error naming the reason. |
| `GUARDRAILS_ON_EVENT` | the endpoint | Fallback used only when the flag is absent, so a CI job sets it once. Single URL only. |
| `GUARDRAILS_ON_EVENT_AUTH` | the verbatim `Authorization` header value, e.g. `Bearer abc123` | **Env only.** Never a flag, never a file. |
| `--on-event-detail` | off | §6.3 |

**Two interactions worth stating because neither is obvious:**

- **Webhooks are independent of the log server.** `--no-log-server` disables `GET /events` and does not
  touch `--on-event`, so a headless or CI run — the run that most needs to be observed and the one that
  can least easily be attached to — gets the full stream with no listener bound at all. That is the
  configuration this feature exists for.
- **`--dry-run` emits no events and therefore no deliveries.** It exits before the DAG, and §8.1's
  stream begins at the first `task-started`. A consumer must not read "no deliveries" as "no run"; the
  same rule §8.1 already states for an absent `events.jsonl`.

**Why NOT `guardrails.json`, and the reason is a security argument.** Three, in ascending order of force:

1. **Precedent.** The SSOT already says this about a machine concern: *"A MACHINE concern is better set
   via the `GUARDRAILS_WORKTREE_ROOT` env var (§2) than this per-plan key"* (§2, `worktreeRoot`).
2. **The URL is frequently itself the credential.** Slack incoming webhooks and webhook.site both put
   the secret in the path. `guardrails.json` is committed **and hashed into `PlanDefinitionHash`, which
   keys the review attestation** — the exact reason `PromptRunnerConfig.ApiKeyEnv` holds only the *name*
   of an env var (`src/Guardrails.Core/Model/PromptRunnerConfig.cs:124-129`). A URL key would be that
   documented mistake, made once more.
3. **Decisive: `guardrails.json` is a file a model can write.** `plan-breakdown` authors plan folders,
   and a task's write scope can cover the plan folder. A webhook URL readable from a plan file is an
   **agent-writable egress channel** for guardrail `detail` — a prompt-injected task could add an
   endpoint and exfiltrate the run. Keeping the URL on the command line and in the operator's own
   environment means **the classic SSRF threat model does not apply at all**: the URL is supplied by the
   operator, not by content the run processes. That property is worth more than the convenience, and it
   is *created* by this placement decision rather than merely coexisting with it.

**Why exactly ONE endpoint, and this is a "don't build it" answer.** Two endpoints plus one
`GUARDRAILS_ON_EVENT_AUTH` means **endpoint B's token is sent to endpoint A** — a credential leak
created by configuration. Fixing that needs per-endpoint secrets, which needs structured config, which
is exactly what §6.4 just rejected on security grounds. One endpoint keeps the auth story clean and
covers every measured use case (an agent monitor, or a CI wrapper). Multiple endpoints is a follow-up
issue whose blocker is named (§8).

**Why the `GUARDRAILS_` prefix on the secret is load-bearing, not cosmetic.** `ProcessRunner.ApplyEnvironment`
(`src/Guardrails.Core/Execution/ProcessRunner.cs:195-215`, issue #442) **deletes every inherited
`GUARDRAILS_*` variable that is not in the child's declared overlay.** So `GUARDRAILS_ON_EVENT_AUTH`
is stripped from every action, every guardrail script, and every AI-merge worker **for free, by the
existing hermeticity rule** — the webhook secret cannot reach an agent or a guardrail script. A
differently-named variable (`MY_WEBHOOK_TOKEN`) would be inherited by every child. **Name the secret in
the `GUARDRAILS_` namespace precisely because that namespace is hermetic.**

### 6.5 SSRF, redirects, TLS

The URL is operator-supplied from the command line or the operator's own environment — never from a
plan file (§6.4), never from an agent, never from the network. The classic SSRF threat therefore does
not apply. The constraints below exist for the residual cases and for clear failure messages:

| Rule | Decision |
|---|---|
| **Scheme** | `http` or `https` only, checked at startup with `Uri.TryCreate(..., Absolute)` + a scheme test — the same shape as `PlanValidator.IsAbsoluteHttpUrl` (`src/Guardrails.Core/Loading/PlanValidator.cs:474-476`). Anything else is a startup error naming the scheme. |
| **Private / loopback addresses** | **Explicitly ALLOWED, and blocking them would be a bug.** An agent-side monitor on `127.0.0.1` and a sidecar on an RFC1918 address are the *primary* use cases. There is no security benefit to blocking them when the operator supplied the address. |
| **Redirects** | **NOT followed.** `SocketsHttpHandler { AllowAutoRedirect = false }`, and a 3xx is a hard non-retryable failure. A redirect can move the POST — with its `Authorization` header and its payload — to a host the operator never named. .NET strips `Authorization` on a cross-host redirect in current versions, but relying on framework behavior the code does not state is precisely the silent-dependency pattern this repo keeps getting burned by. Note the existing `openai-compat` clients leave `AllowAutoRedirect` at its default `true`; this design does not follow that precedent. |
| **TLS validation** | **Stays on. No `ServerCertificateCustomValidationCallback`, no `--insecure` flag, ever.** A self-signed endpoint is the operator's to fix. Written down because someone will ask. |
| **Plain `http` to a non-loopback host** | A one-line **warning** at startup, not an error: the auth header and the payload would cross the network in the clear. A sidecar on a private network is a legitimate reason to proceed. |
| **Response influence** | None. The body is read to ≤ 8 KB and discarded; no header, status, or body content changes harness behavior beyond the retry classification of §5.1. |
| **`Authorization` value** | Rejected at startup if it contains CR or LF — header-injection defense. Never logged, echoed, journaled, or written to any file. |
| **Resource bounds** | Per-attempt timeout, per-row ceiling, bounded queue, bounded drain, bounded response read — all of §5.2. |

### 6.6 How the URL is displayed

**The console summary, `webhooks.log`, and every warning print the URL as `<scheme>://<host>[:<port>]/…`
— scheme, host, port, and a fixed `/…` when there is any path or query. Never the path. Never the query
string.** A Slack webhook URL printed in full into a redirected `run.log` that an operator later pastes
into a GitHub issue is a live credential leak, and it would be caused by our own success message.

---

## 7. Schema changes — exact `02-schemas-and-contracts.md` edits

### Edit 1 — §8.1's "On every row" table gains `bracket`, and `seq`'s wording is corrected

Replace the `seq` row (currently line 3798) and add one row after it:

```diff
 | `kind` | the event discriminator, kebab-case (table below) |
-| `seq` | a monotonic, 1-based counter within this PROCESS's bracket, assigned under the writer's append lock. **`seq`, not `at`, is the ordering key.** It restarts at 1 for a resume, which appends a fresh bracket to the same file. |
+| `seq` | a monotonic, 1-based counter within this PROCESS's bracket, assigned under the writer's append lock. **`seq`, not `at`, is the ordering key.** It restarts at 1 for a resume, which appends a fresh bracket to the same file — so `seq` is unique only together with `bracket`. |
+| `bracket` | an opaque 8-hex-character id for THIS process's append bracket, generated once per `RunEventStream` and stamped under the same lock. It is what makes `seq` a usable key: a resume reuses the `runId` and restarts `seq` at 1, so `(runId, seq)` collides across brackets while **`(runId, bracket, seq)` identifies a row uniquely and for all time**. Added by #585 layer 3 (§8.3), where the collision stops being a curiosity: a webhook receiver deduplicating on `(runId, seq)` would silently discard an entire resumed run. |
 | `at` | when the row was WRITTEN (ISO-8601 UTC), stamped under the same lock. …
```

### Edit 2 — §8.1's "A runId spans processes" paragraph gains one sentence

Append to that paragraph (currently ending line 3869):

> Each process's rows carry a distinct `bracket` (above), so "which `run-finished` is mine?" is
> answerable by key rather than only by position — which is the form a §8.3 webhook receiver needs,
> since it never sees file order at all.

### Edit 3 — a NEW §8.3, inserted after §8.2 and immediately before `## 9. Prompt runners` (line 3911)

> ### 8.3 Webhook delivery of the event stream (`--on-event <url>`) — issue #585 layer 3
>
> With `--on-event <url>` (or `GUARDRAILS_ON_EVENT`), `guardrails run` **POSTs each §8.1 row to that
> URL as it is written**. It is the same projection, delivered rather than served: one `RunEventStream`
> writes the row once, appends it to `events.jsonl`, and hands the same serialized line to a sink that
> queues it for delivery. There is no second row shape and no second `seq`.
>
> **The run is never affected.** A delivery failure — a timeout, any status, a full queue, a shutdown
> with rows still pending — **cannot change the run's exit code, its verdict, its journal, or its
> timing** beyond a bounded drain at shutdown. `events.jsonl` remains the durable record, and a
> consumer that must be complete re-reads it.
>
> **The request.**
>
> | | |
> |---|---|
> | Method / body | `POST`, `Content-Type: application/json; charset=utf-8`, exactly **one** §8.1 row per request. Never batched. |
> | `User-Agent` | `guardrails/<version>` |
> | `X-Guardrails-Delivery-Id` | `<runId>:<bracket>:<seq>` — **the idempotency key**, pre-assembled so a receiver can deduplicate without parsing the body. Stable across retries of the same row. |
> | `X-Guardrails-Event-Kind` | the row's `kind`, so a receiver can route or ignore without parsing |
> | `X-Guardrails-Delivery-Attempt` | 1-based; a value > 1 means this row was POSTed before |
> | `Authorization` | the verbatim value of `GUARDRAILS_ON_EVENT_AUTH`, when set |
>
> **The body is the `events.jsonl` line, with exactly ONE documented divergence.** `detail` — the only
> free-text field on the row (§8.1: a failing guardrail's reason, or a settled task's summary) — is
> **withheld by default**, carrying the fixed string `(detail withheld; pass --on-event-detail)`. With
> `--on-event-detail` it is the file's value, truncated at 2000 characters with a `…[truncated]`
> suffix. The field is always PRESENT so a receiver can never read "withheld" as "nothing to report".
> Every other field is byte-identical to the file line, and `events.jsonl` itself is never altered by
> either mode.
>
> **Why `detail` is withheld by default.** It is the one field that can carry an absolute path, a
> fragment of source, or model-authored prose: for a script guardrail it is the first line of the
> child process's stdout, uncapped (a compiler error naming a file, an assertion with its stack); for
> a prompt guardrail it is the judge's own text; on `task-settled` it can embed an absolute
> `feedback.md` path or an agent's `needs human:` question verbatim. `faultKind` was narrowed to a
> type name for exactly this reason (§8.1); the same bar applied to the whole row set produces this
> default. The rest of the row is closed token sets, numbers, and author-controlled names.
>
> **What a receiver is promised.**
>
> - Deliveries within one `(runId, bracket)` are **attempted in strictly increasing `seq` order** (one
>   serial pump). A retry delays later rows; it never lets them overtake.
> - **Arrival** order is not guaranteed behind a load balancer. Order by `seq`, never by `at` or by
>   receipt time.
> - **`seq` is not contiguous.** A gap means a row was DROPPED — it is in `events.jsonl` and was never
>   delivered. This is the reconciliation path, and it is the reason delivery is allowed to fail.
> - A `runId` yields **more than one `run-finished`** across a resume or a concurrent process; each
>   bracket has its own.
> - **Any 2xx is success and the response body is ignored** (read to ≤8 KB, discarded). There is no
>   acknowledgment protocol and no reply a receiver can send that changes the run.
>
> **Failure policy.** Retryable: `408`, `429`, `5xx`, connection/DNS/TLS failure, and the per-attempt
> timeout. **Not retryable:** `3xx` (redirects are never followed) and every other `4xx` — a
> byte-identical retry of a rejected request only wastes the budget. Bounds: **4 attempts**, backoff
> **1 s / 2 s / 4 s** with jitter, **10 s** per attempt, and a hard **45 s** ceiling per row. After
> **5 consecutive rows** exhaust every attempt the endpoint is marked failing **for the rest of the
> run**: later rows are dropped on arrival with no HTTP attempted, and one console line says so at the
> moment it happens. The circuit does not re-close. In-memory queue capacity is 1024 rows; a full
> queue drops rather than waits. At shutdown the queue is drained for up to **30 s** before the pump
> is cancelled — the drain deliberately does **not** observe the run's cancellation token, because
> `run-finished` with `exitCode: 3` is precisely the event a cancelled run's supervisor needs. A full
> queue drops its **oldest** entry, never the incoming one, so a stalled pump cannot make the terminal
> row the one that is lost.
>
> **Every drop is recorded, in two places.** A console line prints at the end of every run that used
> `--on-event` — **including when nothing was dropped**, because silence on success is the defect
> §8.1 exists to remove — and `logs/<runId>/webhooks.log` carries one line per drop plus a summary.
> There is deliberately **no `webhook-dropped` event kind**: such a row would itself be queued for
> delivery, so a failing drop-notice would emit another. There is deliberately **no `run.json`
> field**: a consumer computes its own drop set exactly, by diffing the `(bracket, seq)` values it
> received against `events.jsonl`.
>
> **Configuration.** One endpoint per run.
>
> | Surface | Meaning |
> |---|---|
> | `--on-event <url>` | the endpoint. **Not repeatable** — a second occurrence is a startup error. |
> | `GUARDRAILS_ON_EVENT` | same, used only when the flag is absent (§5.1) |
> | `GUARDRAILS_ON_EVENT_AUTH` | the verbatim `Authorization` header value, e.g. `Bearer …`. **Environment only** — never a flag (shell history, `ps`, `/proc/<pid>/cmdline`) and never a file. Rejected at startup if it contains CR or LF. |
> | `--on-event-detail` | include the `detail` field (above). Default off. |
>
> **There is deliberately no `guardrails.json` key for the URL.** Three reasons, the third decisive:
> a machine concern belongs in the environment (§2's own rule for `worktreeRoot`); the URL is
> frequently itself a credential and `guardrails.json` is committed *and hashed into
> `PlanDefinitionHash`* (the reason `apiKeyEnv` holds only a variable NAME, §9); and **`guardrails.json`
> is a file a model can write** — a URL readable from the plan folder would be an agent-writable
> egress channel for the run's own guardrail output. Keeping the endpoint on the command line and in
> the operator's environment is what makes the SSRF question moot: the URL comes from the operator,
> never from content the run processes.
>
> **Security posture — this is the first mechanism in the harness that sends run content off the
> machine.** `GET /events` (§12.2) serves these same rows and is bound to loopback precisely because
> logs may echo secrets. Constraints, all enforced at startup or in the client: the scheme must be
> `http` or `https`; **redirects are never followed** (a 3xx could move the payload and the
> `Authorization` header to a host the operator never named); TLS validation is always on and there is
> no opt-out flag; plain `http` to a non-loopback host prints a warning but proceeds; loopback and
> private addresses are explicitly allowed, because an agent monitor on `127.0.0.1` is the primary use
> case. The auth value is never logged, journaled, or written to any file, and **every message the
> harness prints about webhooks shows the URL as `<scheme>://<host>[:<port>]/…`, never its path or
> query** — for many webhook services the path is the credential, and a full URL in a redirected
> `run.log` is a live leak. Delivery errors are reported as an exception TYPE NAME plus an HTTP status
> code, never `ex.Message`, which routinely contains the whole request URI. The secret's
> `GUARDRAILS_` prefix is load-bearing rather than cosmetic: §5.1's hermeticity rule (#442) strips
> every unlisted `GUARDRAILS_*` variable from every child process, so the webhook credential cannot
> reach an action, a guardrail script, or the AI-merge worker.

### Edit 4 — §5.1's closing "harness-process knobs" paragraph

Replace the sentence at line 1715 (which is already stale — `GUARDRAILS_TELEMETRY` and
`GUARDRAILS_TELEMETRY_CORPUS_ROOT` also exist):

```diff
-Harness-process knobs that the harness reads from its OWN environment rather than passing to a child
-— `GUARDRAILS_WORKTREE_ROOT` (§2) is the only one — are consumed in the parent and are correspondingly
-not visible to a child, since no row above declares them.
+Harness-process knobs that the harness reads from its OWN environment rather than passing to a child
+— `GUARDRAILS_WORKTREE_ROOT` (§2), `GUARDRAILS_TELEMETRY` and `GUARDRAILS_TELEMETRY_CORPUS_ROOT`
+(§15), and `GUARDRAILS_ON_EVENT` / `GUARDRAILS_ON_EVENT_AUTH` (§8.3) — are consumed in the parent and
+are correspondingly **not visible to a child**, since no row above declares them. For
+`GUARDRAILS_ON_EVENT_AUTH` that is a security property, not a side effect: the hermeticity rule above
+is what keeps a webhook credential out of every action, guardrail script and merge worker, and it is
+why the variable is named inside this namespace rather than outside it.
```

### Edit 5 — one sentence appended to §12.2's `GET /events` paragraph

> The same rows can also be **pushed** rather than served: `guardrails run --on-event <url>` POSTs each
> one to an operator-supplied endpoint (§8.3). That is delivery of this same projection, not a second
> stream — and it is the one path on which these rows leave the machine.

---

## 8. What is NOT in layer 3, and where each item lives

| Not built | Home |
|---|---|
| A `ws:` / SSE endpoint | **Superseded** (§2.1). Not a follow-up. #585 closes with layer 3. |
| Batched delivery | **Rejected outright** (§4.1) — the rate does not justify it and it forks the body shape. Not deferred. |
| An HMAC body signature (`X-Guardrails-Signature`) | **New issue.** Real value (body integrity, which the auth header does not give); needs a documented canonicalization + verification recipe. |
| Multiple endpoints | **New issue.** Blocker named: per-endpoint credentials. One `GUARDRAILS_ON_EVENT_AUTH` across two endpoints sends B's token to A. |
| Sender-side kind filtering (`--on-event-kinds …`) | **Rejected**, on #585's own rule: *"A consumer filters on fields, never on a `kind` allowlist… an unrecognized `kind` must remain a visible row."* An allowlist at the **sender** is strictly worse than one at the receiver — a kind added later is silently never delivered, and the receiver cannot tell. Filtering belongs at the receiver, where a missed kind is at least visible. |
| A `webhook-dropped` event kind | **Rejected** (§5.4) — self-referential; a failing drop-notice emits another. |
| A `run.json` webhook section | **Rejected** (§5.4) — the consumer can compute the exact drop set by diffing against `events.jsonl`. |
| A durable outbound spool / redelivery on the next run / a dead-letter replay verb | **Out of scope** — it is the delivery-guarantee product, not the notifier one (see "Ambiguity named"). If it is ever wanted it needs its own crash and resume semantics, and it starts by asking whether `events.jsonl` + a replay tool is not simply the answer. |
| Capping or relativizing `detail` **in `events.jsonl`** | **Out of scope** (§6.3). The file is loopback-only and full fidelity is what a post-mortem needs. |
| Webhooks from `guardrails logs` / `guardrails attach` | **Out of scope.** The dispatcher's lifetime is a run's. A post-mortem viewer replaying a finished file has no delivery semantics worth building. |
| Feeding the telemetry corpus (#570) from the webhook path | **Out of scope** — telemetry reads the journal; a lossy transport is the wrong source for a corpus. |
| Rewiring `guardrails attach` onto `run-finished` | **Already an open follow-up** from the #595 design (its "Proposed plan-document edits" item 4). Unchanged by this design. |

---

## 9. Devil's-advocate self-critique

**Premises re-verified against the tree rather than taken from the issue (#563), and four came back
changing the design:**

| Claim | Verified | Consequence |
|---|---|---|
| The `await using` scope disposes the sink AFTER `RunFinished` | Read out of `RunCommand.cs:499-752` — the real brace nesting and unwind order | **Held.** Now stated with line numbers (§3.3), because its failure is plan 35 §9.3 repeated. |
| `IRunObserver` is genuinely called concurrently, so two `seq` counters can disagree | `IRunObserver.cs:8-9` — *"Implementations MUST be thread-safe — M4 workers emit events concurrently"* | **Held**, and it is the premise the whole central decision rests on (§3.1). |
| The `User-Agent` version can be read in Core | **FALSE.** `GuardrailsVersion` is in `Guardrails.Cli`, which references Core, not the reverse | Design changed: the value is **injected** (§4.3). Reading Core's own assembly would have silently reported `1.0.0`. |
| `GuardrailFailureReason.MaxChars` can be reused | **FALSE.** It is `private const` inside an `internal static class` | Design changed: promote it to `internal` so the 2000 has one owner rather than a copied literal (§5.2). |
| The log viewer would serve `webhooks.log` | **FALSE.** `LogServer` routes are `/`, `/diagram.html`, `/events`, `/tasks/{id}/…` — no arbitrary file under `logs/<runId>/` | Claim removed, and the design now says explicitly that adding such a route is *not* wanted (§5.4). |
| A full queue drops the newest row | True of the first draft's `FullMode` choice — **and it would have made `run-finished` the guaranteed casualty** of a stalled pump | Design changed to `DropOldest` with the `itemDropped` counting overload (§3.2). |


**The strongest counter-argument, and it is SUSTAINED as a real cost: §5.3's circuit drops
`run-finished`.** An endpoint that fails five rows early in a long run has its circuit opened, and the
terminal event — the single row a CI wrapper exists to receive — is never attempted, hours later, when
the endpoint may well have recovered. I rejected the fix (always attempt the terminal row once, even
when open) because it makes the dispatcher read `kind`, and §4.1's whole value is that the body is
opaque to it.

**That reasoning is defensible but it is not obviously right, and I would not defend it hard.** The
counter-counter is short: "the dispatcher already reads `kind` to set `X-Guardrails-Event-Kind`" — which
is true, and it collapses my layering objection to a preference. What survives is the empirical claim: a
circuit opens only after 20 consecutive failed POSTs, and an endpoint in that state is unlikely to
answer the 21st. **If review disagrees, the change is ~10 lines and it cuts cleanly in**: on the final
drain, attempt the last queued row once regardless of circuit state. I have flagged it in the summary as
the first thing to push back on.

**Second: the `bracket` field is scope creep into a contract that shipped last week.** Layer 3 was told
to build on the settled vocabulary, not re-decide it, and this adds a field to every row on every layer.
**Response:** it is not a re-decision, it is additive and changes no existing field's meaning, and it is
the smallest possible fix for a **silent** data-loss bug that layer 3 itself creates (§4.2). The
alternative — a delivery-only header — leaves the file reader with the same collision while the
contract tells it to re-read the file. And invariant #4 requires the contract change to land with the
change that motivates it; the `faultKind` narrowing set exactly this precedent one plan ago. The cost is
one field, one line in `AppendLine`, and updating the row-shape tests that assert exact JSON.

**Third: withholding `detail` by default guts the payload.** Answered at length in §6.3 — `outcome`, not
`detail`, is what #585's Second Problem actually turns on, and it is always sent. **This is the second
thing I would expect pushback on**, and it also cuts cleanly: flipping the default is a one-word change
to the flag (`--on-event-no-detail`). I would rather be argued out of a safe default than into an unsafe
one.

**Fourth: `IEventSink` is an interface with exactly one implementation — speculative abstraction.**
Partly true. **Response:** it earns its keep on two grounds that are not speculative. It keeps
`HttpClient`, retry policy and a background pump out of `RunEventStream`, whose job is to write a line.
And it is the **only** way a test can assert the wire body and the file line are the same bytes without
standing up an HTTP server — which is the one assertion that structurally prevents the vocabulary fork
this whole design is organized around. If it were a three-member interface I would drop it; at two
members, one of which is a bool, it is a seam and not a framework.

**Fifth: serializing every row twice, and doing it inside a lock.** Real. **Response:** §8.1 declares
this stream low-frequency by contract; the second serialization is of a record that is `Equals`-equal to
the first for every kind that has no `detail`, and the enqueue it feeds is a lock-free `TryWrite`. The
alternative — enqueue outside the lock — trades a measured nothing for out-of-order delivery under
parallel workers.

**Sixth: this is the first feature that sends run content off the box, and no amount of §6 makes that
not true.** Accepted, and it is the honest headline rather than a caveat. The mitigations are: the
feature is entirely opt-in (no flag, no traffic), the default payload is structured fields only, the
free text needs a second explicit flag, and the credential lives in a namespace the harness already
strips from every child process. What remains exposed by design is the *shape* of the run — task names,
guardrail names, outcomes, costs, models. That is the feature.

**Seventh, an accepted limitation rather than an objection: `model` is provider-echoed and
unvalidated.** A hostile `openai-compat` endpoint could put arbitrary text in it. Not fixed here,
because the same string already reaches `run.json` and the telemetry corpus, so it is not a new
exposure — and an operator pointed at a hostile inference endpoint has a much larger problem than a
webhook field. Worth a separate issue if anyone wants a cap on it.

**Eighth: the concurrent-run hazard is inherited, not fixed.** Two `guardrails run` invocations on one
plan folder resolve the same `runId` and both append to one `events.jsonl` (§8.1's "per process" hedge).
Layer 3 makes it *more* visible, not worse: with `bracket`, a receiver can at least tell the two apart —
which is more than a file reader can do today. A plan-folder lock remains its own design with its own
resume and crash semantics.

---

## 10. Implementation handoff

One task per row; every `filesTouched` cell is backticked and segment-resolvable against the real tree,
and each row is deliverable by a single task's `writeScope` (#553 / `GR2068` `HandoffPathUnreachable`,
`GR2069` `HandoffRowSplitAcrossTasks`).

| # | Agent | filesTouched | Deliverable |
|---|---|---|---|
| 1 | `guardrails-architect` | `docs/plans/02-schemas-and-contracts.md` | §7 Edits 1–5 verbatim. Lands **with** task 3, not after (invariant #4). |
| 2 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/IEventSink.cs` | The new seam: `bool IncludeDetail { get; }` and `void Emit(string jsonLine)`, with the "called inside the append lock; must not block; must not throw" XML doc of §3.1 verbatim. |
| 3 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/RunEventStream.cs`, `src/Guardrails.Core/Execution/GuardrailFailureReason.cs` | `bracket` on `EventRow` + generated in the ctor + stamped in `AppendLine`; the `IEventSink? sink = null` ctor parameter (**defaulted here on purpose** — "no sink" is the correct behavior for a run without `--on-event` and for the 20-odd existing test constructions, which then compile unchanged; contrast task 7, where `BuildObserverChain`'s parameter is deliberately NOT defaulted); the wire-copy block of §3.1 including `CapDetail` and the `DetailWithheld` constant. Promote `GuardrailFailureReason.MaxChars` to `internal const`. Update the class doc's row-shape paragraph. |
| 4 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/RunEvents/` | Written **RED** against tasks 2–3: `bracket` present, stable within a process, distinct across two `RunEventStream` instances; **the wire line equals the file line byte-for-byte for every kind with no `detail`**; withheld-marker and cap behavior; `seq`/`bracket` under concurrent writers; a sink that throws does not propagate into `AppendLine`. |
| 5 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/WebhookEventSink.cs` | The dispatcher: bounded channel, one pump, `internal static bool IsRetryable(...)` as a pure function, the §5.2 bounds as named constants, the §5.3 circuit, `webhooks.log`, the `onNotice` summary, and the **drain-before-cancel** `DisposeAsync` of §3.3 with a comment citing plan 35 §9.3. `SocketsHttpHandler { AllowAutoRedirect = false }`. Errors reported as type name + status only. |
| 6 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/Webhooks/` | `IsRetryable` truth table (every row of §5.1); backoff schedule and the 45 s ceiling; circuit opens at exactly 5 and never closes; **a full queue drops the OLDEST and the newest row still gets through** (the `run-finished` property, §3.2) and every drop is counted; `DisposeAsync` drains before cancelling and the summary fires on the fault path; **a negative assertion that neither the auth value nor the URL path appears in `webhooks.log` or the notice text** — construct the sink with a secret-shaped token and a secret-shaped URL path and assert neither string appears in either output, the same shape as the existing `faultKind`-carries-no-message test. |
| 7 | `guardrails-harness-developer` | `src/Guardrails.Cli/Commands/RunCommand.cs` | `--on-event` / `--on-event-detail` options + `GUARDRAILS_ON_EVENT` / `_AUTH` fallbacks. **Validation runs EARLY, beside the other option parsing, and a bad value exits `ExitCodes.HarnessError` (1) before any run state is touched** — the same posture as an unparseable `--autonomy`; an invalid URL must not surface mid-run, and `TryStart` must therefore never throw. Validate: scheme is `http`/`https`; the flag occurs at most once (declare it so a second occurrence is *detected*, not silently last-wins); no CR/LF in the auth value; warn on plain `http` to a non-loopback host. Add the redacted URL renderer of §6.6. Place `WebhookEventSink.TryStart` at `:506` with `await using` per §3.3. Thread the new `IEventSink?` parameter through `BuildObserverChain` (`:2382`) to both call sites (`:537`, `:542`). **No default value on THAT parameter** — a defaulted one lets a production call site silently deliver nothing, which is the plan-34 §3 swallow hazard; contrast task 3, where defaulting the `RunEventStream` ctor parameter is correct. |
| 8 | `guardrails-test-author` | `tests/Guardrails.Integration.Tests/RunEvents/` | The composition-root and end-to-end proofs, and this is the row that matters most (#382). A real run against a real loopback `HttpListener`: rows arrive, **`run-finished` arrives** (the plan-35 §9.3 assertion that did not exist), bodies match `events.jsonl` line-for-line, headers are exactly §4.3, `detail` is withheld without the flag and present with it, a receiver returning 500 causes retries and then a recorded drop with the exit code unchanged, and a receiver that never binds leaves the run's exit code and timing untouched. |
| 9 | `guardrails-skill-author` | `.claude/skills/guardrails-domain-knowledge/SKILL.md` | The contract quick-reference gains `--on-event`: the delivery key `(runId, bracket, seq)`, "a failed delivery never affects the run", and "`detail` is withheld unless `--on-event-detail`". |
| 10 | `guardrails-architect` | `docs/plans/585-layer3-webhooks-contract.md` | Fold the draft-PR review outcome back into this document, and record the `ws:` closure so #585 can be closed with the implementation. |

**Sequencing.** 1 ∥ 2 → 4 (RED) → 3 → 5 → 6 → 7 → 8 → 9 → 10. Tasks 1 and 2 are independent and may run
in parallel. Task 4 is authored against tasks 2–3's shape and must fail before task 3 lands. Task 8 is
the gate: nothing here is proven by unit tests alone, because the defect class this feature is exposed
to — a correctly-implemented projection swallowed by the composition root — is invisible to them.

---

## 11. Proposed plan-document edits

1. **`docs/plans/02-schemas-and-contracts.md`** — §7 Edits 1–5, verbatim (task 1).
2. **`docs/plans/595-event-vocabulary-contract.md`** — append a short note under "What #585 layer 3
   depends on from this design": item 2's key is now `(runId, bracket, seq)`, and item 3's claim that
   `faultKind` is *"the one value on the row that can carry an absolute path, a token, or a fragment of
   source"* is **superseded by §6.2** — `detail` is a strictly larger exposure on two of the six kinds.
   Worth recording as a pattern rather than a correction: the field that was audited got narrowed, and
   the field that arrived in the same release was never audited against the same bar.
3. **`docs/plans/03-roadmap.md`** — no change. Layer 3 is #585's own remaining scope, already carried
   there; nothing here is a v2 bet.
4. **New issue: HMAC body signing for `--on-event`** (§8).
5. **New issue: multiple `--on-event` endpoints**, with the per-endpoint-credential blocker stated so
   the next proposal starts from it (§8).
6. **#585 itself:** on merge, close it — layer 3 completes the three layers, and §2.1 closes the `ws:`
   question on the record rather than leaving it open behind the issue.
