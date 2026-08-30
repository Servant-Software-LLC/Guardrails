## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's 14-author-tests-runner-verdict NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "14-author-tests-runner-verdict": { "someKey": "someValue" } }`.
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

Read: **plan sections 3.5, 6.4 and 6.5**.

## Task

### What to build

`tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatVerdictTests.cs`, class
**`OpenAiCompatVerdictTests`** (pinned - task 15 filters on it). Tests only.

Cover:

- **verdict transcription** via `PromptJsonExtractor` (task 05): prose around a valid object recovers
  it; a fenced json block that is not the last block loses to the last one; **prose with no JSON
  writes NO FILE AT ALL**;
- **the role gate**: an `Action` invocation is REFUSED; `Guardrail` and `Advisory` are served;
- **`ServesRoles` pinned BY CONSTRUCTION** - build the real runner for each kind-by-role pair and
  assert it accepts or refuses. Reading back the same field the runner reads is an echo of itself and
  proves nothing;
- section 6.5: an invocation with **empty** `StreamLogPath`, `WorkingDirectory` and `PlanDirectory`
  completes without crashing (`CriticalityJudge` supplies all three empty).

Drive these against the **real `FakeOpenAiServer`** (task 06) over a real loopback socket. The seam
under test is the OpenAI HTTP wire, so faking that boundary is correct and expected - do NOT
substitute an in-process double for the runner itself. The runner stub and the config surface already
exist (task 09), so everything here compiles.

Each test must assert an effect **only the production implementation emits** - the verdict file's
bytes, the stream log on disk, the pause that happened. *"The seam was called" is not an assertion*;
that is exactly how the bugs this plan cites shipped green.

Every test must FAIL against the throwing stub. **Do NOT implement the runner.**

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatVerdictTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
