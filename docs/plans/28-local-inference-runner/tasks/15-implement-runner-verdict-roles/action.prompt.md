## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "15-implement-runner-verdict-roles": { "someKey": "someValue" } }`.
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

Read: **plan sections 3.5, 3.7, 6.4 and 6.5**.

## Task

### What to build

Make **`OpenAiCompatVerdictTests`** pass, and only that class. After this task the runner is complete.

- **Verdict transcription (section 6.4).** The runner writes the verdict file, and the rule that makes
  that safe is that it may only ever **TRANSCRIBE**. Extract with `PromptJsonExtractor` (task 05);
  for a verdict the object must carry a boolean `pass`. Anything else means **NO FILE IS WRITTEN**.
  The failure direction is safe by construction: no file is already the contractual fail, and the
  runner can never produce a `pass: true` the model did not write as a boolean.
- **The role gate.** Refuse an `Action` invocation loudly rather than serving it. Fill in
  `PromptRunnerKinds.ServesRoles` (`Claude` serves all three; `OpenAiCompat` serves `Guardrail` and
  `Advisory`), `NeedsContainmentHook` (`Claude` true, `OpenAiCompat` false) and `WritesFiles`. These
  are **statements of fact about the BUILD, not config keys** - a config key would invite an operator
  to declare a capability the build does not have.
- **Registry construction.** `PromptRunnerRegistry` constructs an `OpenAiCompatPromptRunner` for
  `kind: "openai-compat"`, and `PromptRunnerKinds.Implemented` grows to two. The existing test that
  pins `Implemented` against the dispatch switch
  (`tests/Guardrails.Core.Tests/ModelTiering/PromptRunnerSchemaTests.cs`) must still pass - it is
  parameterised over every kind and asserts `IsImplemented(kind) == (registry construction
  succeeded)`, so it stays green **only if** construction genuinely works. Do not edit it.
- **`--settings` is fatal.** If an `ExtraArg` of `--settings` reaches this runner, THROW rather than
  proceeding - after task 17's splice condition it is genuinely unreachable, so its arrival means the
  splice and the capability list disagree, which is a harness bug worth throwing on.
- **Section 6.5** - an invocation with empty `StreamLogPath`, `WorkingDirectory` and `PlanDirectory`
  must complete without crashing. `CriticalityJudge` supplies all three empty.

**Do NOT edit any test file.**

### Two stale assertions you must update (added after this task halted)

Implementing `openai-compat` makes two assertions in
`tests/Guardrails.Core.Tests/ModelTiering/PromptRunnerSchemaTests.cs` false BY DESIGN - they encode the
premise that this kind has no implementation, which is exactly the premise your deliverable exists to
falsify. No task owned that file, so it is now in your writeScope FOR THESE TWO EDITS ONLY.

Plan section 3.1 draws the line precisely, and it is the whole of your authority here:

> v1 implements `PromptRunnerKind.OpenAiCompat` and nothing else. `Local`, `Codex` and `OpenRouter`
> remain reserved names and **remain GR2044 errors**.

So exactly ONE kind stops being an error. Make these two changes and nothing else:

1. `RecognizedButUnimplementedKind_FailsValidate_NotJustRegistryConstruction` - remove the
   `[InlineData("openai-compat")]` row. **Keep `codex`, `openrouter` and `local`.** The test keeps its
   full force for every kind that is still reserved; it simply no longer claims that about the one kind
   this plan implements.

2. `OpenAiCompatKind_IsRecognized_NotAnUnknownToken` - keep what is still true and drop only what is
   now false. `openai-compat` is still a RECOGNIZED token rather than an unknown one, and that is worth
   pinning; what is no longer true is the "no implementation" half. Assert it produces neither
   `InvalidPromptRunnerKind` nor a GR2044 "no implementation" diagnostic, and update the doc comment so
   the next reader sees "recognized AND implemented" rather than the old "recognized but not yet".

**Do not touch anything else in that file.** In particular
`ImplementedKindList_AgreesWithRegistryDispatch_ForEveryKind` already passes - registry construction
genuinely works for every kind - and the three reserved-kind rows must keep failing validate exactly as
they do today. Deleting the theory, weakening it to a smoke test, or removing the reserved kinds would
turn a real gate into scenery, and would be a far worse outcome than this task staying red.

If any OTHER test in the repo turns red from your change, that is a finding, not a licence: write
`needsHuman` naming it rather than widening your own scope again.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs`, `src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs`, `src/Guardrails.Core/Model/PromptRunnerConfig.cs`, `tests/Guardrails.Core.Tests/ModelTiering/PromptRunnerSchemaTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
