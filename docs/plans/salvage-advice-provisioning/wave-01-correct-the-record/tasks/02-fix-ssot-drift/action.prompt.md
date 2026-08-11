## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-01-correct-the-record/02-fix-ssot-drift": { "someKey": "someValue" } }`. The harness REJECTS a fragment keyed by
  anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
PR #426 changed `RetryPolicy` but left the SSOT stating the old, ungranted command — the repo's own
rule is that a contract change lands in the SSOT in the SAME change, so this is drift to correct.

In `docs/plans/02-schemas-and-contracts.md`, the retry-salvage description names the SOME-recovery
route as `git checkout <ref> -- <path>` in TWO places (around lines 382 and 574 — grep for
`git checkout <ref>` rather than trusting the line numbers, which move). Change both to
`git show <ref>:<path>`, matching what `RetryPolicy.AppendSalvageSection` actually emits.

Leave the ALL-recovery route (`git apply prior-attempt.patch`) wording alone — wave 4 owns that.
Change nothing else in the document.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`. After
this task completes the harness runs a `git diff` check and rejects any edit outside that path. An
out-of-scope edit fails the task immediately and consumes a retry.
