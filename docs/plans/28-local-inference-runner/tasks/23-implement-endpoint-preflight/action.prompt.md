## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "23-implement-endpoint-preflight": { "someKey": "someValue" } }`.
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

Read: **plan section 7**.

## Task

### What to build

Two deliverables. The second is a re-baseline this task OWNS, and it is the reason
`ProvidersInitAnnotationTests.cs` is in your writeScope.

**1. The preflight.** In the pre-DAG preflight phase (`src/Guardrails.Cli/PlanPreflightPhase.cs` -
plan 26 put sample verification there and this belongs beside it), implement a check that runs **once
per distinct endpoint** before the DAG:

1. `GET {endpoint}/models`, short timeout. Unreachable (refused, DNS, timeout, TLS, 5xx) halts.
   **404/405 downgrades to a WARNING** naming the endpoint, skipping only the model-presence
   assertion.
2. Every declared `model` for that endpoint appears in the list; otherwise halt with the per-engine
   remedy text.
3. **The tool-capability probe**, once per **(endpoint, model)**: one minimal chat completion
   carrying a trivial tool whose only correct response is to call it. Halt on a 400/422 rejecting
   `tools`, and halt on a **200 with no `tool_calls`** - naming the block, endpoint and model, and
   saying that v1's verifier role requires tool calling.

**Discovery is a registry scan.** A plan with no `openai-compat` block must open **zero
connections** - so gate all of the above on there being something to probe, and do not construct an
HTTP client speculatively.

**2. Add `OpenAiCompat` to `PromptRunnerKinds.ModelEnumerable`, and RE-BASELINE the test that pins it
empty.** The listing endpoint is what that member describes, and `providers init` reads it.

`tests/Guardrails.Core.Tests/ModelTiering/ProvidersInitAnnotationTests.cs` currently asserts
**`Assert.Empty(PromptRunnerKinds.ModelEnumerable)`** and **`Assert.Equal(["claude"],
result.UnenumerableKinds)`** inside `Annotate_NeverInventsAModelIdAndStillSucceeds`. Both become false
the moment you add the kind. Update them to the new truth - `ModelEnumerable` contains
`OpenAiCompat`, and the unenumerable set is whatever remains - **without weakening what the test is
actually for**: its subject is that `providers init` NEVER INVENTS A MODEL ID. Keep that assertion
exactly as strong; the structural check that the only new keys are the four solicited ones must
survive verbatim. If you cannot re-baseline it without weakening that guarantee, emit `needsHuman`
rather than loosening it.

This re-baseline is **in scope on purpose**: without it the suite goes red at the terminal gate with
no task able to fix it.

**Do NOT edit `OpenAiCompatPreflightTests.cs`.**

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/PlanPreflightPhase.cs`, `src/Guardrails.Core/Model/PromptRunnerConfig.cs`, `tests/Guardrails.Core.Tests/ModelTiering/ProvidersInitAnnotationTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
