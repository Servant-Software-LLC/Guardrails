## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `09-author-tests-attach-replay`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "09-author-tests-attach-replay": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Create `tests/Guardrails.Integration.Tests/RunEvents/AttachReplayTests.cs`, class **`AttachReplayTests`**,
every test carrying `[Trait("Category", "RunEvents")]`.

The design constraint that decides this whole task: **the attached view must be driven by the real
`LiveRunObserver`, not by a reimplementation of it.** A second table rendering the same data will drift
from the first, and then "the familiar console table" quietly stops being familiar. So the tests drive the
SHIPPED renderer over replayed events - they must not assert against a test double that renders the table
itself.

Pin these test METHOD names:

- `Attach_DrivesTheRealLiveRunObserver_FromObserverJsonl`
- `Attach_ReplaysTheRecordedCallSequence_InOrder`
- `Attach_OnAFinishedRun_ReplaysToCompletion` - replay after the run ended, which is what makes an
  overnight escalation diagnosable
- `TwoConcurrentAttachments_BothReplayEveryEvent_AndNeitherWritesToTheRun` - assert the run's own files
  are unmodified by attaching; a watcher that perturbs the run is worse than no watcher
- `Attach_OnAMissingObserverJsonl_FailsWithAnActionableMessage` - not a stack trace

These MUST COMPILE and FAIL - `guardrails attach` does not exist yet. Do NOT write the command; that is
task 10.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RunEvents/AttachReplayTests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
