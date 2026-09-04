## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `06-author-tests-webhook-dispatcher`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "06-author-tests-webhook-dispatcher": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "06-author-tests-webhook-dispatcher": { "someKey": "someValue" },
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

Author the tests that specify the webhook **dispatcher** — the queue, the pump, the circuit, the drop
accounting, the six-step teardown, and the rule that no notice ever prints a credential — and add the
member **stubs** they compile against.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Webhooks/WebhookEventSinkTests.cs`, `src/Guardrails.Core/Execution/WebhookEventSink.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

`WebhookEventSink.cs` is a PRODUCTION file and it IS inside your scope — but only for the member
stubs below. Real dispatcher logic is task 07's job. Writing it here makes your own tests green and
fails this task's red census.

**The one upstream symbol you depend on** is `EventDelivery` — the `readonly record struct
EventDelivery(string DeliveryId, string Kind, string JsonLine)` that task 03 lands in
`RunEventStream.cs`. If it is missing, that is the missing-symbol case above: emit
`{"needsHuman": ...}` rather than defining your own copy. A second definition of the row's delivery
shape is exactly the second-owner problem the design spends §3.1 refusing.

Task 05 has already implemented `IsRetryable` and `RedactUrl` in this same file. **Leave both alone**,
and leave the §5.2 bounds constants task 04 landed alone — your tests read them, they do not redefine
them.

### The stub surface

Add to `WebhookEventSink` (namespace `Guardrails.Core.Execution`), and nothing beyond it:

```csharp
public sealed class WebhookEventSink : IAsyncDisposable
{
    // Production entry point. Returns null when there is no --on-event URL. Never throws:
    // the CLI validates the URL EARLY, before any run state is touched (design §6.4, task 09).
    public static WebhookEventSink? TryStart(
        Uri? url, string? auth, string userAgent, Action<string> onNotice, CancellationToken cancellationToken)
        => throw new NotImplementedException("task 07");

    // The Action<EventDelivery> onRow callback RunEventStream invokes inside its append lock.
    public void Emit(EventDelivery delivery) => throw new NotImplementedException("task 07");

    public ValueTask DisposeAsync() => throw new NotImplementedException("task 07");

    // TEST SEAM. Internal, and Guardrails.Core.csproj already carries
    // <InternalsVisibleTo Include="Guardrails.Core.Tests" /> (measured: line 27).
    internal WebhookEventSink(
        Uri url, string? auth, string userAgent, Action<string> onNotice,
        HttpMessageHandler handler, double timeScale, CancellationToken cancellationToken)
        => throw new NotImplementedException("task 07");
}
```

**Every stubbed member throws `NotImplementedException`. `DisposeAsync` included.** A
`=> ValueTask.CompletedTask` stub would make both `DisposeAsyncNeverThrows…` tests pass against a tree
where teardown does not exist, and the red census reads that as "not coupled to the code path" and
fails this task. The same trap applies to any stub that returns a default instead of throwing.

**The two seam parameters, and why each is legitimate rather than test-shaped design damage:**

- **`HttpMessageHandler handler`** — the HTTP transport is a PROCESS boundary. Faking it is correct
  and expected: it is what lets every bound, the circuit and the teardown be proven in-process with
  no listener bound and no port. Production goes through `TryStart`, which builds the real
  `new SocketsHttpHandler { AllowAutoRedirect = false }` (§6.5) and is the ONLY path that does — so
  the production handler configuration cannot be bypassed by accident.
- **`double timeScale`** — every duration bound in §5.2 (the per-attempt timeout, the three backoff
  steps, the per-row ceiling, the backlog drain budget and the terminal delivery timeout) is
  multiplied by it. `TryStart` never passes it; production is `1.0`. Without it,
  `CircuitOpensAtExactlyFiveConsecutiveFailures` alone costs 5 rows × (1 + 2 + 4) s ≈ **35 seconds of
  sleeping**, and three of the tests below need the circuit open before they start. A unit suite that
  sleeps two minutes on every attempt of task 07 and on every CI run is a guardrail people delete.
  **The scale multiplies at the USE site; the constants themselves keep their §5.2 values** — two of
  your tests assert those values directly, so an implementation that scales the constants breaks
  them, which is the tests doing their job.

### The test class

File `tests/Guardrails.Core.Tests/Webhooks/WebhookEventSinkTests.cs`, namespace
`Guardrails.Core.Tests.Webhooks`. Class name is pinned: **`WebhookEventSinkTests`**. **Every test
method carries BOTH traits:**

```csharp
[Trait("Category", "RunEvents")]
[Trait("Plan", "36-onevent")]
```

`Category=RunEvents` is what this task's census and task 07's filter select on; `Plan=36-onevent`
exists ONLY so the plan's baseline preflights can exclude this plan's intentional red. A test missing
`Category` is invisible to the census and reads as "never authored".

You will want one fake `HttpMessageHandler` with a settable response behaviour (a status to return,
an exception to throw, a delay to wait) that **records every request it receives** — the delivery id,
the time it arrived, and how many times each id was seen. Almost every assertion below is a statement
about that record. Capture notices with a plain `List<string>` behind the `Action<string> onNotice`.

