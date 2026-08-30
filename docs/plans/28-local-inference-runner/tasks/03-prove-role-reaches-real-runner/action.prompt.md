## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's 03-prove-role-reaches-real-runner NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "03-prove-role-reaches-real-runner": { "someKey": "someValue" } }`.
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

Read: **plan sections 3.4 and 9**.

## Task

### What to build

The seam-ledger row this plan owes (#382), and the reason it exists: **every assertion in
`PromptRoleSeamTests` captures its `PromptInvocation` at a FAKED `IPromptRunner`.** That fake stands
in for `ClaudePromptRunner`, which is an **external-resource adapter** - it spawns the `claude` child
process. The ledger classifies that seam **E**, and an E row owes a proof that drives the REAL
adapter with the **process** boundary faked underneath it. Without this task, all seven role tests
could pass while the role never survives the trip to the thing that actually runs.

Write `tests/Guardrails.Integration.Tests/RoleSeam/RoleReachesRealRunnerTests.cs`, class
**`RoleReachesRealRunnerTests`** (pinned - the guardrail filters on it). One test is enough:

- drive the **real `GuardrailRunner`** with the **real `ClaudePromptRunner`**, over a **stub `claude`
  binary**. This repo already does exactly this - read `FakeClaudePlanBuilder` and
  `ClaudePromptRunnerStreamLogTests` first and reuse that fixture rather than inventing one;
- assert the invocation that reaches the real runner carries `PromptRole.Guardrail`.

Assert an effect **only the production path emits** - what the real runner actually received or
wrote. *"The collaborator was called"* is NOT an assertion, and a recording double satisfying it is
precisely the passing-but-blind shape this task exists to close.

This task writes **one test file and nothing else**. It changes no production code: task 02 already
assigned the roles. If the test fails, the finding is that the role does not travel - report it with
`needsHuman` rather than editing production to make your own test pass.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RoleSeam/RoleReachesRealRunnerTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
