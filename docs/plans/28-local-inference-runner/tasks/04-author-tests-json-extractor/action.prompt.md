## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "04-author-tests-json-extractor": { "someKey": "someValue" } }`.
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

Read: **plan sections 3.3 and 6.4**.

## Task

### What to build

**1. The stub** `src/Guardrails.Core/Prompts/PromptJsonExtractor.cs` - a static class with the
extraction entry point, every member throwing `NotImplementedException`, so the test project
compiles.

**2. The test file** `tests/Guardrails.Core.Tests/Prompts/PromptJsonExtractorTests.cs`, class
**`PromptJsonExtractorTests`** (pinned - the guardrail filters on it).

The contract, from section 6.4: **the last fenced ```json block; else the last top-level JSON
object; it must parse.** Anything else yields nothing.

Cover at least these behaviours, one `[Fact]` each:

- a bare JSON object (what a strong model emits today) is extracted unchanged - **the
  no-regression case**, because `OverwatchProposal` and the triage sidecar parse strictly today and
  must not get worse;
- prose around a JSON object - the object is recovered (this is the section 3.3 payoff);
- a fenced ```json block with prose before and after - the fenced block wins;
- **two** fenced blocks - the LAST one wins;
- a fenced block AND a later bare object - the plan says the fenced block is tried first; pin
  whichever the plan specifies and cite it in a comment;
- malformed JSON - nothing is extracted (it must FAIL CLOSED, never a partial or a guess);
- no JSON at all - nothing is extracted.

All of these must FAIL against your throwing stub.

**Do NOT implement the extractor, and do NOT touch `OverwatchProposal` or `NeedsHumanTriage`.**

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Prompts/PromptJsonExtractorTests.cs`, `src/Guardrails.Core/Prompts/PromptJsonExtractor.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