### Two traps that will otherwise cost you a retry

1. **A test that asserts only the value of a constant is GREEN against the stub**, because task 04
   already landed those constants. The census reads a green pinned test as "not coupled to the code
   path" and fails the task. Where a test below says "assert the constant AND the behaviour", both
   halves are required: the constant pins the production number, the behaviour pins that the number
   is actually enforced.
2. **A negative assertion over an empty collection passes vacuously.** Every "never contains" test
   below must first assert that notices were produced at all and that they contain what they are
   SUPPOSED to contain. That positive control is the difference between proving a secret was withheld
   and proving nothing ran (#176).

### The behaviours — these exact method names

The fourteen names below are pinned. The census reads them out of the runner's TRX, so a renamed or
merged test reads as an unbound behaviour.

**The bounds (design §5.2).**

**1. `BackoffScheduleIsOneTwoFourWithJitter`**
Assert the constants: 4 attempts per row (initial + 3 retries), backoff steps of 1 s / 2 s / 4 s,
jitter in `[0.5, 1.5)`. **And assert the behaviour:** with a transport that returns 503 every time,
one row produces **exactly 4** requests, and the three gaps between them each fall inside their
jittered band. Allow generous slack for timer resolution and scheduler noise on top of the band —
Windows' timer granularity alone is ~15 ms. The one gap comparison that is exact regardless of
jitter, and worth asserting because it pins the SHAPE rather than the numbers: the third gap is
always greater than the first, since 2.0 × step exceeds 1.5 × step. Jitter exists so that a parallel
burst of failures does not resynchronize.

**2. `PerRowCeilingIsFortyFiveSeconds`**
Assert the constant is 45 s. **And assert the behaviour:** the ceiling is a per-row
`CancellationTokenSource`, so it truncates the schedule however the attempt timings fall. With a
transport that hangs past the per-attempt timeout on every attempt, the full schedule (4 × the
per-attempt timeout, plus 1 + 2 + 4 of backoff) exceeds the ceiling — so the row must be given up **at
the ceiling**, with fewer than 4 completed attempts, rather than running the schedule out. A ceiling
that exists only as a constant is a comment.

**The circuit (design §5.3).**

**3. `CircuitOpensAtExactlyFiveConsecutiveFailures`**
"Exactly" is the assertion, and it has three halves. With a transport that fails every row: after
**four** terminally-failed rows the circuit is still closed — row five IS attempted. After the
**fifth**, row six gets **zero** requests: dropped on arrival, counted, no HTTP attempted. And
**consecutive means consecutive**: four failures, then one delivered row, then four more failures
leaves the circuit closed, because a success resets the counter. Without that third half, a naive
"five failures ever" counter passes.

**4. `CircuitNeverCloses`**
Once open it stays open for the rest of the run: no half-open probe, no timer. Open the circuit, then
flip the transport to return 200, wait longer than any plausible cooldown at your chosen time scale,
and emit more rows — assert the transport receives **nothing** for them and every one is counted as a
drop. Assert too that there is no API to reset it. Rationale, so nobody re-litigates it in a later
attempt: 5 rows × 4 attempts = 20 consecutive failed POSTs is not a transient, and "when does it
re-open?" is a question with no good answer that somebody would have to keep answering.

**The queue (design §3.2).**

**5. `FullQueueDropsTheOldestNotTheNewest`**
Capacity is 1024 with `FullMode = DropOldest`. Block the pump (a transport that hangs), emit
comfortably more than 1024 distinguishable rows, then release and tear down: the rows that were lost
are the **oldest**, and the **newest row still gets through**. This is the whole reason `DropOldest`
was chosen over `DropWrite`: with any newest-loses policy the queue is full exactly when the terminal
row arrives, so the single row a CI wrapper exists to receive is the one guaranteed to be dropped.
Assert on the delivery ids, not on counts alone — counts cannot tell head-drop from tail-drop.

**6. `EveryDroppedRowIsCounted`**
The channel is built with the counting `itemDropped` overload precisely so a displaced row is counted
rather than vanishing. Assert the reported dropped count against **both** sources: rows displaced by
a full queue, and rows dropped on arrival by an open circuit. Then assert the third case from §3.3's
closing paragraph: an `Emit` **after** `DisposeAsync` has returned writes to a completed channel, so
`TryWrite` returns false — it must be a silent no-op that **never throws**. Its count cannot be
reported (the summary has already printed); not throwing is the whole requirement. There is no known
path that emits after teardown and the sink is nonetheless required to survive one.

**Teardown must never fail the run (design §3.3 step 3, blocker B1).**

**7. `DisposeAsyncNeverThrowsWhenTheNoticeSinkThrows`**
Inject an `onNotice` that throws on every call; `DisposeAsync` must complete without throwing.

**8. `DisposeAsyncNeverThrowsWhenTheTransportThrows`**
Inject a transport that throws — on send, and on its own `Dispose` — with rows still pending;
`DisposeAsync` must complete without throwing.

Say why in a comment on both, because it is the finding that nearly shipped: `await using` puts the
dispose in a compiler-emitted `finally` that spans past `return exitCode;` in `RunCommand.RunAsync`,
**so an exception thrown in teardown replaces the in-flight return and turns a wholly green run into
an unhandled exception** — and on the fault path it replaces the original exception and destroys the
diagnosis. A delivery mechanism may never affect the run.

**The terminal row — the guarantee the whole feature exists for (design §3.3 steps 2–3).**

**9. `TerminalRowIsAttemptedWithTheCircuitOpen`**
**This is the most important test in this file: it is the blocker-3 regression test.** Open the
circuit, emit one more row, then dispose with a transport that would now succeed — assert that
**exactly one** request is made for that last-enqueued row's delivery id. The circuit does not
suppress the terminal delivery; §3.3 step 3 always spends one attempt on the last-enqueued row,
whatever the circuit says.

This depends on a precision that is easy to implement backwards, so pin it in the test's own comment:
**`_lastEnqueued` records the last row `Emit` SAW — whether or not the circuit or a full queue kept
that row out of the pump's path.** If the circuit's arrival-drop also skipped the `_lastEnqueued`
write, the guarantee would evaporate in exactly the scenario it exists for.

**10. `TerminalRowIsAttemptedWithABacklogPending`**
The other half of blocker 3, and the likelier failure in the field: a slow-but-alive endpoint backs
the serial pump up near the end of a run without ever tripping the failure threshold, so the terminal
row sits behind a backlog. Fill a backlog behind a slow transport, dispose, and assert the
last-enqueued row still gets its one attempt after the backlog budget expires. Note in the comment
that teardown **abandons the retry budget entirely** — one attempt per row through the backlog — and
that retrying during teardown is precisely what starves the terminal row.

**11. `CancelledPathUsesTheShortBudget`**
Assert the constants: backlog drain budget 10 s normally and **0 s** when the run was cancelled;
terminal delivery timeout 10 s normally and **500 ms** when cancelled. **And assert the behaviour:**
with the run's `CancellationToken` already cancelled and a backlog queued, the backlog rows are
**not attempted at all** and exactly one attempt is spent on the last-enqueued row. The budget is
selected by the token; the drain itself never observes it — a token that is already cancelled would
otherwise skip the drain entirely, which is the bug this shape exists to avoid. The reason the number
is 500 ms and not 30 s: `Program.cs` passes no `InvocationConfiguration`, so System.CommandLine's
default ~2 second `ProcessTerminationTimeout` bounds the WHOLE Ctrl-C unwind, which the log server's
own 5 s drain must also fit inside.

**12. `AFaultedPumpIsReportedNotSummarizedAsZero`**
`Task.WhenAny(pump, delay)` does **not** throw on a faulted pump, so a summary reading "0 dropped"
while rows sit in a dead channel would be the silent disappearance §2.2 mocks the shell shim for.
Induce it with a transport that **ignores its cancellation token** and returns only long after the
teardown budget — so step 4's bounded await expires with the pump still inside `SendAsync`. Assert
`DisposeAsync` still completes within its bounds, and that the notice text reports delivery stopping
early with a nonzero count of rows never attempted, rather than a clean "0 dropped". Use a finite
delay comfortably longer than the scaled budget (a few seconds), never `Timeout.Infinite`, which
leaves a task hanging for the life of the test host.

**No notice ever prints a credential (design §5.4's closing rule, and §6.6).**

**13. `NoNoticeTextEverContainsTheAuthValue`**
Construct with a recognisable auth value (e.g. `Bearer sup3r-s3cret-t0k3n`), drive failures of every
shape the notices cover — a retryable status that exhausts, a hard 4xx, and a transport exception
**whose own `Message` contains the full request URI and the auth value** — then dispose and assert no
captured notice contains the token or the header value. The transport-exception case is the one that
matters: `HttpRequestException`'s message routinely carries the whole URI, which is why the rule is
**type name and status code only, never `ex.Message`**.
POSITIVE CONTROL, in the same test: assert notices were produced and that they name the exception
TYPE and the status — otherwise "contains no secret" is satisfied by having produced nothing.

**14. `NoNoticeTextEverContainsTheUrlPath`**
Same shape, aimed at the URL: with an endpoint like
`https://hooks.example.com/services/T00/B11/XyZ?token=abc`, assert no notice or summary line contains
any path segment or the query — every one renders the URL as `<scheme>://<host>[:<port>]/…` via the
`RedactUrl` task 05 shipped. POSITIVE CONTROL: assert the host DOES appear, so the test cannot pass on
an empty notice list. Cover the end-of-run summary line too — it prints on **every** run that used
`--on-event`, **including at zero drops**, because silence on success is the exact defect this whole
issue is about, and a line that always prints is proof the mechanism ran at all.
For Slack and webhook.site **the URL path IS the credential**; this test and #13 are the reason the
renderer exists.

### Done when

`Guardrails.Core.Tests` **compiles** and all fourteen tests **fail** against the stubs. Failing is the
deliverable; not compiling is a mistake to fix. Do NOT implement the dispatcher — that is task 07.
