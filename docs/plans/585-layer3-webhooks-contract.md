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
| The `onRow` callback parameter + `EventDelivery` + the wire-copy block in `RunEventStream` | **harness** — `Guardrails.Core.Execution` |
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

**Decision: `RunEventStream` gains an optional `onRow` callback, and invokes it with the already-stamped,
already-serialized line from inside its append lock. The webhook dispatcher supplies that callback.**

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

**The seam is a callback parameter, NOT an interface** — corrected by the adversarial pass, which was right on
two counts. A one-implementation `IEventSink` is speculative abstraction, and putting `IncludeDetail` on it
inverted the ownership: the *writer* owns the row shape, so it must own what the wire copy looks like rather
than asking the sink for the sink's policy. Both ctor parameters go on `RunEventStream`, which is where the
row already lives:

```csharp
// src/Guardrails.Core/Execution/RunEventStream.cs
/// <param name="onRow">
/// OPTIONAL second destination for each row (#585 layer 3): the delivery the webhook dispatcher queues.
/// Invoked on the RUN's own thread, INSIDE the append lock, so it MUST return in microseconds and MUST
/// NOT throw — a throw here would propagate into a Scheduler worker while holding <c>_gate</c>, and a
/// delivery mechanism is never permitted to affect the run (§8.3). Enqueue and return; a full queue is a
/// recorded DROP, never a wait. Null = no webhook endpoint, and the byte-identical behavior of today.
/// </param>
/// <param name="includeDetail">
/// Whether <paramref name="onRow"/>'s copy carries the free-text <c>detail</c> field (§8.3). The
/// events.jsonl row is NEVER affected either way.
/// </param>
public RunEventStream(
    IRunObserver inner, string directory, string runId,
    Action<EventDelivery>? onRow = null, bool includeDetail = false)

/// <summary>
/// One row on its way OFF the machine (§8.3). Carries the three values the delivery's headers need
/// alongside the body, so the dispatcher never re-parses the JSON it was just handed — which would be a
/// third serialization round-trip per row and a failure mode nobody has specified.
/// </summary>
public readonly record struct EventDelivery(string DeliveryId, string Kind, string JsonLine);
```

And the change inside `RunEventStream.AppendLine` — the whole of it:

