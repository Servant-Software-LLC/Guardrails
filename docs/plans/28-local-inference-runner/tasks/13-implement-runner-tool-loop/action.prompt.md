## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "13-implement-runner-tool-loop": { "someKey": "someValue" } }`.
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

Read: **plan sections 3.2, 5, 6.6 and 4**.

## Task

### What to build

Make **`OpenAiCompatToolLoopTests`** pass, and only that class.

- **The fixed, read-only tool set**, named exactly **`Read`**, **`Glob`** and **`Grep`**. Those
  spellings are not a preference: `Overwatch.cs` and `NeedsHumanTriage.cs` already tell the model
  *"your ONLY tools are Read, Glob and Grep"* in prose, and those strings are harness-owned and stay
  verbatim. A tool schema whose names disagree with the prompt text is a contradiction handed to the
  weakest model in the system. No write tool, no shell tool.
- **`allowedTools` FILTERS rather than being ignored.** When the declared list names at least one of
  `Read` / `Glob` / `Grep`, offer only those; otherwise offer all three. Disclose the narrowing in
  the `runner-notice` line.
- **Containment on every tool call** via `PromptToolContainment.IsReadable` with roots
  `{ WorkingDirectory, PlanDirectory }`, empty entries dropped. A refusal is a DENIAL and counts
  toward `AbortAfterConsecutiveToolDenials` - both advertised consumers set it to 3, and
  `PromptInvocation` calls that bound *"a runner-agnostic POLICY the harness declares"* whose
  detection is the runner's own business. Ignoring it would be indefensible.
- **The section 6.6 rule - the reason this task exists.** An OpenAI-compatible server may accept a
  `tools` array, ignore it, and return an ordinary completion; the protocol cannot distinguish *"I
  considered the tools and needed none"* from *"I do not implement tools"*. The model then answers
  from the prompt alone, having read NOTHING, and emits an immaculate `{"pass": true}` - and every
  other check passes, because they all test for a MALFORMED response and this one is perfect. So:
  **a `Guardrail`-role invocation that completes with ZERO tool calls FAILS the attempt**
  (`PromptFailureKind.Error`, naming the block and the endpoint).

  It is deliberately blunt, and blunt in the safe direction: a verifier that read no files has not
  verified anything, so refusing is right even in the rare case the answer was obtainable from the
  prompt alone. Trusting it is the false green.

  **Scope it to the `Guardrail` role ONLY.** An `Advisory` invocation legitimately reasons over text
  it was handed and may call nothing; applying the rule there would fail every advisory call on every
  engine. `PromptInvocation.Role` carries the distinction - a second payoff from that field existing.

Render the transcript as you go: it is the operator's only readable view of a tool loop, and it names
every tool call and its result size, so a verifier that rendered a verdict having read nothing is
visible to a human.

**Do NOT edit any test file.**

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
