## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-03-operator-surfaces/04-forward-attempt-model-in-decorators": { "someKey": "someValue" } }`.
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

Make `AttemptModelForwardingTests` pass: forward `AttemptModelResolved` from **both** on-the-fly
decorators to their inner observer.

```csharp
public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
    _inner.AttemptModelResolved(task, attempt, model, requestedModel);
```

- `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs`
- `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`

Both files already carry a run of one-line explicit forwards — grep for `_inner.VerifierAdvisoryFound`
in either and add yours alongside them, in the same idiom. Pass all four arguments through unchanged;
neither decorator has any business re-deriving or reformatting the model strings.

### Why this is a task and not a footnote

`IRunObserver.AttemptModelResolved` is a **default-method** member with an empty body. A decorator that
simply omits it still compiles, still satisfies the interface, and silently swallows the event — in
**every** mode, because these two decorators wrap both the live and the plain observer chains. The
compiler will not tell you, the live table will still render the event (the test that exercised the
inner observer directly saw it), and the on-the-fly log site and the diagram will quietly never see it.

This repo has already paid for that lesson twice: `VerifierAdvisoryFound`'s doc comment says it in place
(*"an unforwarded call resolves to this empty body and the advisory is swallowed silently, in exactly
the mode most operators run"*), and the JIT breakdown phase (#469) shipped invisible for the same
reason. `JitBreakdownVisibilityTests.T12_OnTheFlyLogSiteObserver_ForwardsBothPhaseMembers` is the
regression test that came out of it — and the tests you are turning green are its direct descendants.

### One of your three tests is about a decorator that does not exist yet

`EveryForwardingObserverInTheCliAssembly_DeclaresAttemptModelResolved` reflects over the whole
`Guardrails.Cli` assembly and requires **every** non-abstract `IRunObserver` implementation that takes an
`IRunObserver` constructor parameter to declare the member. Today that is exactly the two files above —
but the point is the day it is three. If that test fails naming a type you were not expecting, do **not**
widen your edit to reach it: your `writeScope` is these two files, an out-of-scope edit fails the task,
and a third decorator appearing mid-wave is a fact a human needs to see. Write
`{"needsHuman": {"question": "<the type the reflection test named>", "kind": "blocked-work"}}` to the
state-out path and stop.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs` and `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`.
The harness runs a `git diff` check after this task and rejects any edit outside those two paths —
including the three test files, `IRunObserver.cs`, `LiveRunObserver.cs`, `ConsoleRunObserver.cs` and
`TaskExecutor.cs`. An out-of-scope edit fails the task immediately and consumes a retry. **Do NOT edit
the authored tests.** If a test in `AttemptModelForwardingTests` is genuinely wrong or incompatible,
write `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` rather than changing it.

### Done when

`dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~AttemptModelForwardingTests"`
passes — three tests. `AttemptModelDisclosureTests` and `AttemptModelRenderingTests` belong to 02-author-tests-disclosure
and 03-author-tests-rendering, run in parallel with you, and your guardrail does not select them.