```csharp
lock (_gate)
{
    EventRow stamped = row with { Seq = ++_seq, At = DateTimeOffset.UtcNow, Bracket = _bracket };
    string line = JsonSerializer.Serialize(stamped, LineOptions);

    Directory.CreateDirectory(_directory);
    File.AppendAllText(Path.Combine(_directory, "events.jsonl"), line + "\n", Utf8NoBom);

    if (_onRow is not null)
    {
        // The wire copy differs from the file line in EXACTLY ONE field, and only ever `detail` (§8.3) —
        // and only when the row HAS one. A null stays null, so a task-started / attempt-started /
        // run-finished / PASSING guardrail-finished row serializes to a byte-identical string, and a
        // receiver never sees a withheld marker where there was nothing to withhold.
        EventRow wire = stamped.Detail is null
            ? stamped
            : stamped with { Detail = includeDetail ? CapDetail(stamped.Detail) : DetailWithheld };

        string wireLine = ReferenceEquals(wire, stamped) ? line : JsonSerializer.Serialize(wire, LineOptions);

        // The contract above says the callback must not throw. A public delegate parameter cannot
        // ENFORCE that — a test double or a future second consumer can throw — and a throw escaping here
        // holds `_gate` inside a Scheduler worker. Belt as well as braces.
        try { _onRow(new EventDelivery($"{_runId}:{_bracket}:{stamped.Seq}", stamped.Kind, wireLine)); }
        catch (Exception) { /* a delivery mechanism may never affect the run (§8.3) */ }
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

- A **bounded `Channel<EventDelivery>`**, capacity **1024**, **`FullMode = DropOldest`**, created with the
  `Channel.CreateBounded<T>(BoundedChannelOptions, Action<T> itemDropped)` overload so every displaced
  row is counted rather than vanishing. `Emit` calls `TryWrite`, which never blocks.

  **`DropOldest`, not `DropWrite` or `Wait`, and the reason is `run-finished`.** With any newest-loses
  policy, a stalled pump means the queue is full exactly when the terminal row arrives — so the single
  row a CI wrapper exists to receive is the one guaranteed to be dropped. `DropOldest` inverts that:
  the newest row always gets in, and what is lost is the stalest event, which is also what a
  late-attaching supervisor cares least about. Ordering is unaffected (dropping from the head leaves
  the tail in order); a gap is already the documented meaning of a drop (§4.4).

- **`Emit` also stores the delivery in a `_lastEnqueued` field** (a plain volatile write, under the same
  append lock, so it is the true tail). This is what §3.3's guaranteed terminal attempt reaches for, and
  it is why the dispatcher needs **no** knowledge of the event vocabulary to protect the terminal row:
  `run-finished` is by construction the last row a process emits, so "the last enqueued row" *is* it on
  every normal path, with no `kind` comparison anywhere.
- **The `itemDropped` callback runs on the RUN's thread, inside the append lock**, so it may do exactly
  two things: `Interlocked.Increment` a counter and record the delivery id in a field. **No file IO and
  no console write ever happens on the run's thread** — that would be the one way this design could
  measurably slow a run, and §5.4 is why there is no per-drop file to write at all.
- The whole of `Emit` is inside a `catch (Exception)` that increments the drop counter — belt as well as
  the braces `AppendLine` already puts around the callback (§3.1).
- **One** background pump task started in the constructor, reading `ReadAllAsync`. One pump, not a fan-out:
  serial delivery is what keeps arrival in `seq` order, and a retrying row delays later rows rather than
  being overtaken by them. **Its `Task` is retained and awaited** (§3.3) — an unobserved pump that faults
  is the silent disappearance §2.2 mocks the shell shim for, so a faulted pump is reported in the summary.
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
| **A dedicated `IEventSink` interface** (the first draft's shape) | Speculative abstraction: one implementation, and both stated justifications fail. "Keeps `HttpClient` out of `RunEventStream`" is equally true of a delegate; "the only way a test can prove wire/file byte-identity without a server" is false — a lambda collecting deliveries does it. And putting `IncludeDetail` on the *sink* inverted ownership: `RunEventStream` owns the row shape, so it must own what the wire copy looks like. **Cut on the adversarial pass; it deleted a file and a handoff task.** |
| The dispatcher **re-parsing** the JSON line to build its headers | A third JSON operation per row, and a parse failure with no specified behavior, to recover values the writer had in hand. `EventDelivery` carries them alongside the body instead. |

### 3.3 Lifetime and teardown — the `LogServer` lesson, applied

The single most valuable delivery in the whole feature is the terminal `run-finished` row, because it
carries the `exitCode` a CI wrapper branches on. It is appended in the `finally` at the very end of
`RunCommand.RunAsync`. **If the dispatcher is torn down before or during that, the payoff is lost — and
lost silently.**

Plan 35 §9.3 is the recorded cost of getting exactly this wrong one surface over: `LogServer.DisposeAsync`
drained in-flight requests **three lines too late**, after `_listener.Stop()` had already torn down the
shared HTTP.sys request queue, so the "best-effort" final delivery of `run-finished` failed *every single
time* across ~10 measured variants. The finding, verbatim: **"A 'best-effort' mechanism that is 0%
effective is not best-effort; it is dead code."** The fix was to move the drain above the **transport**
teardown, and `LogServer` now additionally defers the listener stop behind a 250 ms linger
(`src/Guardrails.Cli/Ui/LogServer.cs:1147-1201`).

**The lesson stated correctly, because the first draft inverted it and the inversion caused two of this
document's blockers.** `LogServer.DisposeAsync` **cancels first** (`LogServer.cs:1149`) and always did;
what moved was the drain, above `_listener.Stop()`. So the rule is not "cancel last" — it is:

> **Signal wind-down first. Drain second. Tear the transport down last.**

The first draft's "keep the full retry budget through the drain and cancel at the end" is what loses the
terminal row, and it is exactly how blocker 3 below arises. Layer 3's transport is the `HttpClient`, so
that is what must be disposed last, after the pump has provably returned.

Layer 3 takes the corrected lesson as a construction rule, not a hope:

1. **The sink is constructed BEFORE the observer chain and disposed AFTER the `RunFinished` bracket.**
   Concretely, in `RunCommand.RunAsync`, on the line after `diagramSeed` is read (`:505`) and before
   the `OnTheFlyDiagramObserver? diagramObserver = null;` bracket opens (`:518`):

   ```csharp
   await using var eventSink = WebhookEventSink.TryStart(   // null when no --on-event URL
       onEventUrl, onEventAuth, userAgent, io.Out.WriteLine, cancellationToken);
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

2. **`DisposeAsync` runs six steps in this order, and every one of them is load-bearing.**

   | # | Step | Why it is where it is |
   |---|---|---|
   | 1 | Complete the channel writer and set `_draining` | Signals wind-down. From here the pump makes **one attempt per row** — it abandons the retry budget entirely (see step 2). |
   | 2 | **Backlog phase**: the pump keeps delivering in `seq` order, one attempt each, until the queue empties or `BacklogDrainBudget` expires (**10 s**, and **0 s when the run was cancelled**). Anything left is counted as a drop. | Retrying during teardown is what starves the terminal row (blocker 3). |
   | 3 | **Terminal phase, which ALWAYS runs**: if `_lastEnqueued` was not delivered, make exactly **one** attempt at it, bounded by `TerminalDeliveryTimeout` (**10 s**, **500 ms** when cancelled), **ignoring the circuit and ignoring the backlog**. | This is the guarantee the whole feature exists for, and it is why §5.3's circuit no longer costs `run-finished`. |
   | 4 | Cancel the pump's token, then **`await` the pump, bounded** | `LogServer.cs:102-105` documents the trap verbatim: *"disposing that CancellationTokenSource while a wait is outstanding on it is undefined behaviour, so nothing may touch it until every dispatched request has returned."* A cancelled token does not mean `SendAsync` has returned. |
   | 5 | Dispose the `HttpClient`, then the `CancellationTokenSource` | The transport goes **last**, after the pump has provably returned — the corrected §9.3 rule. |
   | 6 | Emit the buffered notices + the summary through `onNotice` (§5.4) | Last, so the counts are final and no console write races the live table (§5.3). |

   Worst-case teardown cost is therefore **20 s** on a normal exit and **~500 ms** on a cancelled one —
   both stated as numbers rather than left to be multiplied out.

