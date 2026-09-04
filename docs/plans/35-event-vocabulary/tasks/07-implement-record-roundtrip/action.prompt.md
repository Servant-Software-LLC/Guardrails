## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `07-implement-record-roundtrip`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "07-implement-record-roundtrip": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "07-implement-record-roundtrip": { "someKey": "someValue" },
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

Make the `ObserverRecordRoundTripTests` tests pass. Both halves of the round-trip are yours, because
neither is verifiable without the other.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/ObserverProjection.cs` and `src/Guardrails.Cli/Commands/AttachCommand.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT edit the tests authored upstream.** They are the specification. If one is genuinely wrong,
write `{"needsHuman": "<why>"}` to the state-out path and stop rather than changing it - an
out-of-scope edit to a test file fails the task immediately and consumes a retry.

### 1. `ObserverProjection` - the writer

Flatten the `AttemptRecord` onto the `AttemptFinished` line. **All five `required` members must be on
it** - `Attempt`, `StartedAt`, `EndedAt`, `Outcome`, `LogDir` - plus the optionals the record holds
(`CostUsd`, `Turns`, `NeedsHumanKind`, and the provenance fields). Omitting any of the five makes the
replay throw a `FormatException` that `AttachCommand` swallows and skips, producing a silent, total
loss of attempt replay with a green suite.

Also record `RunFinished` as its own line. This class's documented contract is "record every observed
call, in order"; a decorator that drops a member makes its own doc false.

### 2. `AttachCommand` - the replay

Rebuild the `AttemptRecord` in the `AttemptFinished` case from that line. You will need one new
`RequireDateTimeOffset` helper beside the existing `RequireString` / `RequireInt` / `RequireBool`.

Add **no `case` for `RunFinished`**. The `default:` branch ignores members it does not recognise, and
that is deliberate: an attaching client built against an older harness must not crash on a newer
stream. **Comment that the omission is deliberate**, or the next reader will file it as a bug and
"fix" it.

**Do not weaken the skip-on-`FormatException` behaviour** to make a test pass. It is what keeps attach
forward-compatible. The tests exist to prove the line is complete, not to remove the safety net.

### Done when

All four `ObserverRecordRoundTripTests` tests pass, the existing `AttachReplayTests` still pass, and
the solution builds.
