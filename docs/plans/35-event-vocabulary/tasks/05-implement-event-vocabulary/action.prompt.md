## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `05-implement-event-vocabulary`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "05-implement-event-vocabulary": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "05-implement-event-vocabulary": { "someKey": "someValue" },
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

Make the `RunEventVocabularyTests` tests pass by widening the `events.jsonl` writer.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/RunEventStream.cs` and `src/Guardrails.Cli/Commands/RunCommand.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT edit the tests authored upstream.** They are the specification. If one is genuinely wrong,
write `{"needsHuman": "<why>"}` to the state-out path and stop rather than changing it - an
out-of-scope edit to a test file fails the task immediately and consumes a retry.

**Your `RunCommand.cs` edit is ONE line and nothing else:** the `new RunEventStream(...)` construction
inside `BuildObserverChain`, which must now pass `runId` (that method already has it as a parameter).
Raising `RunFinished` from `RunCommand` is task 11 - do not attempt it here, and do not restructure the
try/finally.

### What to change in `RunEventStream`

1. **`runId` becomes a constructor parameter**, replacing the
   `Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))` derivation. The composition root
   already holds the real run id; deriving it from a directory name is a silent coupling that a test
   whose runId happens to equal the directory name cannot detect.

2. **`seq` on EVERY row**: monotonic, 1-based, per-process, assigned **inside** `lock (_gate)` -
   and **move the `At` stamp inside that lock too**. Both are ordering-relevant and both are currently
   built outside it.

3. **The `run-finished` row**, carrying `exitCode` and `faultKind`. It is the only kind with **no
   `taskId`**, so `EventRow.TaskId` becomes `required string?`. **Keep `required`**: dropping it would
   let a future kind omit `taskId` silently, which `JsonIgnoreCondition.WhenWritingNull` makes
   indistinguishable from a legitimately run-scoped row.

4. **The widened `attempt-finished` row.** Each field names its `TelemetryRow` twin verbatim:

   | Row field | `TelemetryRow` | From the record |
   |---|---|---|
   | `costUsd` | `CostUsd` | `record.CostUsd` |
   | `turns` | `Turns` | `record.Turns` |
   | `model` | `Model` | `record.Provenance?.Model` |
   | `tier` | `Tier` | `record.Provenance?.Tier` |
   | `runner` | `Runner` | `record.Provenance?.Runner` |
   | `startedAt` | `StartedAt` | `record.StartedAt` |
   | `endedAt` | `EndedAt` | `record.EndedAt` |
   | `needsHumanKind` | *(none - journal-owned)* | `record.NeedsHumanKind` |

   Do **not** add `elapsedSeconds` or `attemptsMax`, and do **not** substitute a value when the record
   holds none - a row omitting `model` because the journal has no provenance is correct and honest.

5. **Update the class doc**: extend the "Emitted kinds" list with `run-finished`, and **delete the
   now-false paragraph** saying run-level bracketing is not here.

### Done when

Every `RunEventVocabularyTests` test passes, the solution builds, and the existing `Category=RunEvents`
tests still pass - the additive kinds landed earlier must keep their exact shape.
