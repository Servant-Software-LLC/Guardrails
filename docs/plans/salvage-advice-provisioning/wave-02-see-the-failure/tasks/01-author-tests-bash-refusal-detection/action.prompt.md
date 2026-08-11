## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-02-see-the-failure/01-author-tests-bash-refusal-detection": { "someKey": "someValue" } }`. The harness REJECTS a fragment keyed by
  anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
The harness is blind to Bash refusals: in one real run **86 refused git calls were detected as ZERO
permission walls**. `ClaudePermissionScanner.DenialPhrase` matches none of the real refusal texts, and
`WriteFamilyTools` excludes `Bash` outright.

Create `tests/Guardrails.Core.Tests/ClaudePermissionScannerBashRefusalTests.cs` with xUnit v3 tests that
FAIL against the current scanner and will pass once it detects Bash refusals. Pin the refusal strings
VERBATIM — they are transcript-sourced, so a future phrasing change must break a test, not silently
re-blind the harness:

- `"This command requires approval"`
- `"This Bash command contains multiple operations. The following part requires approval: git restore --staged src/Foo.cs"`

Cover: (a) each phrase is recognised as a denial; (b) a refusal on a `Bash` tool-use is attributed as a
permission wall (today `Bash` is excluded from the write-family set); (c) an ordinary non-refusal Bash
result is NOT flagged (no false positives).

The tests MUST COMPILE and FAIL — `ClaudePermissionScanner` already exists, so failing means the
detection is missing, which is the intended red. Do NOT implement the detection.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ClaudePermissionScannerBashRefusalTests.cs`. After this task completes the
harness runs a `git diff` check and rejects any edit outside that path — including
`src/Guardrails.Core/Prompts/ClaudePermissionScanner.cs`, which the implementation task owns. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by
a missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to
the state-out path and stop.
