## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "18-author-tests-endpoint-preflight": { "someKey": "someValue" } }`.
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

Read: **plan sections 6.6 and 7**.

## Task

### What to build

`tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatPreflightTests.cs`, class
**`OpenAiCompatPreflightTests`** (pinned). Tests only, driven against the real `FakeOpenAiServer`.

Reachability lives in the **run preflight**, not in `validate` - `validate` stays static and offline.
The preflight runs once, before the DAG, so an unreachable endpoint halts before a token is spent.

Cover:

- **Reachable** - `GET {endpoint}/models` answers and every declared `model` for that endpoint is in
  the list, so the run proceeds.
- **Unreachable** (refused / DNS / timeout / TLS / 5xx) - **halt**.
- **Model not listed** - halt, naming the per-engine remedy.
- **`GET /models` returns 404 or 405** - a **WARNING, not a halt**: the model-presence assertion is
  skipped and the run proceeds. The distinction is *"the server answered but does not offer this"*
  versus *"there is no server"*, and an engine that serves chat perfectly while omitting the listing
  endpoint must not be locked out by a check that exists to help.
- **The tool-capability probe** (section 6.6 - this is the check that closes the false green): one
  minimal `POST {endpoint}/chat/completions` carrying a single trivial tool whose only correct
  response is to call it. Three outcomes, three tests:
  - a response containing `tool_calls` - **capable**, proceed
  - a 400/422 rejecting `tools` - **halt**, naming block, endpoint, model
  - **a 200 with NO `tool_calls` - halt.** This is the silent case and the entire reason the probe
    exists.
- **Once per (endpoint, model)** - prove it with a connection counter on the fake server, not with a
  counter the preflight increments. Model-level matters: one server can host a model whose template
  emits tool calls and one whose template does not.
- **The zero-cost condition:** a plan declaring NO `openai-compat` block must cost **zero HTTP
  requests**. Prove it the rung-1 way - a loopback listener that **fails the test on ANY accepted
  connection**, never a counter the preflight increments, which would only measure our own bookkeeping.

All must FAIL today. **Do NOT implement it.**

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatPreflightTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
