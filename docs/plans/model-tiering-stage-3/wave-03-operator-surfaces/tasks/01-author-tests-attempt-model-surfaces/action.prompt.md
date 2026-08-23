## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-03-operator-surfaces/01-author-tests-attempt-model-surfaces": { "someKey": "someValue" } }`.
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

Author the **failing tests** — and only the minimal stub declarations they need to COMPILE — for the
two OPERATOR surfaces of #349: the per-attempt log preamble, and the live/plain UI.

Wave 2 already made the model the harness actually ran a recorded fact. **Nothing in this wave
re-derives it.** Every test below consumes what wave 2 persisted: `AttemptProvenance.Model` (now
best-known-actual — observed, else the route's, else the `"(cli default)"` sentinel) and
`AttemptProvenance.RequestedModel` (written **only** when it differs). Read those two members in
`src/Guardrails.Core/Journal/JournalModel.cs` before you write anything — the doc comments there are
the contract, not this prompt's summary of it.

### The settled shape — do NOT re-litigate it

- `requestedModel`'s **presence is the mismatch signal.** There is no separate flag. A surface that
  always prints one string throws away the entire reason #349 exists; a surface that always prints two
  is equally wrong, because then the two-string form carries no information.
- **Do not re-parse the stream, and do not force `--model`.** Wave 2 owns capture. Every value you
  assert on is read off the provenance object the attempt already folded.

### Files to write — and nothing else

**Three new test files**, all under `tests/Guardrails.Integration.Tests/ModelTiering/`. This project is
the right home for all three: it is the only test project that references **both** `Guardrails.Core`
and `Guardrails.Cli` (check `Guardrails.Integration.Tests.csproj` — `Guardrails.Core.Tests` references
Core alone), and the decorator tests need the CLI types.

1. `AttemptModelDisclosureTests.cs` — public sealed class **`AttemptModelDisclosureTests`**
2. `AttemptModelRenderingTests.cs` — public sealed class **`AttemptModelRenderingTests`**
3. `AttemptModelForwardingTests.cs` — public sealed class **`AttemptModelForwardingTests`**

These three class names are load-bearing: tasks 02, 03 and 04 each run
`--filter "FullyQualifiedName~<one of them>"`, and that one-class-per-consumer split is what stops
task 03's guardrail waiting on task 04's deliverable. **One behaviour must not move between classes.**

**Three stub declarations**, and nothing more in those files:

4. `src/Guardrails.Core/Execution/IRunObserver.cs` — add ONE new default-method member:

   ```csharp
   void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) { }
   ```

   `model` is the attempt's **best-known-actual** model; `requestedModel` is non-null **only** when the
   route asked for something else — the same two-field contract `AttemptProvenance` already carries, so
   no observer re-derives the comparison. Mirror `VerifierAdvisoryFound`'s shape and doc-comment
   discipline (grep for `void VerifierAdvisoryFound` in that file): primitives plus `TaskNode`, an empty
   default body, and a doc comment whose last paragraph states plainly that a transparent DECORATOR must
   forward it EXPLICITLY or the event is swallowed in every mode. **Do NOT add it to the private
   `NullObserver`** — that type deliberately overrides only the non-default members.

5. `src/Guardrails.Cli/Ui/LiveRunObserver.cs` — add ONE pure static, and nothing else:

   ```csharp
   public static string AttemptModelSummary(string model, string? requestedModel) =>
       throw new NotImplementedException();
   ```

   with an XML doc comment. This is the ONE formatter both renderers will call in task 03, so the plain
   and live surfaces cannot drift. It lives here for the same reason `StatusMarkup` and
   `PostMortemPagePath` do: no live terminal renders in a non-interactive test, so a public pure
   function IS the test seam. **Do not implement it, do not add `AttemptModelResolved` to this class,
   and change nothing else in the file** — that is task 03's whole deliverable.

6. `tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs` — widen `RunAsync` with an
   OPTIONAL observer, so a test can watch the real attempt loop:

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
`tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelDisclosureTests.cs`,
`tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelRenderingTests.cs`,
`tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelForwardingTests.cs`,
`tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs`,
`src/Guardrails.Core/Execution/IRunObserver.cs` and `src/Guardrails.Cli/Ui/LiveRunObserver.cs`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside these paths —
including `TaskExecutor.cs`, `ConsoleRunObserver.cs`, either on-the-fly decorator, `Stage2ConformanceTests.cs`,
or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a
compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and stop.

### The behaviours, and the EXACT test-method name each must carry

A census guardrail reads a TRX result file and requires **every** name below to be present and
`Failed`. Name them exactly, and put each in the class shown.

#### `AttemptModelDisclosureTests` — the log preamble and the raise (task 02 makes these green)

| test method | behaviour |
|---|---|
| `RouteLog_NamesTheObservedModel_NotTheRequestedOne` | in a run whose fake runner echoes a model DIFFERENT from the route's, the attempt's `attempt-route.log` `model:` line carries the **observed** value |
| `RouteLog_CarriesARequestedModelLine_WhenTheObservedDiffersFromTheRoute` | that same log also carries a `requested model:` line whose value is the **route's** model |
| `RouteLog_CarriesNoRequestedModelLine_WhenTheObservedMatchesTheRoute` | in a run whose runner echoes exactly what the route asked for, the log carries **no `requested model:` line at all** — present *is* the mismatch signal |
| `AttemptLoop_RaisesAttemptModelResolved_WithBothStrings_OnMismatch` | the attempt loop raises `AttemptModelResolved` for the prompt attempt with `model` = the observed value and `requestedModel` = the route's |
| `AttemptLoop_RaisesAttemptModelResolved_WithNoRequestedModel_WhenTheRunnerEchoedTheRoute` | the same run with an agreeing runner raises the event with `requestedModel` **null** — the event is raised either way; only the second string is conditional |

**The `requested model:` key is not invented — it is the file's own idiom.** `attempt-route.log` already
writes `runner block: `, `model: `, `effort: `, `requested tier: `, `served tier: `, `tierSource: ` — one
`key: value` per line. `requested model:` is the exact sibling of the `requested tier:` line already
there. Read `WriteRouteDisclosure` in `src/Guardrails.Core/Execution/TaskExecutor.cs` (grep for
`static void WriteRouteDisclosure`) and match that shape; do not invent a second format.

Assert on the **written file**, read back off disk via `Stage2RunResult.AttemptLogDir(taskId, attempt)`
— never on an in-memory value the harness returned. That is not hypothetical in this repo: it shipped
`AttemptRecord.Usage` declared, read by the per-tier aggregation, and assigned by no construction site
at all, with every guardrail green (#475).

#### `AttemptModelRenderingTests` — the two renderers (task 03 makes these green)

| test method | behaviour |
|---|---|
| `Summary_NamesBothModels_WhenTheRequestedModelIsPresent` | `LiveRunObserver.AttemptModelSummary(observed, requested)` contains BOTH strings |
| `Summary_OmitsTheRequestedModel_WhenItIsAbsent` | `AttemptModelSummary(observed, null)` contains the observed model, does **not** contain the requested one, and is **not equal** to the two-argument form — the one-string and two-string cases must be distinguishable |
| `ConsoleObserver_WritesTheSharedSummary_ForAttemptModelResolved` | a real `ConsoleRunObserver` over a `StringWriter`, called **through `IRunObserver`**, writes a line CONTAINING `LiveRunObserver.AttemptModelSummary(model, requestedModel)` — the agreement property that stops the plain surface growing its own second copy of the wording |
| `LiveObserver_DeclaresAttemptModelResolved_RatherThanInheritingTheEmptyDefault` | reflection: `typeof(LiveRunObserver).GetMethod("AttemptModelResolved", ...)` is non-null. A class that merely *implements* `IRunObserver` gets no member for a default method, so this is exactly the "silently inherited the empty body" failure |

#### `AttemptModelForwardingTests` — the decorators (task 04 makes these green)

| test method | behaviour |
|---|---|
| `LogSiteDecorator_ForwardsAttemptModelResolved_ToItsInnerObserver` | construct `OnTheFlyLogSiteObserver` wrapping a recording inner `IRunObserver`, invoke the event, assert the inner received it with the same `(task, attempt, model, requestedModel)` |
| `DiagramDecorator_ForwardsAttemptModelResolved_ToItsInnerObserver` | the same for `OnTheFlyDiagramObserver` |
| `EveryForwardingObserverInTheCliAssembly_DeclaresAttemptModelResolved` | reflection over `typeof(LiveRunObserver).Assembly`: **every** non-abstract type implementing `IRunObserver` that has a constructor taking an `IRunObserver` parameter must DECLARE `AttemptModelResolved`. The decorator pair named above is what existed when this wave was authored; this clause is what catches a third one added later |

**Invoke through the interface, not the concrete type.** Write
`((IRunObserver)decorator).AttemptModelResolved(task, 1, "m", "r")`, not `decorator.AttemptModelResolved(...)`.
A decorator that has not yet declared the member has no class-level method to call, so the concrete-type
form would be a COMPILE error rather than a behavioural red — and the interface form is also the dispatch
path the Scheduler actually uses. `JitBreakdownVisibilityTests.T12_OnTheFlyLogSiteObserver_ForwardsBothPhaseMembers`
is the precedent for the fixture shape (a small `CountingObserver`, a temp tree, the decorator's real
constructor arguments); grep for it and follow it.

### The red must COMPILE

Failing is the point; **not compiling is a mistake to fix.** With the three stubs above, all twelve tests
compile: the interface member exists, `AttemptModelSummary` exists (and throws), and the harness accepts
an observer. Do NOT implement the disclosure, the raise, the renderers or the forwarding — those are
tasks 02, 03 and 04.

Every one of these twelve must be RED for a reason that is about this wave. A test that fails because it
does not compile, or because it asserts something already true, is not a red — the census guardrail names
each one individually so a hollow assertion cannot hide behind a genuinely-failing sibling.
