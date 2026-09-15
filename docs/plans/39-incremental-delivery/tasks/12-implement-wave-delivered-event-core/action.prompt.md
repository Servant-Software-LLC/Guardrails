## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `12-implement-wave-delivered-event-core`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "12-implement-wave-delivered-event-core": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

**A PRE-EXISTING test goes RED the moment task 11 merges, and closing its Core half is your
job.** `tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs` enumerates
`typeof(IRunObserver).GetMethods(...)` and asserts four named decorators DECLARE every member —
explicitly rejecting the interface's empty default (*"Inheriting the interface's empty default
declares nothing"*). Task 11 adds `WaveDelivered` with a no-op default, so the sweep fails on all
four until the declarations land: `RunEventStream` and `ObserverProjection` here, the two Cli
ones in task 13. It is in no task's `writeScope`, needs no edit, and goes green by itself once
you declare. **Do not "fix" it by touching the test.**

Note also that a file search for `IRunObserver` returns FILES, not implementers: 17 files in Core mention
it and only three DECLARE it — the two transparent decorators above plus the nested `NullObserver`, whose
contract is to swallow and which must NOT forward.

Forward `WaveDelivered` through every **Core** `IRunObserver` decorator so `WaveDeliveredEventTests`
passes. The member is `void WaveDelivered(Model.WaveNode wave, Journal.WaveDeliveredRecord delivery)`.
Forward the record INSTANCE unchanged — never rebuild it — so every observer downstream sees exactly the
record the journal persisted (design 39 §5). Each decorator does one more thing:

- **`ObserverProjection` appends one `observer.jsonl` line, then forwards.** The line carries
  `"member": "WaveDelivered"`, `waveDir`, `commit` and `covers` (a JSON array, in order), the shape its
  `SuppliedResourcesCommitted` line already uses. `ObserverProjection_AppendsTheDeliveryToObserverJsonl`
  pins it. `guardrails attach`'s replay skips a member it has no case for (the `default` arm in
  `AttachCommand.cs`), so the line is recorded without being replayed live. `AttachCommand.cs` is in no
  task's scope, so leave it alone.
- **`RunEventStream` forwards and appends NO `events.jsonl` row.** DECIDED: `events.jsonl` gains no
  `wave-delivered` kind. `RunEventStream` writes no row for any wave-level event today, and the durable
  record of a delivery is `run.json`'s `waves.<dir>.delivered`. `RunEventStream_AppendsNoEventsRowForADelivery`
  pins it.

**Find them yourself — search, do not trust a list.** Use the Grep tool with the pattern `: IRunObserver`
over `src/Guardrails.Core/` (glob `*.cs`). At authoring time that found `ObserverProjection` and
`RunEventStream`. **If your search returns a different set, trust the search.**

The no-op default on the interface is what let task 11 compile — it is NOT the deliverable. A decorator
inheriting the default silently drops the event, which is the defect this task prevents.

The CLI decorators are a SEPARATE task (13) — do not reach into `src/Guardrails.Cli/`.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/ObserverProjection.cs`, `src/Guardrails.Core/Execution/RunEventStream.cs`, and `src/Guardrails.Core/Execution/IRunObserver.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.

