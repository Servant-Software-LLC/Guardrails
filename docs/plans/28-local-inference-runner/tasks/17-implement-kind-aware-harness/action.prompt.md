## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "17-implement-kind-aware-harness": { "someKey": "someValue" } }`.
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

Make `KindAwareHarnessTests` pass.

**1. The splice condition.** In `GuardrailRunner` and `ActionRunner`, the settings splice gains one
condition: `isWorktreeMode && PromptRunnerKinds.NeedsContainmentHook(block.Kind)`.

This is **not a weakening**. The hook exists to police `Write`/`Edit`/`MultiEdit`/`NotebookEdit`/`Bash`
tool calls; a runner that offers none of them has nothing for it to police. With the condition, the
runner's own `--settings` refusal becomes a TRUE backstop - reachable only if the splice and the
capability list disagree.

**2. The capability-aware verdict contract.** `PromptComposer.AppendVerdictContract` takes one
boolean - *can this runner write files?* - resolved from `PromptRunnerKinds.WritesFiles(kind)`. It
emits either the shipped section **byte-identical**, or the transcription form. One instruction,
never two. The composer learns a capability, never a vendor name.

`ComposedPrompt` is otherwise untouched, and `composed-prompt.md` - written before the invocation and
called by the SSOT *"exactly what the runner got"* - stays true. The runner appends nothing to it; its
own framing rides in the wire `system` message.

**Do NOT edit `KindAwareHarnessTests.cs`.**

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/GuardrailRunner.cs`, `src/Guardrails.Core/Execution/ActionRunner.cs`, `src/Guardrails.Core/Prompts/PromptComposer.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
