## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "26-author-tests-judge-spend": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements part of `docs/plans/28-local-inference-runner.md`. READ THE SECTION(S) NAMED BELOW before you start -
the plan carries the reasoning, the rejected alternatives, and the exact file:line evidence.
Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

Read: **plan section 11 finding 3**.

## Task

### What to build

`tests/Guardrails.Core.Tests/Journal/JudgeSpendRecordingTests.cs`, class
**`JudgeSpendRecordingTests`** (pinned). Tests only.

Today `grep "CostUsd\|Usage" GuardrailRunner.cs` returns **nothing** - judge cost is in neither
`JournalCost.Total` nor `OverheadCostUsd`. A verifier-only v1 with no judge measurement is
unfalsifiable, which disqualifies a plan whose thesis is that measurement decides the v2 bets.

Pin both halves:

1. **Judge `costUsd` and `usage` reach `AttemptJudge` provenance and the per-tier report** - read
   from `run.json`'s BYTES, not from the in-memory `PromptResult` object. A runner reporting no cost
   records `null`, never `0`.
2. **`JournalCost.Total` is provably UNCHANGED by their presence.** This is the load-bearing test.
   Folding judge spend into the total would make `maxCostUsd` trip earlier on every existing Claude
   run and change the `--autonomous` brake's behaviour - a semantic change to the liveness floor,
   shipped inside a local-inference plan. The two numbers are deliberately separate: the total is
   **actor spend**, the judge column is **verifier spend**.

Both must FAIL today. **Do NOT implement it.**

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Journal/JudgeSpendRecordingTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
