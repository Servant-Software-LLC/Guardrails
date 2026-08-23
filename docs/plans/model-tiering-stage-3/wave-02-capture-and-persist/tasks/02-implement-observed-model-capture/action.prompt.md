## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-02-capture-and-persist/02-implement-observed-model-capture": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt), including the bare folder
  name and the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Fill real logic over the two stub declarations `01-author-tests-observed-model-capture` left, so
`ObservedModelCaptureTests` goes green.

### 1. `src/Guardrails.Core/Prompts/ClaudeStreamParser.cs` — stop discarding the init model

`Feed` currently returns early on **every** line whose `type != "result"`. Grep for the guard that reads
the `type` property and compares it to `"result"` — it is the only such comparison in the method. That
early return is what throws the `system`/`init` model away, and it is the entire gap this task closes.

Widen it so the parser also mines `model` from a `{"type":"system","subtype":"init",…}` line, keeping the
existing behaviour for everything else. Then read `model` off the terminal `result` line too, as the
**fallback**. Surface the answer on the `ClaudeResult.Model` member task 01 declared.

**Precedence is init-wins, and it is asserted:** when both lines carry a model and they differ, the init
value is the answer. Do not implement "last one wins" — the two orderings are indistinguishable until a
session switches models mid-run, and then they disagree.

Match the tolerance discipline the rest of this file already follows and that
`ClaudeStreamParserTests` pins:

- an unparseable line is skipped, not thrown on;
- a `model` that is absent, is not a string, or is empty yields **null**, never `""`. Absent must stay
  absent — a `""` reads as "the runner reported a model and it was blank". This is the same
  absent-not-zero rule `TryGetUsage` follows two members below, for the same reason.

### 2. `src/Guardrails.Core/Prompts/ClaudePromptRunner.cs` — carry it onto `PromptResult`

There are **three** `new PromptResult { … }` sites in this file. Set `ObservedModel = result.Model` on the
two that have a parsed stream behind them: the `#452` denial-abort result, and the normal terminal result.
The third is the **launch-failure** result — the binary never started, so there is no stream and no model;
leave it absent. Copy the shape of the `Usage` mapping already sitting in both of those sites: a straight
carry of what the parser mined, no recomputation, no defaulting.

### Do NOT force `--model` — and this is checked

The point of this change is to record what actually **ran**. Passing `--model` unconditionally so the
harness "knows" the model would (a) pin the zero-setup user who deliberately passes nothing, and (b)
record the model we *requested*, which is the weaker fact and is already recorded. Argv construction is
**out of scope for this task**: leave it exactly as it is. `ClaudePromptRunnerArgsTests` — which asserts
`--model` is absent when the route names no model — is inside this task's guardrail filter, so an
unconditional flag reds this task.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Prompts/ClaudeStreamParser.cs`
and `src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`. The harness runs a `git diff` check after this
task and rejects any edit outside those two paths — an out-of-scope edit fails the task immediately and
consumes a retry. In particular: **do NOT edit the authored tests.** Make them pass by fixing the
implementation. If a test in `ObservedModelCaptureTests` is genuinely wrong or incompatible with the
shipped contract, write `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the state-out
path rather than changing it.
