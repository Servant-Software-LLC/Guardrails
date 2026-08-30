## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-add-openai-block-config-surface": { "someKey": "someValue" } }`.
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

Read: **plan sections 3.5, 4 and 6.2**.

## Task

### What to build

The declarative surface every later task in this plan builds on. Split out of the runner
test-authoring task because it is a separate deliverable that task 18 also needs, and bundling them
gave that task the structural over-scope fingerprint.

**1. The block keys** (`Model/PromptRunnerConfig.cs`, `Loading/RawManifests.cs`) - add the section 4
keys as OPTIONAL properties, each absent (`null`) by default so no existing `guardrails.json`
changes by a byte:

- `endpoint` - absolute http/https base URL
- `contextTokens` - integer >= 1
- `apiKeyEnv` - the NAME of an env var holding a bearer token, **never the token itself** (this file
  is committed and hashed into `PlanDefinitionHash`, which keys the review attestation)
- `wire` - a verbatim request-body passthrough map
- `engine` - **operator-facing text ONLY** (`ollama` | `llama.cpp` | `mlx` | `lm-studio` | `vllm`).
  It selects the remedy SENTENCE in an error and nothing else. It must never select a code path,
  change a request, or appear in any logic - see the plan's section 3.1.

**2. `PromptFailureKind.ContextOverflow`** - the exact mirror of the shipped `OutputCap` on the other
side of the same window. Every consumer switch already has a `_` default, so the member is additive.

**3. The kind-fact stubs** on `PromptRunnerKinds`, in the same shape as the existing `Implemented` /
`ModelEnumerable` members: `ServesRoles`, `NeedsContainmentHook`, `WritesFiles`. Task 12 fills them
in; here they exist so later code compiles against them.

**4. The runner class stub** `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs` - the class
implementing `IPromptRunner`, every member throwing `NotImplementedException`. It must NOT be
registered anywhere yet.

**5. The round-trip test** `tests/Guardrails.Core.Tests/ModelTiering/OpenAiCompatConfigShapeTests.cs`,
class **`OpenAiCompatConfigShapeTests`** (pinned - the guardrail filters on it). Assert a
`guardrails.json` carrying the new keys LOADS and the values land on the block, and that a config
omitting them loads with each absent as null. **No TDD split here** - this is a data model plus its
loader binding, so the property declaration IS the implementation and there is no behavioural stub to
be red against (the plan-breakdown collapse rule, reason stated).

**Do NOT implement any runner behaviour** - that is tasks 11-15.

**Binding the five keys is part of THIS task (added after the first run halted here).** Declaring the
properties on `PromptRunnerConfig` and `RawManifests` is only half the job: the ONLY code that maps a
deserialized `RawPromptRunner` onto a `PromptRunnerConfig` is `PlanLoader.BuildRunnerConfig`
(`src/Guardrails.Core/Loading/PlanLoader.cs`), and it binds every field by an explicit named-property
assignment - there is no reflection and no extension point. Without five one-line assignments there, the
keys deserialize and then read back null forever, which is exactly what guardrail
`02-config-shape-tests-pass` exists to catch. `PlanLoader.cs` is in your writeScope for that and nothing
else: touch `BuildRunnerConfig` only, and leave the frontmatter reader alone (that is task 21's).

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Model/PromptRunnerConfig.cs`, `src/Guardrails.Core/Loading/RawManifests.cs`, `src/Guardrails.Core/Loading/PlanLoader.cs`, `src/Guardrails.Core/Prompts/PromptFailureKind.cs`, `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs`, `tests/Guardrails.Core.Tests/ModelTiering/OpenAiCompatConfigShapeTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