3. **`DisposeAsync` MUST NOT THROW — the contract covers teardown as strictly as it covers `Emit`.**
   This is where the first draft violated its own ruling. `await using` puts the dispose in a
   compiler-emitted `finally` spanning to `RunCommand.cs:747`, so **an exception thrown there replaces
   the in-flight `return exitCode;` from `:714` — turning a wholly-green run into an unhandled
   exception** — and on the fault path replaces the original exception, destroying the diagnosis. The
   repo already knows this shape: `RunCommand.cs:676` wraps a journal write in
   `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)` for
   exactly this reason. So the whole body of `DisposeAsync` sits inside a `catch (Exception)`, and
   §8.3 states it. **Cutting the per-drop log file (§5.4) removes the largest source of a throw here
   outright**, which is most of why it was cut.

4. **The Ctrl-C contract, decided rather than assumed.** `src/Guardrails.Cli/Program.cs` is six lines and
   passes **no `InvocationConfiguration`**, so System.CommandLine 2.0.9's default applies —
   `~/.nuget/packages/system.commandline/2.0.9/lib/net8.0/System.CommandLine.xml:774-779`: *"If not
   provided, a default timeout of 2 seconds is enforced."* **The entire Ctrl-C unwind — Scheduler
   cancellation, journal writes, `RunFinished`, this drain, and `logServer.DisposeAsync()` — must fit in
   two seconds**, so the first draft's 30 s drain was unreachable, and worse, it would have eaten the
   budget that layer 2's own drain (`ShutdownDrainTimeout`, 5 s) paid ~10 measured variants to win.

   **Decision: the cancelled path skips the backlog entirely and spends ~500 ms on one attempt at the
   terminal row.** Loopback RTT is routinely sub-millisecond, so that is a real chance rather than a
   gesture, and it leaves the remaining budget to the log server. **§8.3 says plainly that on Ctrl-C the
   terminal row's delivery is a single best-effort attempt, and the file is the record.** The run's
   cancellation token is passed to the dispatcher **only to select the budget** — the drain itself never
   observes it, because a token that is already cancelled would otherwise skip the drain entirely.

   **Explicitly NOT taken:** raising `ProcessTerminationTimeout`. It is a cross-cutting change to every
   command's Ctrl-C behavior, it is a maintainer UX call rather than layer 3's to make, and layer 2's
   drain already exceeds the 2 s budget today. **Filed as a follow-up** (§11) — the observation belongs
   to the CLI host, not to webhooks.

5. **A test pins the ordering, not a comment.** An integration test runs a real plan against a real
   loopback `HttpListener` and asserts a `run-finished` body arrives — and a second asserts it still
   arrives when the endpoint is slow enough to have backed the pump up. Per plan 35's own measurement,
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

**`bracket`** is `<unix-ms>-<4 hex>` — e.g. `1756948327104-a3f9` — generated once in `RunEventStream`'s
constructor and stamped on every row inside the append lock, exactly like `seq` and `at`. Zero call-site
changes: one field on `EventRow`, one line in `AppendLine`, one line in the constructor.

**Why not 8 random hex characters, which was the first draft's answer.** A pure random id gives identity
but no **ordering**, and a receiver holding rows from two brackets could not tell which process was later
— so §8.1's "take the LAST `run-finished` as current" would have had no wire analog at all, while §4.2
claimed answering that question as a *benefit* of the field. A millisecond prefix closes the gap for
free; the random suffix keeps two processes starting in the same millisecond distinct. It is an opaque
token for equality and a sortable one when a receiver needs to order brackets — and it is deliberately
NOT a timestamp anyone should compute elapsed time from, for the same reason `at` is not (§8.1).

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

Three custom headers, each earning its keep, plus the two standard ones. **Two of the three values come
in on the `EventDelivery` (§3.1) rather than being recovered from the body** — the dispatcher never parses
the JSON it was handed, which is what keeps §4.1's "the body is opaque to the dispatcher" true rather than
aspirational:

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
| **Backlog drain budget** | **10 s** normally, **0 s** when the run was cancelled | §3.3 step 2. Retries are abandoned for the whole drain — one attempt per row |
| **Terminal delivery timeout** | **10 s** normally, **500 ms** when the run was cancelled | §3.3 step 3. Always spent, circuit or no circuit, backlog or no backlog |
| Worst-case teardown | **20 s** normally, **~500 ms** cancelled | the sum of the two above, stated so nobody has to add them up |
| `detail` cap when included | **2000 chars** — `GuardrailFailureReason.MaxChars` (`src/Guardrails.Core/Execution/GuardrailFailureReason.cs:25`), **promoted from `private const` to `internal const`** so there is ONE owner of the number rather than a copy that drifts | an existing in-repo precedent for capping exactly this kind of text, and it stops one runaway compiler line becoming a multi-megabyte POST |
| Response body read | **≤ 8 KB, discarded** | releases the connection without buffering a hostile response |

### 5.3 The circuit: give up loudly, once

