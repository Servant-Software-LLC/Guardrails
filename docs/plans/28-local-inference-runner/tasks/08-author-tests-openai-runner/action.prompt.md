## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-author-tests-openai-runner": { "someKey": "someValue" } }`.
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

Read: **plan sections 3.5, 4, 6.1, 6.2, 6.4, 6.6 and 8**.

## Task

### What to build

The largest authoring task in this plan: **three test classes and the full runner stub they compile
against**, so the three implementation tasks that follow each have their own filtered, already-red
test class.

**1. The config surface** (`Model/PromptRunnerConfig.cs`, `Loading/RawManifests.cs`) - add the
section 4 keys as optional properties: `endpoint`, `contextTokens`, `apiKeyEnv`, `wire`, `engine`.
Add `ServesRoles`, `NeedsContainmentHook` and `WritesFiles` to `PromptRunnerKinds` as **throwing or
minimal stubs** in the same shape as the existing `Implemented` / `ModelEnumerable` members. Add
`ContextOverflow` to `PromptFailureKind`.

**2. The runner stub** `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs` - the class,
implementing `IPromptRunner`, every member throwing `NotImplementedException`. It must make all three
test classes COMPILE. It must not be registered anywhere yet.

**3. Three test classes**, each pinned by name because each implementation task filters on exactly one:

- **`OpenAiCompatTransportTests`** (task 09) - request shape, SSE streaming, `usage` handling, the
  `runner-notice` disclosure line, and the failure taxonomy. From section 8 and 6.2: a truncating
  server fails the attempt; a server omitting `usage` yields `Usage = null` and NEVER `{0, 0}`; 404
  is `Error` and 429 is `Transient`, **each proven by the pause that did or did not happen**, not by
  reading a classification; `finish_reason: "length"` is `OutputCap`. Section 6.1: an over-long
  prompt is refused BEFORE the request, and a request that grows past the window on turn three is
  refused ON TURN THREE (recompute per turn, not once at entry). Streaming is proven by the stream
  log growing BEFORE the response completes.

- **`OpenAiCompatToolLoopTests`** (task 10) - the read-only tools named exactly `Read`, `Glob` and
  `Grep` (the harness's own supervisory prompts name those strings verbatim - see `Overwatch.cs` and
  `NeedsHumanTriage.cs`); a tool call outside both roots is refused by `PromptToolContainment` and
  the refusal counts toward the denial bound; three refusals in a row fires the abort with the
  refused paths; `allowedTools` NARROWS the offered set when it names any of the three.
  **And the section 6.6 case, which is the most important test in this plan:** a server that accepts
  `tools`, calls NONE, and returns a well-formed `{"pass": true}` must FAIL the attempt for a
  `Guardrail`-role invocation - and the assertion is that **no verdict file containing `pass: true`
  exists**, not merely that an error was raised. An `Advisory` invocation that calls no tool must
  still SUCCEED (the rule is role-scoped; a test that omits this would ship a rule breaking every
  advisory path).

- **`OpenAiCompatVerdictTests`** (task 11) - verdict transcription via `PromptJsonExtractor`: prose
  around a valid object recovers it; a ```json block that is not the last block loses to the last
  one; prose with no JSON writes **NO FILE AT ALL**. The role gate: an `Action` invocation is
  REFUSED, `Guardrail` and `Advisory` are served - and `ServesRoles` is pinned **BY CONSTRUCTION**
  (build the real runner for each kind-by-role pair and assert it accepts or refuses), never by
  reading the same field the runner reads. An invocation with empty `StreamLogPath`,
  `WorkingDirectory` and `PlanDirectory` completes without crashing (section 6.5).

Drive every one of these against the **real `FakeOpenAiServer`** from task 05 over a real loopback
socket. Each test must assert an effect **only the production implementation emits** - the verdict
file's bytes, the stream log on disk, the pause that happened. *"The seam was called" is not an
assertion*; that is exactly how the bugs this plan cites shipped green.

Every test in all three classes must FAIL against the throwing stub. **Do NOT implement the runner.**

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatTransportTests.cs`, `tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatToolLoopTests.cs`, `tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatVerdictTests.cs`, `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs`, `src/Guardrails.Core/Prompts/PromptFailureKind.cs`, `src/Guardrails.Core/Model/PromptRunnerConfig.cs`, `src/Guardrails.Core/Loading/RawManifests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
