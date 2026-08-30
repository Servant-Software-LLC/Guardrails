## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-implement-shared-json-extractor": { "someKey": "someValue" } }`.
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

Read: **plan section 3.3**.

## Task

### What to build

**1. Implement `PromptJsonExtractor`** so `PromptJsonExtractorTests` goes green: last fenced ```json
block, else the last top-level JSON object, must parse, otherwise nothing.

**2. Route BOTH existing strict consumers through it** - this is the deliverable that makes section
3.3 a payoff rather than a claim:

- `src/Guardrails.Core/Execution/OverwatchProposal.cs` - today `JsonDocument.Parse(resultText)` on
  the WHOLE message, then requires an object with a string `diagnosis`. Parse via the extractor
  instead, then apply the same shape requirement.
- `src/Guardrails.Core/Execution/NeedsHumanTriage.cs` - the sidecar writer, same shape, same change.

This only ever WIDENS what parses on paths that **fail closed today**, so it cannot make a Claude
run worse. Keep every downstream behaviour identical: a message that parses today must still parse
to the same value, and one that yields nothing must still yield nothing (the advisory
`RecordNoVerdict` path is unchanged).

**Do NOT edit `PromptJsonExtractorTests.cs`** - it is outside your writeScope.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Prompts/PromptJsonExtractor.cs`, `src/Guardrails.Core/Execution/OverwatchProposal.cs`, `src/Guardrails.Core/Execution/NeedsHumanTriage.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
