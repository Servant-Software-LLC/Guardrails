## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-02-see-the-failure/02-implement-bash-refusal-detection": { "someKey": "someValue" } }`. The harness REJECTS a fragment keyed by
  anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Make the tests authored by `01-author-tests-bash-refusal-detection` pass.

In `src/Guardrails.Core/Prompts/ClaudePermissionScanner.cs`:
- extend `DenialPhrase` (grep for it; do not rely on a line number) to match the real refusal texts
  `"This command requires approval"` and `"... contains multiple operations. The following part requires
  approval: ..."`;
- stop excluding `Bash` from the tool set that can register a permission wall (grep for
  `WriteFamilyTools`), so a refused Bash command is attributed rather than dropped.

Do NOT edit the authored tests. Make them pass by fixing the scanner. If the authored tests are
genuinely wrong or incompatible, emit `{"needsHuman": "<why>"}` rather than changing them — an
out-of-scope edit to a test file fails the write-scope check and burns a retry.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/ClaudePermissionScanner.cs`. An out-of-scope edit fails the task
immediately and consumes a retry.
