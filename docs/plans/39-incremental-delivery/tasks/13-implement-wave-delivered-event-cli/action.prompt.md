## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `13-implement-wave-delivered-event-cli`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "13-implement-wave-delivered-event-cli": { "someKey": "someValue" } }`.
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

**Two of the four decorators asserted by a pre-existing test are yours.**
`tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs` requires
`OnTheFlyDiagramObserver` and `OnTheFlyLogSiteObserver` to DECLARE every `IRunObserver` member —
an inherited default does not count. Task 11 turned it red by adding `WaveDelivered`; task 12
closed the Core half; this task closes the Cli half, and until it does the sweep stays red and
the terminal gate fails. It is in no `writeScope` and needs no edit — do not touch the test.

The four Cli `IRunObserver` implementers play TWO different roles — do not treat them alike:
- **DECORATORS** — `OnTheFlyDiagramObserver` and `OnTheFlyLogSiteObserver` — wrap an inner observer
  (`_inner`), and must DECLARE `WaveDelivered` and forward it. They are the Cli types
  `ObserverForwardingSweepTests` hand-lists, and the only Cli types `WaveDeliveredCliForwardingTests`
  asserts forwarding for.
- **RENDERERS** — `ConsoleRunObserver` and `LiveRunObserver` — wrap nothing (no `_inner`). Implement
  `WaveDelivered` on each by rendering the event; there is nothing to forward to.

The member is `void WaveDelivered(Model.WaveNode wave, Journal.WaveDeliveredRecord delivery)`, and the
Scheduler raises it only for a record whose status is `delivered`, after that record is persisted (design
39 §5). A decorator forwards the record INSTANCE unchanged. A renderer prints ONE line naming the wave's
directory, the promoted `Commit` and the `Covers` list, plus the record's `Detail` when it has one (a
delivery that `--merge-on-success` forced past a held decision names the decision it overrode there), and
never infers anything the record does not say. `ConsoleRunObserver_PrintsTheDeliveredWaveAndCommit` and
`LiveRunObserver_PrintsTheDeliveredWaveAndCommit` pin that the line exists and names the wave and the
commit; an empty body passes every forwarding row and fails these two.

- **`ConsoleRunObserver`** writes the line under its lock, the way its `[supplied]` line for
  `SuppliedResourcesCommitted` does.
- **`LiveRunObserver`** adds it as a narrative entry through `AppendNarrative`, the way `WaveFinished` and
  `SuppliedResourcesCommitted` do. Never write to the console beside the live region: a raw write there
  corrupts the table (#145).

The Grep tool with the pattern `: IRunObserver` over `src/Guardrails.Cli/` (glob `*.cs`) lists the four,
and the Grep tool's count mode with the pattern `_inner` on each file shows its role. If the search shows
an implementer not named here, stop and write `{"needsHuman": ...}` rather than guessing its role.
**Your gate runs against `Guardrails.Integration.Tests`**, the only test project referencing
`Guardrails.Cli` — a test of these types cannot compile anywhere else.

**A LogSite caution.** If you render the event on the exported log site, keep the page
byte-identical when no delivery has occurred: `LogSiteHaltBannerTests` pins that page
byte-for-byte, it is in no task's `writeScope`, and a run that delivers nothing must still
produce the output it produces today. This task's `01-tests-pass.ps1` runs `LogSiteHaltBannerTests`
beside `WaveDeliveredCliForwardingTests`, so a change to that page halts here rather than at the plan's
terminal gate.

Forward `WaveDelivered` through both **CLI** decorators, and implement it on both renderers, so
`WaveDeliveredCliForwardingTests` passes.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/ConsoleRunObserver.cs`, `src/Guardrails.Cli/Ui/LiveRunObserver.cs`, `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`, and `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.

