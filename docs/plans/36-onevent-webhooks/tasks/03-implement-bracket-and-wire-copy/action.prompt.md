## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `03-implement-bracket-and-wire-copy`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "03-implement-bracket-and-wire-copy": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "03-implement-bracket-and-wire-copy": { "someKey": "someValue" },
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

Make the `RunEventBracketTests` tests pass: stamp `bracket` on every `events.jsonl` row, and feed the
already-serialized wire copy of each row to the `onRow` callback from inside the append lock.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/RunEventStream.cs`
and `src/Guardrails.Core/Execution/GuardrailFailureReason.cs` — the same two files task 02 stubbed,
and nothing else in that directory. After this task completes the harness runs a `git diff` check and
rejects any edit outside that surface — the CLI, the test projects, the `.csproj`, the plan folder.
An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error
caused by a missing symbol outside that surface, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT edit the tests authored upstream.** `tests/Guardrails.Core.Tests/RunEvents/RunEventBracketTests.cs`
is the specification, and it is outside your write scope. If one of those tests is genuinely wrong,
write `{"needsHuman": {"question": "<which test, and why it is wrong>", "kind": "blocked-work"}}` to
the state-out path and stop rather than changing it — an out-of-scope edit to a test file fails the
task immediately and consumes a retry.

**The stubs are already there.** Task 02 added `EventDelivery`, the
`Action<EventDelivery>? onRow = null, bool includeDetail = false` constructor parameters (accepted and
discarded), and promoted `GuardrailFailureReason.MaxChars` to `internal const`. Your job is to fill
in the behaviour behind them, not to re-declare them.

**The design quotes the exact code.** Read `docs/plans/36-onevent-webhooks.md` §3.1 — it contains the
constructor signature, the `EventDelivery` doc comment, and the whole of the new `AppendLine` body,
verbatim. Follow it. §4.2 is the `bracket` rationale, §4.3 the delivery-id shape, §6.3 the `detail`
policy.

### What to change in `RunEventStream`

**1. `bracket` on `EventRow`, generated ONCE in the constructor.**
`<unix-ms>-<4 hex>` — e.g. `1756948327104-a3f9` — lowercase hex, stored in a readonly field, stamped
on every row **inside the append lock** next to `Seq` and `At`:

```csharp
EventRow stamped = row with { Seq = ++_seq, At = DateTimeOffset.UtcNow, Bracket = _bracket };
```

Zero call-site changes: one property on `EventRow`, one line in the constructor, one term in that
`with` expression. The millisecond prefix is what lets a receiver ORDER two brackets (§4.2); the
random suffix is what keeps two processes that start in the same millisecond distinct. It is
deliberately NOT a timestamp anyone should compute elapsed time from — say so where you generate it.

**2. The wire copy.** §3.1 gives the block whole, and two parts of it are load-bearing:

- **the null-detail guard.** `stamped.Detail is null` short-circuits to the SAME instance, so the
  wire line is the string that was already serialized — byte-identical, with no second
  `JsonSerializer.Serialize` call. A `task-started`, `attempt-started`, `run-finished` or **passing**
  `guardrail-finished` row therefore never carries a withheld marker where there was nothing to
  withhold.
- **the `try`/`catch`.** A public delegate parameter cannot be forced not to throw, and a throw
  escaping here would propagate into a Scheduler worker **while holding `_gate`**. A delivery
  mechanism may never affect the run (§8.3). Catch `Exception`, do nothing, and say why in a comment.

The block goes **after** the file append: if the append throws, nothing is delivered either, and "the
row is in `events.jsonl` and was never delivered" stays a true statement about every drop.

The delivery id is `$"{_runId}:{_bracket}:{stamped.Seq}"` (§4.3) and the kind and the line travel with
it, so the dispatcher never re-parses the JSON it was just handed.

**3. `CapDetail` and `DetailWithheld`.**

- `DetailWithheld` is the fixed string `(detail withheld; pass --on-event-detail)` (§6.3). It is
  PRESENT on the wire row rather than omitted, so a receiver can never read "withheld" as "the
  guardrail had nothing to say".
- `CapDetail` returns the value unchanged when it is at most `GuardrailFailureReason.MaxChars`
  characters, and otherwise the FIRST `MaxChars` characters followed by `…[truncated]`. Reference the
  constant — do not copy the literal `2000`. Task 02 promoted it to `internal const` precisely so the
  number has one owner.

These two are §8.3 *wire* concepts living in the *row writer*, which the design names as its one
deliberate seam-crossing (§10): the writer owns the row shape, so it owns the wire copy's shape too.
Note that in a comment rather than leaving the next reader to wonder.

**4. Update the class doc.** The "Row shape" paragraph currently enumerates what is on every row.
`bracket` now joins `kind`/`seq`/`at`/`runId` there, with what it is for: the delivery key is
`(runId, bracket, seq)`, `seq` restarts at 1 in each new process, and without `bracket` a receiver
deduplicating on `(runId, seq)` silently discards an entire resumed run. Say that the wire copy
differs from the file line in exactly one field, only ever `detail`, and only when the row has one.

### What NOT to change

- **`GuardrailFailureReason`** — task 02 already promoted the one keyword. Leave `Tail`,
  `MaxTailLines` and the class doc alone.
- **The existing kinds and fields.** 41 `Category=RunEvents` tests pin them; `bracket` is additive.
- **The `onRow` / `includeDetail` defaults.** They stay defaulted here on purpose: "no webhook" is
  correct for a run without `--on-event` and for the ~20 existing test constructions, which must keep
  compiling unchanged. (The CLI's `BuildObserverChain` parameter in a later task is deliberately NOT
  defaulted — that is the opposite call, for the opposite reason.)
- **`WebhookEventSink`** — not yours, and now outside your write scope rather than merely discouraged.
  `src/Guardrails.Core/Execution/WebhookEventSink.cs` may ALREADY be present when you run: task 04 is
  a parallel root that lands it as throwing stubs. Leave it exactly as you find it. This task changes
  the row writer only; nothing here opens a socket.

### Done when

Every `RunEventBracketTests` test passes, including the ten pinned methods, and the existing
`Category=RunEvents` tests still pass — the kinds and fields that landed in plans 34 and 35 keep their
exact shape.
