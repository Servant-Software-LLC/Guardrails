## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "12-author-tests-kind-aware-harness": { "someKey": "someValue" } }`.
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

Read: **plan sections 3.6 and 6.4**.

## Task

### What to build

`tests/Guardrails.Integration.Tests/OpenAiCompat/KindAwareHarnessTests.cs`, class
**`KindAwareHarnessTests`** (pinned - the guardrail filters on it). Tests only; no production edit.

Two deliverables are pinned here, and both currently FAIL:

**1. The containment splice becomes kind-aware (section 3.6).** Today `GuardrailRunner` and
`ActionRunner` splice a generated Claude `settings.json` whenever the run is in worktree mode. For an
`openai-compat` runner that is not containment, it is litter - and worse, it makes the runner's own
`--settings` refusal fire on **every worktree-mode pinned judge**, breaking the flagship deliverable
in the default execution mode.

**IN WORKTREE MODE**, a prompt guardrail pinned to an `openai-compat` block must produce a verdict
file whose bytes the harness reads. A serial-only test would pass with the flagship path broken, so
the worktree-mode case is the test that matters - write it that way.

**2. The verdict contract becomes capability-aware (section 6.4).** `PromptComposer` today tells
every model *"You MUST end by writing your verdict as a JSON object to this absolute path"*. A runner
with no write tool cannot, and a runner-supplied system message saying so would leave the weakest
model in the system holding two opposite instructions.

Assert on the COMPOSED BYTES, both ways:
- a writing runner gets the shipped text, **byte-identical** to today;
- a non-writing runner gets the transcription form (*"emit your verdict as the last fenced ```json
  block of your final message; the harness will write it to `<path>`"*).

The composer must learn a **CAPABILITY** (`PromptRunnerKinds.WritesFiles`), never a vendor name -
the SSOT section 9 quarantine.

Also pin that `composed-prompt.md` holds exactly the bytes sent as the user message, compared against
the request the loopback server received.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/OpenAiCompat/KindAwareHarnessTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
