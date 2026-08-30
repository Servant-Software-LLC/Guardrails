## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-implement-runner-transport": { "someKey": "someValue" } }`.
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

Read: **plan sections 4, 6.1, 6.2 and 6.3**.

## Task

### What to build

Make **`OpenAiCompatTransportTests`** pass, and only that class. The other two runner test classes
stay red until tasks 10 and 11 - that is expected, and your guardrail filters to your own class.

Implement, inside `OpenAiCompatPromptRunner`:

- **The request.** POST to `{endpoint}/chat/completions` with `model`, `messages`, `stream: true`,
  `stream_options.include_usage`, `max_tokens` from `maxOutputTokens`, `reasoning_effort` from
  `effort`. Merge the `wire` map verbatim as a body passthrough - it may never override a
  harness-owned field (that is a validation error in task 15, but the class refuses too, as backstop).
  `apiKeyEnv` names an ENV VAR holding a bearer token; it never holds a secret itself.
- **Streaming**, required - the log viewer tails the stream file and a judge showing a dead file for
  ten minutes is the healthy-slow-vs-stuck ambiguity the operator work exists to remove. Honour
  `StallBound` if set (no current call site sets one; it is a contract, and a runner that can only
  honour it by being rewritten is one that will not be).
- **The `runner-notice` disclosure** - a synthetic first JSON object into `StreamLogPath` naming
  every declared setting this runner IGNORES or NARROWS, before the first wire request. When
  `StreamLogPath` is empty, write no notice (section 6.5).
- **`usage`** - real counts when reported; `Usage = null` plus a `runner-notice` line when absent.
  **Never `{0, 0}`.** `CostUsd` is `null` - there is no pricing table.
- **The context bounds (section 6.1), both halves.** Refuse before sending when
  `ceil(chars / 3) + maxOutputTokens > contextTokens` - pessimistic on purpose, `/3` not `/4` -
  computed over the bytes ACTUALLY about to be sent **on EVERY turn**, including accumulated tool
  results, not over the composed prompt once at entry. After the response, compare
  `usage.prompt_tokens` against the optimistic `floor(chars / 4)`; fewer means the server truncated
  and the attempt fails. New `PromptFailureKind.ContextOverflow`, with actionable feedback and no
  auto-escalation - there is nothing the harness can raise.
- **The failure taxonomy (section 6.2)** through this class's OWN signal table, never Claude's:
  DNS/refused/reset/TLS and 429/503/529 are `Transient`; **model-not-found (404) is `Error`, never
  `Transient`** (a pause waits for a human action no waiting produces, would burn the 4h transient
  budget and settle `rate-limited`, a diagnosis that is false); 401/403 is `Error` naming
  `apiKeyEnv` and whether it was set; a 400 rejecting `tools` is `Error` naming block, endpoint and
  model.

The model-not-found remedy text is per-engine, selected by the optional `engine` hint, defaulting to
a neutral sentence naming the model and the endpoint. **`engine` is operator-facing text ONLY** - it
must never select a code path, change a request, or appear in any logic. A plan configured for MLX
and one configured for Ollama must emit **byte-identical requests** for the same model, wire and
prompt.

**Do NOT edit any test file** - all are outside your writeScope.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