**After 5 CONSECUTIVE rows exhaust all their attempts, the endpoint is marked failing for the rest of
the run.** Subsequent rows are dropped on arrival — counted, no HTTP attempted. **It never closes.** No
half-open probe, no timer. Rationale: 5 rows × 4 attempts = 20 consecutive failed POSTs is not a
transient, a half-open state machine is real complexity for a case the durable file already covers, and
"when does it re-open?" is a question with no good answer that anyone would have to keep answering.

**The circuit does NOT suppress the terminal delivery.** §3.3 step 3 always spends one attempt on the
last-enqueued row, whatever the circuit says. So the cost the first draft agonized over — an open circuit
dropping `run-finished` — **does not exist any more**, and it is not the mechanism that needed fixing.

**The notice is BUFFERED, not printed when the circuit opens — and that correction is #145 Bug 1.**
The first draft had the pump write one console line at the moment the circuit opened, mid-run. That is a
console write from a background thread while the Spectre `Live` region is active: `await using var
liveObserver` is constructed at `RunCommand.cs:534` and disposed at the end of the `if (live)` block at
`:539`, so the region covers the entire DAG — exactly when a circuit opens. The constraint is stated in
the code three lines above the construction (`RunCommand.cs:526-528`): *"any console write into an active
Live region corrupts the table (#145 Bug 1)."* So the notice is buffered and flushed with the end-of-run
summary at `:747`, where the region is long gone.

```
Webhook: gave up at 14:22:07 after 5 consecutive delivery failures (last: HttpRequestException).
```

**What buffering costs, honestly.** The circuit's third justification in the first draft was an EARLY
warning, and buffering removes it: the operator now learns at the end, which is when the summary tells
them anyway. Two justifications survive and they are enough for ~15 lines — it bounds wasted work on a
dead endpoint (no 800 pointless POSTs), and it makes the backlog phase of teardown instant. **Surfacing
a live delivery-health indicator inside the table is a `guardrails-ux` question, not a webhook one**, and
is filed rather than smuggled in here (§11).

### 5.4 Where a drop is RECORDED

Ruling 2 is *"never affect the run"*, not *"never tell anyone."* **ONE surface**, and the first draft's
second one was cut on the adversarial pass:

**One console line at the end of every run that used `--on-event`, INCLUDING at zero drops.** Emitted by
`WebhookEventSink.DisposeAsync` through the `onNotice` callback, so it fires on the normal path, the halt
path, and the unwinding-fault path alike, after the final counts are known and after the live region is
gone:

```
Webhook: 211 delivered, 0 dropped -> https://hooks.example.com/…
Webhook: 197 delivered, 14 dropped -> https://hooks.example.com/…
Webhook: gave up at 14:22:07 after 5 consecutive delivery failures (last: HttpRequestException).
Webhook: delivery stopped early (TaskCanceledException); 6 row(s) never attempted.
```

The zero-drop line is not noise: **silence on success is the exact defect this issue is about.** A line
that always prints is proof the mechanism ran at all. The fourth form matters for the same reason — the
pump's `Task` is retained and its fault is **observed**, because `Task.WhenAny(pump, delay)` does not
throw on a faulted pump, and a summary reading "0 dropped" while rows sit in a dead channel would be the
silent disappearance §2.2 mocks the shell shim for.

**CUT: `logs/<runId>/webhooks.log`.** The first draft had a per-drop file beside `events.jsonl`. It does
not earn its keep, and three of the four things said in its favor were wrong:

| Claim made for it | What is actually true |
|---|---|
| "the log viewer already serves it" | **False.** `LogServer.cs:399`/`:413` say the opposite in the code — the routes are an explicit allowlist, *"rather than a wildcard static file server over `_logsRoot`"*, and tests pin that nothing else under `logs/<runId>/` is served. Making the claim true means adding a route, which is unscoped work on a listener whose class comment is about *not* exposing things. |
| "a durable record of which rows were dropped" | Undercut by this very section's own better argument: a consumer computes the **exact** drop set by diffing the `(bracket, seq)` values it received against `events.jsonl`. The file would be a second, weaker owner of a fact the durable record already holds — the objection this design raises against a `run.json` field. |
| "the console summary is ephemeral" | It is captured by `> run.log`, which is how an unattended run is already read (#552). |
| — | And it was **the largest source of a throw inside `DisposeAsync`** (§3.3 step 3): a file open in an editor, a full disk, or `MAX_PATH` on a deep plan folder would have turned a green run into an unhandled exception. |

Cutting it removes a file format, a deliverable, and a way to fail the run. The counts stay; the detail
was always computable.

| Rejected | Why |
|---|---|
| **A `webhook-dropped` event kind in `events.jsonl`** | Seductive and wrong. The row would itself be enqueued for delivery, so a drop-notice that fails emits a drop-notice: a loop, or a special case carved into the one path that must stay simple. Either way it makes the dispatcher self-referential, and a later refactor would re-introduce the loop. |
| **A `webhooks` section in `run.json`** | A journal schema change (§7) for a fact the consumer can compute *exactly* on its own: diff the `(bracket, seq)` set it received against `events.jsonl`. That diff is more accurate than any summary the harness could write, so the journal field would be a second, weaker owner of a fact the durable record already holds. |
| **Silence on success** | See above. |
| **Failing the run, or a nonzero exit code, on drops** | Ruling 2. |

**And a rule that is easy to get wrong:** every notice and the console summary print **the exception's
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
| **`detail` on `guardrail-finished`** | `GuardrailResult.Reason` — for a script guardrail, the **first non-empty line of the child's stdout, uncapped** (`GuardrailRunner.cs:384-404`); for a prompt guardrail, the judge's free prose read verbatim from the verdict file (`:283-293`) | **UNSAFE.** Routinely carries absolute paths (`C:\...\Foo.cs(42,13): error CS0103: …`) and source fragments — a compiler error quotes the offending expression, an assertion quotes expected and actual. A guardrail script can echo anything, including an env var. **Citation corrected on the adversarial pass:** the first draft cited #179 (test guardrails re-emitting assertions and stack traces) as evidence. That output lands in `GuardrailResult.Output`, which feeds retry feedback and never reaches this row — only `FirstNonEmptyLine(StandardOutput)` does. The conclusion is unchanged and the wrong support is removed. |
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
field. `detail` adds the guardrail's own prose on top of an already-actionable row — and `guardrail`
(the failing guardrail's name) and `passed` are always sent too, so a withheld-detail receiver knows
*which* guardrail failed, not merely that one did. The default therefore still answers the question the
issue was filed over, and the flag is one word for the operator who owns both ends.

**Where that defense is INCOMPLETE, and the adversarial pass is right about it.** `outcome` answers the
max-turns-versus-guardrail-failure case. It does **not** carry a `needs-human` **question**:
`AttemptJournaler.cs:543` splices `$"needs human: {question}"` into `TaskResult.Summary`, which becomes
`task-settled`'s `detail`. With detail withheld, a supervisor gets `outcome: needs-human` plus
`needsHumanKind: blocked-work` and must **read a file to learn what was actually asked** — the obligation
#585 exists to delete, on the path #361's resume-time answer injection depends on.

That is not a reason to flip the default; it is a reason the **question needs a carrier that is not
`detail`.** `needsHumanKind` is a closed token and safe; the question is free text authored by a model
and is not. A dedicated field with its own disclosure rule is the right answer and it is **not this
change** — filed as a follow-up (§11), because inventing one here would be exactly the vocabulary fork
this design spends §4.1 refusing.

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

**The console summary and every notice and warning print the URL as `<scheme>://<host>[:<port>]/…` —
scheme, host, port, and a fixed `/…` when there is any path or query. Never the path. Never the query
string.** A Slack webhook URL printed in full into a redirected `run.log` that an operator later pastes
into a GitHub issue is a live credential leak, and it would be caused by our own success message.

**The renderer lives in Core, with the dispatcher — not in the CLI.** Both the startup plain-`http`
warning (CLI) and every runtime notice (Core) need it, and the negative test that asserts no URL path
ever reaches a notice runs against Core. Splitting it across the two assemblies would put one row of
§10's handoff table across two tasks with the assertion stranded between them — `GR2069`
`HandoffRowSplitAcrossTasks`, caught on the adversarial pass and fixed by giving it one owner.

---

## 7. Schema changes — exact `02-schemas-and-contracts.md` edits

### Edit 1 — §8.1's "On every row" table gains `bracket`, and `seq`'s wording is corrected

Replace the `seq` row (currently line 3798) and add one row after it:

```diff
 | `kind` | the event discriminator, kebab-case (table below) |
-| `seq` | a monotonic, 1-based counter within this PROCESS's bracket, assigned under the writer's append lock. **`seq`, not `at`, is the ordering key.** It restarts at 1 for a resume, which appends a fresh bracket to the same file. |
+| `seq` | a monotonic, 1-based counter within this PROCESS's bracket, assigned under the writer's append lock. **`seq`, not `at`, is the ordering key.** It restarts at 1 for a resume, which appends a fresh bracket to the same file — so `seq` is unique only together with `bracket`. |
+| `bracket` | an id for THIS process's append bracket — `<unix-ms>-<4 hex>`, e.g. `1756948327104-a3f9` — generated once per `RunEventStream` and stamped under the same lock. It is what makes `seq` a usable key: a resume reuses the `runId` and restarts `seq` at 1, so `(runId, seq)` collides across brackets while **`(runId, bracket, seq)` identifies a row uniquely and for all time**. Treat it as OPAQUE for equality; its millisecond prefix additionally lets a reader order two brackets, which is the only way a consumer that never sees file order (§8.3) can apply the "take the LAST `run-finished`" rule below. It is NOT a clock to compute elapsed time from, for the same reason `at` is not. Added by #585 layer 3 (§8.3), where the collision stops being a curiosity: a webhook receiver deduplicating on `(runId, seq)` would silently discard an entire resumed run. |
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
> run**: later rows are dropped on arrival with no HTTP attempted. The circuit does not re-close.
> In-memory queue capacity is 1024 rows; a full queue drops its **oldest** entry, never the incoming
> one, so a stalled pump cannot make the terminal row the one that is lost.
>
> **Shutdown, and what the terminal row is actually promised.** At teardown the harness stops retrying
> altogether — one attempt per row — drains the backlog for up to **10 s**, and then, **always and
> regardless of the circuit or the backlog, spends one further attempt (up to 10 s) on the LAST row
> enqueued**, which on every normal path is `run-finished`. Worst-case teardown is therefore ~20 s.
> **On a CANCELLED run (Ctrl-C) the backlog phase is skipped entirely and the terminal attempt is
> bounded at ~500 ms** — because the CLI host allows the whole process about **two seconds** to unwind
> after SIGINT (System.CommandLine's default `ProcessTerminationTimeout`), which the log server's own
> shutdown drain (§12.2) must also fit inside. So on Ctrl-C, delivery of `run-finished` is a single
> best-effort attempt and nothing more; as everywhere else here, **the file is the record.**
>
> **Every drop is recorded, in ONE place.** A console line prints at the end of every run that used
> `--on-event` — **including when nothing was dropped**, because silence on success is the defect
> §8.1 exists to remove — carrying delivered and dropped counts, whether the circuit opened, and
> whether the delivery pump itself faulted. There is deliberately **no per-drop log file**: a consumer
> computes its own drop set exactly, by diffing the `(bracket, seq)` values it received against
> `events.jsonl`, and a file written during teardown is a way for a delivery mechanism to fail a run.
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
| An HMAC body signature (`X-Guardrails-Signature`) | **#604.** Real value (body integrity, which the auth header does not give); needs a documented canonicalization + verification recipe. |
| Multiple endpoints | **#605.** Blocker named: per-endpoint credentials. One `GUARDRAILS_ON_EVENT_AUTH` across two endpoints sends B's token to A. |
| Sender-side kind filtering (`--on-event-kinds …`) | **Rejected**, on #585's own rule: *"A consumer filters on fields, never on a `kind` allowlist… an unrecognized `kind` must remain a visible row."* An allowlist at the **sender** is strictly worse than one at the receiver — a kind added later is silently never delivered, and the receiver cannot tell. Filtering belongs at the receiver, where a missed kind is at least visible. |
| A `webhook-dropped` event kind | **Rejected** (§5.4) — self-referential; a failing drop-notice emits another. |
| A `run.json` webhook section | **Rejected** (§5.4) — the consumer can compute the exact drop set by diffing against `events.jsonl`. |
| A per-drop `logs/<runId>/webhooks.log` | **Designed, then CUT on the adversarial pass** (§5.4). Three of the four arguments for it were false, and it was the largest way a delivery mechanism could have failed a run. The counts survive in the console summary; the detail was always computable. |
| A dedicated `IEventSink` interface | **Designed, then CUT** (§3.1) — one implementation, both justifications false, and it inverted who owns the row shape. |
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
| The log viewer would serve `webhooks.log` | **FALSE.** `LogServer` routes are an explicit allowlist — `LogServer.cs:399`/`:413` say so in the code, *"rather than a wildcard static file server over `_logsRoot`"* | The claim was one of four supports for the per-drop file, three of which turned out false. **The file itself was cut** (§5.4). |
| A full queue drops the newest row | True of the first draft's `FullMode` choice — **and it would have made `run-finished` the guaranteed casualty** of a stalled pump | Design changed to `DropOldest` with the `itemDropped` counting overload (§3.2). |

### What the INDEPENDENT adversarial pass found

Run by a non-authoring agent, per the standing rule that the adversarial pass must not be run by the
author. It sustained three of the design's central claims — the §3.3 lifetime and unwind order, the §3.1
`seq` race (`Scheduler.cs:1109-1111` really does `Task.Run` per worker), and the §4.2 collision (a full
test sweep found **zero** exact-JSON or property-count assertions on event rows, so `bracket` breaks
nothing) — and it falsified **five blockers**. Every one is fixed above; recorded here because "the
author's own critique missed five things" is the most useful fact in this document.

| # | What it found | Fix |
|---|---|---|
| B1 | **`DisposeAsync` throwing turns a green run red.** The first draft made `Emit` exception-proof by contract and left teardown unguarded — while `await using` puts that dispose in a `finally` spanning to `RunCommand.cs:747`, where an `IOException` **replaces the in-flight `return exitCode;`**. Ruling 2 violated on a path the design never bounded. | §3.3 step 3: the whole body is inside `catch (Exception)`, and §8.3 says so. Cutting the per-drop file (§5.4) removed the largest source. |
| B2 | **The Ctrl-C reasoning was fiction.** `Program.cs` passes no `InvocationConfiguration`, so System.CommandLine 2.0.9's default **2-second** `ProcessTerminationTimeout` applies. The 30 s drain was unreachable — and it would have starved layer 2's own 5 s drain, regressing the plan-35 §9.3 fix on that path. | §3.3 step 4: a decided contract — cancelled runs skip the backlog and spend ~500 ms on the terminal row. `ProcessTerminationTimeout` is filed, not changed here. |
| B3 | **A slow endpoint starves `run-finished` before the circuit can help** (above). | §3.3 steps 2–3. |
| B4 | **The mid-run circuit notice re-introduces #145 Bug 1** — a console write from the pump thread while the Spectre `Live` region is active (`RunCommand.cs:526-528` states the constraint three lines above the construction at `:534`). | §5.3: the notice is buffered and flushed with the end-of-run summary. |
| B5 | **`HttpClient` disposed without awaiting the cancelled pump** — the trap `LogServer.cs:102-105` documents verbatim, done backwards one object over. | §3.3 steps 4–5: cancel → await the pump → dispose the transport. |

And nine weaker findings, all applied: the §3.1 snippet emitted a withheld marker on rows that never had
a `detail` (W1); it lacked the `try`/`catch` its own test required (W2); `void Emit(string)` could not
produce §4.3's headers without re-parsing the body it was handed (W3 → `EventDelivery`); the plan-35
§9.3 citation **inverted the lesson**, and the inverted rule is what caused B3 and B5 (W4); the URL
renderer was split across two handoff tasks with a test stranded between them, a `GR2069` shape in a
document that claims to apply that check (W6); §6.3's defense did not cover the `needs-human` question
(W8 → §11 follow-up); a faulted pump under-reported (W9); the #179 citation was wrong even though its
conclusion held (N1); and `bracket` as pure random gave identity without ordering (N2).


**The strongest counter-argument — SUSTAINED, and the independent pass found the version of it that
actually mattered.** The first draft agonized over the wrong case. It conceded that an OPEN circuit drops
`run-finished`, defended the trade, and flagged it for review. The pass pointed at the **closed** circuit
instead, which is both likelier and worse: a slow-but-alive endpoint near the end of a run backs the
serial pump up by as much as 5 × 45 s = **225 seconds** without ever tripping the 5-consecutive-failure
threshold, the terminal row sits behind that backlog, the 30-second drain expires nowhere near it, and
`run-finished` is dropped **deterministically, in the exact scenario the feature exists for.** Plan 35
§9.3's own verdict applies unchanged: *a best-effort mechanism that is 0% effective in its headline case
is dead code.*

**The fix is structural rather than a special case, and it removed my "least firmly held" item entirely.**
Teardown abandons the retry budget and always spends one attempt on the LAST ENQUEUED row, ignoring both
the circuit and the backlog (§3.3 steps 2–3). And because `run-finished` is by construction the last row
a process emits, the dispatcher protects it **without reading `kind` at all** — so the layering objection
that made me reject the special case is not paid either. The pass was also right that the objection was
already hollow: the dispatcher does see `kind`, for the routing header.

**What remains a real cost.** An open circuit still drops every *intermediate* row from that point on.
That is the intended behavior, and the covering mechanism is the contract's own: a consumer that must be
complete re-reads `events.jsonl`.

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

**Fourth: `IEventSink` was an interface with exactly one implementation — speculative abstraction.
CONCEDED IN FULL; it is cut.** My defense was that it kept `HttpClient` out of `RunEventStream` and was
the only way to test wire/file byte-identity without a server. The first is equally true of a delegate;
the second is simply false — a lambda collecting deliveries proves it. And the pass found a third thing I
had missed: `IncludeDetail` on the sink was an **ownership inversion**, the row writer asking the sink
for the sink's policy, when `RunEventStream` owns the row shape and must own the wire copy's. Cutting it
deleted a file, deleted a handoff task, and fixed the ownership. **This is the finding I am least proud
of and most glad of** — I had written the justification for the interface into its own XML doc, which is
how a speculative abstraction survives review: it arrives pre-argued.

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
| 2 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/RunEvents/` | Written **RED** against task 3's shape: `bracket` present, `<unix-ms>-<4hex>`, stable within a process and distinct across two `RunEventStream` instances; **the wire line equals the file line byte-for-byte for every kind with no `detail`** — including a PASSING `guardrail-finished`, which is where the first draft's snippet was wrong; withheld-marker and cap behavior on the two kinds that do carry one; `seq`/`bracket` under concurrent writers; **a callback that throws does not propagate into `AppendLine`**. |
| 3 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/RunEventStream.cs`, `src/Guardrails.Core/Execution/GuardrailFailureReason.cs` | `bracket` on `EventRow` + generated in the ctor + stamped in `AppendLine`; the `EventDelivery` record struct; the `Action<EventDelivery>? onRow = null, bool includeDetail = false` ctor parameters (**defaulted here on purpose** — "no webhook" is correct for a run without `--on-event` and for the 20-odd existing test constructions, which then compile unchanged; contrast task 6, where `BuildObserverChain`'s parameter is deliberately NOT defaulted); the wire-copy block of §3.1 **including its null-detail guard and its `try`/`catch`**; `CapDetail` and `DetailWithheld`. Promote `GuardrailFailureReason.MaxChars` to `internal const`. Update the class doc's row-shape paragraph. |
| 4 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/WebhookEventSink.cs` | The dispatcher: bounded `DropOldest` channel with the counting `itemDropped` overload, `_lastEnqueued`, one retained pump, `internal static bool IsRetryable(...)` as a pure function, the §5.2 bounds as named constants, the §5.3 circuit with its **buffered** notice, the redacted-URL renderer of §6.6, the `onNotice` summary, and the six-step `DisposeAsync` of §3.3 — **backlog phase → guaranteed terminal attempt → cancel → await pump → dispose transport → report**, whole body inside `catch (Exception)`, with a comment citing plan 35 §9.3 and the corrected rule. `SocketsHttpHandler { AllowAutoRedirect = false }`. Errors reported as type name + status only. |
| 5 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/Webhooks/` | `IsRetryable` truth table (every row of §5.1); backoff schedule and the 45 s ceiling; circuit opens at exactly 5 and never closes; a full queue drops the OLDEST and the newest row still gets through, every drop counted; **`DisposeAsync` never throws** (inject a failing notice sink and a failing transport); **the terminal row is attempted even with the circuit open AND with a backlog** — the blocker-3 regression test; the cancelled-path budget is the short one; a faulted pump is reported rather than summarized as zero; **a negative assertion that neither the auth value nor the URL path reaches any notice text.** |
| 6 | `guardrails-harness-developer` | `src/Guardrails.Cli/Commands/RunCommand.cs` | `--on-event` / `--on-event-detail` options + `GUARDRAILS_ON_EVENT` / `_AUTH` fallbacks. **Validation runs EARLY, beside the other option parsing, and a bad value exits `ExitCodes.HarnessError` (1) before any run state is touched** — the same posture as an unparseable `--autonomy`; an invalid URL must not surface mid-run, and `TryStart` must therefore never throw. Validate: scheme is `http`/`https`; the flag occurs at most once (declare it so a second occurrence is *detected*, not silently last-wins); no CR/LF in the auth value; warn on plain `http` to a non-loopback host. Place `WebhookEventSink.TryStart` at `:506` with `await using` per §3.3, passing the CLI's `GuardrailsVersion`-derived `User-Agent` and the run's `CancellationToken`. Thread `onRow`/`includeDetail` through `BuildObserverChain` (`:2382`) to both call sites (`:537`, `:542`). **No default values on THOSE parameters** — a defaulted one lets a production call site silently deliver nothing, the plan-34 §3 swallow hazard; contrast task 3. |
| 7 | `guardrails-test-author` | `tests/Guardrails.Integration.Tests/RunEvents/` | The composition-root and end-to-end proofs, and this is the row that matters most (#382). A real run against a real loopback `HttpListener`: rows arrive, **`run-finished` arrives** (the plan-35 §9.3 assertion that did not exist), **and it still arrives when the receiver is slow enough to have backed the pump up**; bodies match `events.jsonl` line-for-line; headers are exactly §4.3; `detail` is withheld without the flag and present with it; a receiver returning 500 causes retries and then a recorded drop **with the exit code unchanged**; a receiver that never binds leaves the run's exit code and timing untouched. |
| 8 | `guardrails-skill-author` | `.claude/skills/guardrails-domain-knowledge/SKILL.md` | The contract quick-reference gains `--on-event`: the delivery key `(runId, bracket, seq)`, "a failed delivery never affects the run", and "`detail` is withheld unless `--on-event-detail`". |
| 9 | `guardrails-architect` | `docs/plans/585-layer3-webhooks-contract.md` | Fold the draft-PR review outcome back into this document, and record the `ws:` closure so #585 can be closed with the implementation. |

**Sequencing.** 1 ∥ 2 (RED) → 3 → 4 → 5 → 6 → 7 → 8 → 9. Tasks 1 and 2 are independent and may run in
parallel; task 2 is authored against task 3's shape and must fail before task 3 lands. Task 7 is the
gate: nothing here is proven by unit tests alone, because the defect class this feature is exposed to —
a correctly-implemented projection swallowed by the composition root — is invisible to them.

**One deliberate seam-crossing, named rather than hidden.** `CapDetail` and `DetailWithheld` are §8.3
*wire* concepts implemented in task 3's *row writer*. That follows from cutting `IEventSink`: the writer
owns the row shape, so it owns the wire copy's shape too. The cost is that half the §8.3 payload contract
lives in `RunEventStream` and half in `WebhookEventSink`. I take that over the ownership inversion the
interface created, but it is the one place a reader will have to look in two files.

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
6. **New issue: the CLI host gives the whole process ~2 seconds to unwind after Ctrl-C.**
   `src/Guardrails.Cli/Program.cs` passes no `InvocationConfiguration`, so System.CommandLine's default
   `ProcessTerminationTimeout` applies. **This is not a layer-3 problem** — `LogServer`'s own
   `ShutdownDrainTimeout` is **5 seconds** and therefore already exceeds it, so plan 35's hard-won
   terminal-row delivery is truncated on the cancelled path *today*. Layer 3 adapts to the budget
   (§3.3 step 4) rather than raising it, because the timeout governs every command and the trade —
   a Ctrl-C that is genuinely held for longer — is a maintainer UX call. **Found by the adversarial
   pass on this design; it belongs to the CLI host.**
7. **New issue: the `needs-human` question has no carrier but `detail`.** `AttemptJournaler.cs:543`
   splices `$"needs human: {question}"` into `TaskResult.Summary` → `task-settled.detail`, so with
   detail withheld a supervisor learns *that* a task needs a human and not *what was asked* — the
   filesystem read #585 exists to remove, on the path #361's answer injection depends on. The right fix
   is a field designed for the question with its own disclosure rule, not widening `detail`. §6.3 states
   the gap rather than papering over it.
8. **New issue (`guardrails-ux`): surface delivery health in the live table.** §5.3's circuit notice is
   buffered to the end of the run because a console write into an active Spectre `Live` region corrupts
   it (#145 Bug 1). An operator watching a two-hour run should be able to see that webhook delivery has
   stopped without waiting for the summary — but that is a live-table design question, not a webhook
   one, and smuggling it in here would be the scope creep this document keeps refusing elsewhere.
6. **#585 itself:** on merge, close it — layer 3 completes the three layers, and §2.1 closes the `ws:`
   question on the record rather than leaving it open behind the issue.
