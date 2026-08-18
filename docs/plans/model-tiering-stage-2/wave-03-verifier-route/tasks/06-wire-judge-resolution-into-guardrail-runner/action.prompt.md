## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/06-wire-judge-resolution-into-guardrail-runner`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/06-wire-judge-resolution-into-guardrail-runner": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Wire **`TierResolver.ResolveJudge`** into `GuardrailRunner`'s prompt-guardrail path so the
conformance clauses task 05 authored go green.

**Today the judge's block is chosen with no tier awareness whatsoever.** In
`src/Guardrails.Core/Execution/GuardrailRunner.cs` (grep for these; **do not rely on line numbers**):

```csharp
PromptRunnerConfig runnerConfig = registry.ResolveConfig(promptFile.Frontmatter.Runner);
...
PromptRunnerSettings settings = PromptExecutionSupport.ApplyPromptOverrides(
    runnerConfig.EffectiveSettings(isGuardrail: true), promptFile.Frontmatter.MaxTurns);
...
PromptResult promptResult = await registry.Resolve(promptFile.Frontmatter.Runner)...
```

The block comes from `frontmatter.Runner` or the default — never from the actor's rung, never bumped.

### What to change

Resolve the judge's route through `ResolveJudge` (given the actor's resolution for this attempt) and
use the **resolved judge block** where `runnerConfig` is used today — including the runner instance
the invocation actually executes on. A change that computes a route and then still executes on
`frontmatter.Runner` is the classic half-wire: green unit tests, dead in production.

**Rule 7 falls out for free IF you do this correctly, and is silently wrong if you do not.**
`EffectiveSettings(isGuardrail: true)` folds in the `guardrailOverrides` of **whatever
`PromptRunnerConfig` it is called on**. Call it on the **resolved judge block** and rule 7 —
*"`guardrailOverrides` compose with the resolved JUDGE block, not the actor's"* — is satisfied by
construction. Call it on the actor's block, or on the frontmatter block, and every bumped judge is
silently mis-profiled with another block's permissions, tools and turn budget. Nothing else in the
system will notice.

**Make the resolved judge datum available to task 07.** Task 07 carries it to the journal, and it
cannot invent what you do not expose — put the `JudgeResolution` on the result this method already
returns rather than leaving it a local. Wave 2 lost a task to exactly this (a datum with no path to
its sink, #474).

### Scope

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/GuardrailRunner.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including the conformance tests (task 05
owns them), `TierResolver.cs` (task 02), `AttemptJournaler.cs`/`Scheduler.cs` (task 07), or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.

If making the clauses pass genuinely requires a change outside this file, write
`{"needsHuman": "<the file and why>"}` rather than an out-of-scope edit — that is the honest halt,
and wave 2 proved it is cheaper than the alternative.

**Deterministic guardrails are untouched by all of this.** They run no model and have no judge; only
the prompt-guardrail path changes.
