## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `02-author-tests-bracket-and-wire-copy`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "02-author-tests-bracket-and-wire-copy": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "02-author-tests-bracket-and-wire-copy": { "someKey": "someValue" },
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

Author the RED tests for two things `events.jsonl` does not do yet — the `bracket` field, and the
second destination for each row — **and the minimum stubs that make those tests COMPILE**.

**Scope boundary (harness-enforced):** Write only to these three paths:

- `tests/Guardrails.Core.Tests/RunEvents/RunEventBracketTests.cs` (new file — the tests)
- `src/Guardrails.Core/Execution/RunEventStream.cs` (stubs only — see "The stubs" below)
- `src/Guardrails.Core/Execution/GuardrailFailureReason.cs` (one token — see "The stubs" below)

After this task completes the harness runs a `git diff` check and rejects any edit outside that
surface — any other production file, any other test file, the `.csproj`, the plan folder. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in a file outside that surface, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Two source files are in scope for a narrow reason (#155).** A TDD red must COMPILE. The tests below
name symbols that do not exist yet, so this task adds those symbols as *signatures with no behaviour*,
and nothing more. If you implement the behaviour here, this task's second guardrail fails: it is a
per-test census that requires each pinned test to be observed **Failed**.

### The stubs — exactly these, and nothing else

**1. `src/Guardrails.Core/Execution/RunEventStream.cs` — the delivery record.** File-level, in
namespace `Guardrails.Core.Execution`, beside the `RunEventStream` class (design §3.1 puts it in this
file):

```csharp
public readonly record struct EventDelivery(string DeliveryId, string Kind, string JsonLine);
```

Carry its `<summary>` from design §3.1 verbatim.

**2. `src/Guardrails.Core/Execution/RunEventStream.cs` — two new constructor parameters.** Widen the
existing constructor, both parameters DEFAULTED so all ~20 existing `new RunEventStream(...)` call
sites in `src/` and `tests/` compile unchanged:

```csharp
public RunEventStream(
    IRunObserver inner, string directory, string runId,
    Action<EventDelivery>? onRow = null, bool includeDetail = false)
```

Copy the two `<param>` doc blocks from design §3.1 (they document the contract task 03 makes true).

**Accept them and IGNORE them.** No field, no branch, no call — `AppendLine` is byte-for-byte
unchanged by this task. Discard them explicitly in the constructor body so the intent is legible and
the build stays clean:

```csharp
// Accepted and ignored: task 03 stores these and feeds the wire copy from inside the append lock
// (design §3.1). Stubbed here only so RunEventBracketTests COMPILES against the shape it asserts.
_ = onRow;
_ = includeDetail;
```

Do **not** store them in private fields you never read — this repo builds with
`TreatWarningsAsErrors`, and an assigned-but-never-read private field is CS0414, which would fail this
task's first guardrail on a stub that is otherwise correct.

**3. `src/Guardrails.Core/Execution/GuardrailFailureReason.cs` — one token.** Change

```csharp
private const int MaxChars = 2000;
```

to `internal const int MaxChars = 2000;`. Nothing else in that file moves — not `MaxTailLines`, not
`Tail`, not the class doc. `Guardrails.Core.csproj` already carries
`<InternalsVisibleTo Include="Guardrails.Core.Tests" />`, so the test below can then reference
`GuardrailFailureReason.MaxChars` directly — and it should.

**All three stubs above are checked directly**, by `guardrails/01-stubs-are-real.ps1`, the first and
cheapest guardrail on this task: it greps `src/Guardrails.Core/Execution/RunEventStream.cs` for a
`record struct EventDelivery` declaration and
`src/Guardrails.Core/Execution/GuardrailFailureReason.cs` for `internal const int MaxChars`. Both
measure **zero** on the tree you start from, so both are real work. An earlier version of this plan
relied on the tests' *references* to carry that proof transitively — "it would not compile if the
stub were missing" — and that was measured false: a `RunEventBracketTests` naming none of the three
compiles, and every guardrail on this task went green over it. Write the references anyway (they are
the natural way to write these tests), but the stubs are the deliverable and they are asserted on
their own.

### The tests

**File:** `tests/Guardrails.Core.Tests/RunEvents/RunEventBracketTests.cs`.
**Class name is pinned: `RunEventBracketTests`** — this task's census guardrail and task 03's
tests-pass guardrail both filter on it, so it must match exactly.

**Every test carries BOTH traits, in this order, above `[Fact]`:**

```csharp
[Trait("Category", "RunEvents")]
[Trait("Plan", "36-onevent")]
[Fact]
```

`Category=RunEvents` is what the guardrails filter on. `Plan=36-onevent` exists **only** so the
plan's baseline preflights can exclude this plan's intentional red from the "never build on red"
check; nothing filters on it alone.

Read `tests/Guardrails.Core.Tests/RunEvents/RunEventVocabularyTests.cs` first and follow its idiom:
the `FlatTask`, `NewTempDirectory` and `ReadEventLines` fixtures (copy them into your file — that one
is out of your write scope), `IRunObserver.Null` as the inner observer, `JsonDocument.Parse` on each
line, `Parallel.For` for the concurrent case (`Task.Wait`/`.Result` trip xUnit1031, which is an error
here).

**`EventRow` is a PRIVATE nested record.** You cannot reference it and must not try. Every assertion
in this file is over the **serialized JSON** — the raw lines of `events.jsonl`, and the `JsonLine`
string on an `EventDelivery`. That is also what the design promises a receiver, so asserting on the
wire form is asserting the actual contract.

**Collect deliveries with a lambda.** There is no sink type and no server: construct the stream with
`onRow: d => collected.Enqueue(d)` over a `ConcurrentQueue<EventDelivery>`. Use the concurrent
collection even in the single-threaded tests — a `List<T>` would be unsafe against exactly the wrong
implementation one of these tests hunts (an `onRow` invoked outside the append lock).

Two values are fixed strings the design pins; hard-code them, do not paraphrase:

- the withheld marker is exactly `(detail withheld; pass --on-event-detail)` (§6.3)
- the truncation form is the FIRST `GuardrailFailureReason.MaxChars` characters of the detail followed
  by `…[truncated]`, applied only when the detail is longer than that. Keep-the-head is what the
  *suffix* means; a tail-biased cap would need a prefix marker instead.

#### The ten behaviours — these exact method names

**1. `BracketIsPresentOnEveryRow`**
Raise several kinds through one stream — `TaskStarting`, `AttemptStarting`, `GuardrailFinished`,
`AttemptFinished`, `TaskFinished`, `RunFinished`. Assert **every** line in `events.jsonl` has a
`bracket` property holding a non-empty string. Include `run-finished`: it is the row a CI wrapper
keys on, and it is the only run-scoped kind, so a `bracket` stamped per-task would miss it.

**2. `BracketMatchesUnixMillisAndFourHex`**
Assert the value matches `^[0-9]{13}-[0-9a-f]{4}$` — **and** that the numeric prefix, read as unix
milliseconds, lands within a few minutes of `DateTimeOffset.UtcNow`. The regex alone accepts
`0000000000000-abcd`, which would satisfy the shape while destroying the ordering §4.2 buys with the
millisecond prefix. Assert lowercase hex explicitly.

**3. `BracketIsStableAcrossRowsInOneStream`**
Every row from ONE `RunEventStream` instance carries the SAME bracket — it is generated once in the
constructor, not per row. This is the test that fails against a bracket built inside `AppendLine`.

**4. `BracketDiffersAcrossTwoStreams`**
Two `RunEventStream` instances over the **same directory** and the **same `runId`** produce different
brackets. Same runId is the discriminating part: a bracket derived from the run id would pass a
test that varied the run id. Assert the whole bracket string differs; do **not** assert the
millisecond prefix differs — two constructions in one test share a millisecond routinely, and the
4-hex suffix is what §4.2 says keeps them distinct.

**5. `WireLineEqualsFileLineWhenDetailIsNull`**
Construct with an `onRow` collector and `includeDetail: false`. Raise only kinds that carry no
`detail`: `task-started`, `attempt-started`, `run-finished`. Assert:
- one delivery per file line, same count, same order;
- each `EventDelivery.JsonLine` is **ordinally equal** to its file line — byte-for-byte, not
  "parses to the same object";
- `EventDelivery.Kind` equals the row's `kind`;
- `EventDelivery.DeliveryId` is exactly `<runId>:<bracket>:<seq>` built from that row's own values
  (§4.3's pre-assembled idempotency key).

**6. `WireLineEqualsFileLineForPassingGuardrailFinished`**
The case the design's first draft got wrong. With `includeDetail: false`, raise a `GuardrailFinished`
whose `GuardrailResult.Passed` is **true** — the row's `detail` is null, so the wire copy must be
byte-identical to the file line. Assert the wire line contains no `detail` property at all, and in
particular not the withheld marker. A receiver must never see "withheld" where there was nothing to
withhold.

**7. `WireLineCarriesWithheldMarkerWhenDetailPresent`**
With `includeDetail: false`, raise a FAILING `GuardrailFinished` whose `Reason` is a recognisable
secret-shaped string, and a `TaskFinished` whose `Summary` is another. Assert:
- the **file** line still carries the full detail — `events.jsonl` fidelity is never affected by the
  wire policy (§6.3);
- the wire line's `detail` is **present** and is exactly the withheld marker;
- neither original string appears anywhere in the wire line;
- every OTHER property is identical between the wire line and the file line (parse both, compare the
  property sets and values with `detail` excluded). `detail` is the only field that may differ, and
  a test that checks only `detail` would not notice a second one drifting.

**8. `WireLineCapsDetailAtMaxCharsWhenIncludeDetailIsTrue`**
With `includeDetail: true`, raise a failing `GuardrailFinished` whose `Reason` is
`new string('x', GuardrailFailureReason.MaxChars + 500)`. Assert the wire `detail` is the first
`GuardrailFailureReason.MaxChars` characters followed by `…[truncated]`, and that its length is
`GuardrailFailureReason.MaxChars + "…[truncated]".Length`. In the same test, raise a second failing
guardrail whose reason is comfortably SHORTER than the cap and assert it passes through **unchanged**,
with no suffix — a cap that fires unconditionally is as wrong as one that never fires.
**Reference `GuardrailFailureReason.MaxChars` by name**, never the literal `2000`: one owner of the
number is the whole point of the promotion, and the reference is what proves it happened.

**9. `SeqAndBracketStayConsistentUnderConcurrentWriters`**
Drive appends from several threads with `Parallel.For` through a stream built with an `onRow`
collector. Assert:
- every `seq` in the file is unique, and file order agrees with `seq` order;
- every row carries the one bracket;
- the collected deliveries have the same count as the file rows and the same `(bracket, seq)` values
  **in the same order**. That last one is the assertion that fails against an `onRow` invoked
  OUTSIDE the append lock — enqueue order equals file order is the property §3.1 buys by putting the
  callback inside the lock, and it is why a receiver gets rows in `seq` order without re-sorting.

**10. `AThrowingOnRowCallbackDoesNotPropagate`** — will be GREEN, and that is correct.
Construct with `onRow: _ => throw new InvalidOperationException("boom")`. Raise several events.
Assert no exception escapes the observer call, that **all** rows still landed in `events.jsonl`, and
that a recording inner observer still received every forwarded call.

This one is a **DECLARED EXEMPTION from the red census**, and the reason is structural rather than
incidental: against the stubs above `onRow` is never invoked, so nothing can throw and the test is
green — and a *correct* implementation is also green, because §3.1's `try`/`catch` is exactly what it
pins. Neither outcome distinguishes a good test here, so the census requires only that it EXECUTE
(present in the TRX, not skipped). Write it anyway: it is the regression guard for the one line that
keeps a delivery mechanism from taking down a Scheduler worker while holding `_gate`.

### What NOT to do

- **Do not implement `bracket`, the wire copy, `CapDetail` or `DetailWithheld`.** That is task 03.
  `AppendLine` must be unchanged when you are done.
- **Do not touch any other test file**, including `RunEventStreamTests.cs` and
  `RunEventVocabularyTests.cs`. Copy the fixtures you need instead.
- **Do not add a `Plan` trait to any existing test.**
- **Do not change `GuardrailFailureReason` beyond the one visibility keyword.**

### Done when

`Guardrails.Core.Tests` **compiles** (guardrail 01), and of the ten pinned tests the nine that name
unbuilt behaviour are observed **Failed** while `AThrowingOnRowCallbackDoesNotPropagate` merely runs
(guardrail 02). Failing is intentional; not compiling is a mistake to fix.
