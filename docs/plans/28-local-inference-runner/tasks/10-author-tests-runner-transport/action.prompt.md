## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's 10-author-tests-runner-transport NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "10-author-tests-runner-transport": { "someKey": "someValue" } }`.
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

Read: **plan sections 4, 6.1, 6.2, 6.3 and 8**.

## Task

### What to build

`tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatTransportTests.cs`, class
**`OpenAiCompatTransportTests`** (pinned - task 11 filters on it). Tests only.

Cover the request shape, SSE streaming, `usage` handling, the `runner-notice` disclosure line, and
the failure taxonomy. From sections 8 and 6.2:

- a truncating server fails the attempt (`ContextOverflow`);
- a server omitting `usage` despite `include_usage` yields `Usage = null` and **NEVER `{0, 0}`**;
- **404 is `Error` and 429 is `Transient`, each proven by the pause that did or did not happen** -
  never by reading a classification back. A 404 misclassified as `Transient` would burn the 4h
  transient budget waiting for a human action no waiting produces, then settle `rate-limited`, a
  diagnosis that is false;
- `finish_reason: "length"` is `OutputCap`;
- section 6.1, both halves: an over-long prompt is refused BEFORE the request, and a request that
  grows past the window **on turn three** is refused ON TURN THREE - the estimate is recomputed per
  turn over the bytes actually about to be sent, not once at entry;
- streaming is proven by the **stream log growing BEFORE the response completes**.

Drive these against the **real `FakeOpenAiServer`** (task 06) over a real loopback socket. The seam
under test is the OpenAI HTTP wire, so faking that boundary is correct and expected - do NOT
substitute an in-process double for the runner itself. The runner stub and the config surface already
exist (task 09), so everything here compiles.

Each test must assert an effect **only the production implementation emits** - the verdict file's
bytes, the stream log on disk, the pause that happened. *"The seam was called" is not an assertion*;
that is exactly how the bugs this plan cites shipped green.

Every test must FAIL against the throwing stub. **Do NOT implement the runner.**

### Land the constructor you need, then test against it (added after this task halted)

The first attempt at this task correctly refused to proceed: `OpenAiCompatPromptRunner` had exactly one
constructor, `OpenAiCompatPromptRunner(string name)`, so no test could point a real runner instance at a
specific `FakeOpenAiServer.Endpoint`. There is no other channel - `PromptInvocation` carries none of the
five `openai-compat` keys, and they live only on `PromptRunnerConfig`, which
`IPromptRunner.RunAsync(PromptInvocation, CancellationToken)` never sees.

So `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs` is now in your writeScope, for **the
constructor signature and nothing else**. This is the pattern task 07 already used in this plan: it
carried `PromptToolContainment.cs` alongside its own tests so it could land the real signature its tests
compile against, and it went green on the first attempt.

**What you may change in that file:**

- Widen the constructor so a runner can be handed its endpoint and the rest of its block config. Mirror
  `ClaudePromptRunner`, whose constructor already takes its config-derived collaborators
  (`ClaudePromptRunner(string name, string command, ProcessRunner processRunner)`) - the natural shape
  here is to accept the `PromptRunnerConfig` (or the five keys it carries) plus whatever transport
  collaborator the tests must substitute.
- Keep `Name` behaving exactly as it does now.

**What you must NOT change in that file:**

- Do not implement `RunAsync`. Leave it throwing `NotImplementedException`. Task
  `11-implement-runner-transport` owns the transport, and your tests are supposed to be RED against this
  stub - that red bar is this task's deliverable, exactly as task 07's tests were red against
  `PromptToolContainment`'s unimplemented body.
- Do not add behavior, parsing, HTTP calls, or logging. Signature and field storage only.

If you find you need a second production file to write these tests, do NOT reach for it: write
`needsHuman` naming the file and the exact symbol you need, which is what got this task unblocked the
first time.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatTransportTests.cs`, `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
