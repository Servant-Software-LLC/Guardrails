## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "18-author-tests-block-diagnostics": { "someKey": "someValue" } }`.
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

Read: **plan sections 4 and 7**.

## Task

### What to build

`tests/Guardrails.Core.Tests/Loading/OpenAiCompatDiagnosticsTests.cs`, class
**`OpenAiCompatDiagnosticsTests`** (pinned). Tests only.

`validate` stays **static and offline** - plan 26 just ruled that making it execute things is a
semantic change, and a network probe is the same violation. Everything here is readable from
`guardrails.json` alone.

**GR2065 (error)** - one test per clause:
- `endpoint` missing, or not an absolute http/https URL
- `model` missing
- `contextTokens` missing, or `< 1`
- a `wire` map overriding a harness-owned request field (`model`, `messages`, `stream`,
  `stream_options`, `tools`, `max_tokens`) - `wire: {"stream": false}` is the exact typo that would
  silently disable streaming
- any of the new keys on a block whose `kind` is NOT `openai-compat` - a key that does nothing where
  it was written is indistinguishable from one that works

**GR2067 (warning)** - two forms:
- an `openai-compat` block declaring no `strength`. This matters mechanically:
  `TierResolver.IsWeakVerifier` treats a null-strength non-Claude block as **permanently weak**, so
  every judge on it carries an advisory forever, and advisories that always fire stop being read.
- an `openai-compat` block that is **unreachable** - neither pinned nor a reserved profile name. This
  catches `triage` written for `ai-triage`, which otherwise fails silently.

**GR2009 becomes kind-aware** - assert `claude` still gets the PATH probe and `openai-compat` does
NOT. Today `ValidatePromptRunnerCommands` probes every declared runner with no kind filter, so an
`openai-compat` block draws a confident, wrong warning telling the operator their endpoint is not on
PATH. **A warning that is always wrong trains people to ignore GR2009.**

Also assert the **negative**: a plan with no `openai-compat` block emits none of these codes.

All must FAIL today. **Do NOT implement them.**

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Loading/OpenAiCompatDiagnosticsTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
