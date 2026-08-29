## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `04-implement-failure-classifier`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "04-implement-failure-classifier": { "someKey": "someValue" } }`.
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

Fill real logic over the stub in `src/Guardrails.Core/Telemetry/TelemetryFailureClassifier.cs` so that
`tests/Guardrails.Core.Tests/Telemetry/TelemetryFailureClassifierTests.cs` passes. Read that test file
first; it is the specification.

**Do NOT edit the authored tests.** If one is genuinely wrong, write
`{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` rather than changing it.

**This task's real work is evidence-gathering, and it must happen before you write a pattern.** The
feedback wording has drifted across harness releases, so a pattern derived from today's source alone
will silently mis-bucket older runs — which is the exact defect the classifier exists to prevent, one
level up.

1. **Read the producers.** `RetryPolicy.ForWriteScopeViolation` and
   `RetryPolicy.ForHarnessWriteOutOfScope` in `src/Guardrails.Core/Execution/RetryPolicy.cs`, and the
   staging-move branch at `src/Guardrails.Core/Execution/TaskExecutor.cs:975`. These are today's
   wording.
2. **Survey the historical evidence.** Real `feedback.md` files exist under this repo's own plan log
   sites — `docs/plans/*/logs/**/attempt-*/feedback.md`. Grep them for the write-scope and
   out-of-scope wordings and see how many distinct phrasings actually occur. Use `git log` on
   `RetryPolicy.cs` if you need to know when a wording changed.
3. **Derive one pattern per failure kind that covers every generation you found**, and write a comment
   beside each naming the generations it covers and the sample you verified it against. A reader six
   months from now needs to know whether a new wording is covered or merely unseen.

**Rules the implementation must hold:**
- **Anchor on a USE, not a mention.** The pattern must match the feedback's own structural marker (the
  line the retry policy emits), not the bare words "write scope" — a prose sentence that merely
  mentions write scope must not classify as a violation.
- **Unrecognized wording is `undifferentiated`, and so is a missing log site.** Never fall back to the
  most likely kind. An attempt we cannot classify is data we do not have, and recording it as a guess
  is worse than recording it as unknown: it is wrong in the direction that looks fine.
- **A non-empty `failedGuardrails` short-circuits** — that is a genuine guardrail failure and no file
  needs reading.
- Reading a `feedback.md` must never throw on a locked, unreadable or half-written file; treat any read
  failure as `undifferentiated`.

State in your state-out fragment how many distinct historical wordings you found and which generations
your patterns cover — the next phase's first-class outcome value is justified by that number.
