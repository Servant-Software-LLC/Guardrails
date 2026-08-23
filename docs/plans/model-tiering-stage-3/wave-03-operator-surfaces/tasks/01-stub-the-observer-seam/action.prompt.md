## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-03-operator-surfaces/01-stub-the-observer-seam": { "someKey": "someValue" } }`.
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

Add the **three minimal stub declarations** the rest of this wave compiles against — and nothing else.
You write no tests and no behaviour. Tasks 02/03/04 author the failing tests; tasks 05/06/07 make them
pass. **Everything you declare must be inert**: a member with an empty default body, a formatter that
throws, and an optional parameter that changes no existing call site. If any of the three does real
work, the tests those tasks write would be green on arrival and would prove nothing.

Wave 2 already made the model the harness actually ran a recorded fact. **Nothing in this wave
re-derives it.** Read `AttemptProvenance.Model` and `AttemptProvenance.RequestedModel` in
`src/Guardrails.Core/Journal/JournalModel.cs` before you write anything — the doc comments there are the
contract, not this prompt's summary of it.

### 1. `src/Guardrails.Core/Execution/IRunObserver.cs` — ONE new default-method member

```csharp
void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) { }
```

`model` is the attempt's **best-known-actual** model; `requestedModel` is non-null **only** when the
route asked for something else — the same two-field contract `AttemptProvenance` already carries, so no
observer re-derives the comparison. Mirror `VerifierAdvisoryFound`'s shape and doc-comment discipline
(grep for `void VerifierAdvisoryFound` in that file): primitives plus `TaskNode`, an empty default body,
and a doc comment whose last paragraph states plainly that a transparent DECORATOR must forward it
EXPLICITLY or the event is swallowed in every mode. **Do NOT add it to the private `NullObserver`** —
that type deliberately overrides only the non-default members.

### 2. `src/Guardrails.Cli/Ui/LiveRunObserver.cs` — ONE pure static, and nothing else

```csharp
public static string AttemptModelSummary(string model, string? requestedModel) =>
    throw new NotImplementedException();
```

with an XML doc comment. This is the ONE formatter both renderers will call in task 06, so the plain and
live surfaces cannot drift. It lives here for the same reason `StatusMarkup` and `PostMortemPagePath` do:
no live terminal renders in a non-interactive test, so a public pure function IS the test seam.

**Do not implement it, and do NOT add `AttemptModelResolved` to this class** — that is task 06's whole
deliverable, and task 03's `LiveObserver_DeclaresAttemptModelResolved_RatherThanInheritingTheEmptyDefault`
is RED precisely because this class has not declared it yet. Adding it here turns that test green on
arrival and destroys the proof.

### 3. `tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs` — an OPTIONAL observer

Widen `RunAsync` so a test can watch the real attempt loop:

```csharp
public async Task<Stage2RunResult> RunAsync(Stage2PlanSpec spec, IRunObserver? observer = null)
```

and pass `observer ?? IRunObserver.Null` at **both** sites that currently hardcode `IRunObserver.Null`
(grep for `IRunObserver.Null` — there are exactly two: the `TaskExecutor` construction and the
`Scheduler` construction). Both must receive it: an observer wired into only one of them would make a
raise from the attempt loop look absent. Optional-with-a-default so **no existing call site changes** —
this file is shared with the whole Stage-2 conformance suite, and every one of those tests must still
pass.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/IRunObserver.cs`, `src/Guardrails.Cli/Ui/LiveRunObserver.cs` and
`tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths — including `TaskExecutor.cs`,
`ConsoleRunObserver.cs`, either on-the-fly decorator, the three `AttemptModel*Tests.cs` files, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and stop.

### Done means

The solution builds, the whole Stage-2 conformance suite still passes (you changed no call site), and the
three declarations are present and inert.
