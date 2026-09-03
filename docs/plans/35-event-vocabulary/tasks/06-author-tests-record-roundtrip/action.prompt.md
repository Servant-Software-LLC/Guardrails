## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `06-author-tests-record-roundtrip`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "06-author-tests-record-roundtrip": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "06-author-tests-record-roundtrip": { "someKey": "someValue" },
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

Author the round-trip tests for `observer.jsonl`. This is the single most important test file in the
plan, because the defect it catches is **completely silent**.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RunEvents/ObserverRecordRoundTripTests.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Class name is pinned: **`ObserverRecordRoundTripTests`**, every test carrying
`[Trait("Category", "RunEvents")]`. This task's guardrails filter on that class name.

### Why this file exists - read this before writing a line

`Journal.AttemptRecord` has **five `required` members**: `Attempt`, `StartedAt`, `EndedAt`, `Outcome`,
`LogDir`. If `ObserverProjection`'s flattened line omits any one of them, the attach replay throws
`FormatException` while rebuilding the record - and `AttachCommand` **catches that and SKIPS the line**,
by design, so it stays forward-compatible with members it does not recognise.

The result is `guardrails attach` replaying a run in which **no attempt ever finished**: no exception,
no log line, no failing test, exit code 0. **Every other assertion in this plan still passes in that
state.** This round-trip is the only thing that catches it.

**Therefore: asserting `guardrails attach` exits 0 proves NOTHING here.** It exits 0 when every line
was skipped. Read `tests/Guardrails.Integration.Tests/RunEvents/AttachReplayTests.cs` first for the
established idiom (`ScriptPlanBuilder`, `RunToCompletionAsync`, `InvokeAsync("attach", ...)`), then
assert on what attach actually **rendered to stdout** - the replayed attempt must be visible in the
output. `AttachCommand` has no public replay method, so its rendered output is the observable surface.

### The behaviours - these exact method names

**1. `ObserverLine_CarriesEveryRequiredAttemptRecordMember`**
Drive a real `ObserverProjection` with a fully-populated `AttemptRecord`, read back the
`AttemptFinished` line, and assert **all five required members are present on it** - by name, one
assertion each, so a failure says which one is missing. Also assert the optionals the record held
(`costUsd`, `turns`, and the provenance fields) survived.

**2. `AttachReplaysTheAttempt_RatherThanSilentlySkippingIt`**
Write an `observer.jsonl` whose `AttemptFinished` line was produced by the **real
`ObserverProjection`** (not a hand-written fixture - a hand-written line cannot detect that the
producer omits a field). Run `guardrails attach` over it and assert its **stdout shows the replayed
attempt**. Then assert the negative control: a line with one required member removed must NOT show it.
Without that second half the test cannot distinguish replay from skip.

**3. `AttachReplay_ReconstructsTheRecordFields`**
Assert the values that came back are the values that went in - at minimum the attempt number and the
outcome, read off attach's rendered output.

**4. `RunFinishedIsRecordedOnTheObserverStream`**
`ObserverProjection`'s documented contract is "record every observed call, in order". Assert a
`RunFinished` call produces a line. A decorator that drops a member makes its own doc false - that is
the reason, not any need of the attach renderer.

### Done when

The integration test project **compiles** and all four tests **fail**. Failing is intentional; not
compiling is a mistake to fix. Do NOT change `ObserverProjection` or `AttachCommand` - that is task 07.
