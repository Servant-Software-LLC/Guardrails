## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-03-provision-what-we-prescribe/02-implement-grant-injection": { "k": "v" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Make task 01's tests pass, and land the contract change in the SSOT in the SAME change (the repo rule).

1. In `src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`, inject **Bash(git show*) and nothing else**,
   UNCONDITIONALLY, alongside the existing unconditional `--add-dir` planDirectory injection. Grep for
   the `--add-dir` marker to find the seam - a sibling wave edited this area, so do not trust a line
   number. Unconditional is deliberate: conditioning on "an attempt that carries a salvage ref" would
   make the effective permission set vary between attempts of the same task, reintroducing exactly the
   nondeterminism this wave exists to remove. Surface what was injected so task 03 can record it.
2. Inject NO write verb, under any condition.
3. In `docs/plans/02-schemas-and-contracts.md`, add ONE normative line to the retry-feedback contract:
   the harness must never present a runnable command the effective permission set does not grant - and
   state that the harness injects the read-only grant its own protocol depends on.

Do NOT edit the authored tests. If they are genuinely wrong, emit {"needsHuman": "<why>"} instead.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/ClaudePromptRunner.cs` and `docs/plans/02-schemas-and-contracts.md`. An
out-of-scope edit fails the task immediately and consumes a retry.
