## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author xUnit.v3 tests in the single new file
`tests/Guardrails.Integration.Tests/AiMergeWorkerTests.cs` (class name exactly `AiMergeWorkerTests`,
selected via `--filter "FullyQualifiedName~AiMergeWorkerTests"`). Encode plan 08 §4 / §9.1 / Stage-2
BEFORE the AI-merge worker exists. Build a FAKE AI runner (behind `IPromptRunner`) that writes canned
bytes to `GUARDRAILS_MERGE_OUT`, plus a malicious out-of-bounds writer and a hunk-dropper:
- **byte-producer + merge-env-contract:** the worker receives `GUARDRAILS_MERGE_BASE/_OURS/_THEIRS` on
  disk, writes the resolution to `GUARDRAILS_MERGE_OUT`, and the harness reads `_OUT` (NOT `PromptResult`
  bytes - there are none). `PromptResult.IsError` / exit code is NEVER read as a verdict.
- **(i)** a conflict the AI resolves cleanly (no markers, in-bounds) → re-verify is the verdict, the
  union settles.
- **(ii)** an AI resolution that writes a file OUTSIDE the git-reported-conflicted set → detected
  (blast-radius via `git status --porcelain`), discarded (`reset --hard`), needs-human - the cross-file
  clobber, mechanically closed.
- **(iii)** an AI resolution that leaves a conflict marker → detected (`git diff --check`), needs-human.
- **(iv)** budget exhausted after 1 retry → needs-human.
- **ai-deleted-hunk → colliding-sibling re-verify catches it (B-3, load-bearing):** the AI resolves by
  DROPPING a colliding sibling's source hunk (no markers, in-bounds, bytes compile, the merging task's
  own guardrails pass). Assert the union is CAUGHT because the colliding sibling's FULL guardrail set
  re-runs UNCONDITIONALLY - including a sibling `local` guardrail whose file the merge did NOT touch;
  assert that with a touched-files local-skip wrongly applied to the sibling, this test FAILS.

These reference the not-yet-existing worker + merge env contract, so the project will not compile against
current code - that is the intended "fails on current code" signal. Do NOT implement the worker - tests
only, in this one file. Publish nothing to state.
