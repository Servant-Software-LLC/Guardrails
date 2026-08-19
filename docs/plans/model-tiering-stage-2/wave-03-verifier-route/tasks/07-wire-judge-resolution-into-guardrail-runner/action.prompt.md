## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/07-wire-judge-resolution-into-guardrail-runner`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/07-wire-judge-resolution-into-guardrail-runner": { "someKey": "someValue" } }`.
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

### Where the actor's resolution comes from — THREAD it, never re-derive it

`GuardrailRunner` has no route today, and you must **not** give it a `TierResolver.Resolve` call of
its own. Wave 2 already solved this exact problem for the action path, and you are copying that shape:

- `TaskExecutor.RunAttemptAsync` resolves the actor's route ONCE into a local — grep for
  `TierResolution? route = ResolveRoute(task)` — and that same object is already threaded into
  `_actionRunner.RunAsync(...)` and into `BuildProvenance(task, worktree, route)`.
- Do the same for guardrails: add the route as a parameter on `GuardrailRunner.RunAsync` and pass the
  SAME local at the `_guardrailRunner.RunAsync(` call site inside `RunAttemptAsync`.

That is why `TaskExecutor.cs` is in your write scope: one parameter, one argument.

**A second `TierResolver.Resolve(` call inside `GuardrailRunner` is FORBIDDEN and your guardrail
fails on it.** Two resolution sites drift — one gets a fix, the other does not, and the judge is
graded against a rung the actor never ran at. This is not hypothetical tidiness: the `route` local
exists in exactly that shape *because* wave 2 severed a duplicate derivation already.

The OTHER call site is `RevalidateAsync`, the re-verification path a human's in-place fix runs
through. It has no action attempt and therefore **no actor route — pass `null` there**, and do not
invent one.

**But `null` route does NOT mean "no judge".** With no actor rung to key off, `ResolveJudge` still
does real work: rule 1's frontmatter pin, §6.5.1's `minTier` floor, and the default block. A
revalidate graded by a model resolved a judge exactly as an attempt does, so your resolution must be
exposed on that call's result too — task 08 records it. Handle a null route as a first-class input,
not as a reason to skip resolution and return nothing.

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

**Make the resolved judge datum available to task 08.** Task **08** carries it to the journal, and it
cannot invent what you do not expose — add the resolved judge to **`GuardrailRunResult`** (the record
this method already returns: `Results`, `AnyFailed`, `TimedOut`) rather than leaving it a local.

Wave 2 lost a task to exactly this shape (#474), and shipped a live instance of it (#475:
`AttemptRecord.Usage` is declared, is READ by the per-tier spend aggregation, and is assigned by
NONE of the twelve construction sites — the feature is structurally dead and every guardrail was
green). Exposing the datum here is what keeps wave 3 off that list.

### Scope

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/GuardrailRunner.cs` and
`src/Guardrails.Core/Execution/TaskExecutor.cs`. Your `TaskExecutor` edit is NARROW — the route
parameter at the `_guardrailRunner.RunAsync(` call sites and nothing else; task 08 makes the
provenance change in that same file and must not find its work already done. After this task
completes, the harness runs a `git diff` check and rejects any edit outside those two paths —
including the conformance tests (task 05 owns them), `TierResolver.cs` (task 02),
`JournalModel.cs` (tasks 03/04), or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.

If making the clauses pass genuinely requires a change outside this file, write
`{"needsHuman": "<the file and why>"}` rather than an out-of-scope edit — that is the honest halt,
and wave 2 proved it is cheaper than the alternative.

**Deterministic guardrails are untouched by all of this.** They run no model and have no judge; only
the prompt-guardrail path changes.
