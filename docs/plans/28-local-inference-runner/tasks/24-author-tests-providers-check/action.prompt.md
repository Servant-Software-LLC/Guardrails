## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "24-author-tests-providers-check": { "someKey": "someValue" } }`.
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

Read: **plan section 8**.

## Task

### What to build

`tests/Guardrails.Integration.Tests/OpenAiCompat/ProvidersCheckTests.cs`, class
**`ProvidersCheckTests`** (pinned). Tests only, against the real `FakeOpenAiServer`.

`guardrails providers check <block-name>` is the **manual, opt-in, non-CI** verb that retires
**dialect risk** - the risk no loopback fake can retire, because the fake is one we wrote. It is not
in CI, not in `run`, and not in `validate`; same posture as the existing opt-in real-Claude smoke.

One probe per assumption, each reported **met / unmet / unknown** - the three-way outcome matters,
because "unknown" is an honest answer and collapsing it into "unmet" would make the report lie:

- `stream_options.include_usage` is honoured
- **`tools` are accepted AND actually called** (the assumption MLX is most likely to differ on)
- `num_ctx` is honoured - it is an **Ollama** option and means nothing to MLX, hence "belt, never
  enforcement" in section 6.1
- the model-not-found body shape
- SSE framing
- `reasoning_effort` tolerance
- whether `GET /models` exists at all

Assert the verb exits non-zero only for a genuine failure to REACH the endpoint, never merely because
an assumption came back `unmet` or `unknown` - it is a report, not a gate.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/OpenAiCompat/ProvidersCheckTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
