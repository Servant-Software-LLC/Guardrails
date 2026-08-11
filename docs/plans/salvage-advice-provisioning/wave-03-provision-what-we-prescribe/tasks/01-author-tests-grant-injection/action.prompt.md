## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-03-provision-what-we-prescribe/01-author-tests-grant-injection": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
The harness prescribes a retry-salvage protocol naming `git show`, but never provisions it — it depends
on a plan author remembering #252 and on the operator's own settings file. Direct precedent for fixing
that: `ClaudePromptRunner` ALREADY injects `--add-dir <planDirectory>` unconditionally so the agent can
reach `prior-attempt.patch`.

Create `tests/Guardrails.Core.Tests/ToolGrantInjectionTests.cs` (xUnit v3) asserting:

1. `Bash(git show*)` is present in the tool grants the runner passes, UNCONDITIONALLY — including when
   the plan's own allowedTools contains no git at all, and when it contains an unrelated set.
2. It is injected exactly once (no duplicate when the plan already grants it).
3. NO write verb is ever injected — assert `git restore`, `git checkout`, `git reset`, `git apply` and
   `git stash` are absent from the injected additions. This negative is load-bearing: the decision of
   record is read-only ONLY.
4. The runner SURFACES what it injected (so provenance can record it), rather than silently mutating
   the list.

Grep for the existing `--add-dir` injection in `src/Guardrails.Core/Prompts/ClaudePromptRunner.cs` to
find the seam. Do NOT cite a line number - a sibling wave has already edited this area, so line numbers
have moved; use the durable marker instead.

## Also write the MINIMAL stub the tests compile against

Assertion 4 needs a way to READ what the runner injected, and that surface does not exist yet - so a
test naming it would not compile, and a TDD red must COMPILE and fail, never fail to build. You
therefore also write the **minimal skeleton** for it in
`src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`: just enough surface for the tests to compile -
a member that throws `NotImplementedException` or returns `default`. **Write no real logic**: the
injection itself is task 02's deliverable, and it fills real behaviour over your stub.

These tests MUST COMPILE and FAIL against that stub. Do NOT implement the injection.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ToolGrantInjectionTests.cs` and
`src/Guardrails.Core/Prompts/ClaudePromptRunner.cs` (the stub only). After this task completes the
harness runs a `git diff` check and rejects any edit outside those paths. An out-of-scope edit fails the
task immediately and consumes a retry. If a missing symbol in a file you do NOT own blocks compilation,
do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
