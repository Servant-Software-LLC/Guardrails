## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `12-author-tests-observer-by-field`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "12-author-tests-observer-by-field": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author the failing tests AND the interface stub for `IRunObserver.SuppliedResourcesCommitted`'s new
third argument, `by` — design 41 §6 ("The observer event gains `by`").

**Why the event needs it.** `events.jsonl` is what an unattended consumer reads, and `DecisionRecorded`
has no row there, so the adjacent decision cannot carry the attribution. A record able to name only one
supplier is not provenance.

**THE STUB — an ADDED member on `src/Guardrails.Core/Execution/IRunObserver.cs`:**

```csharp
void SuppliedResourcesCommitted(IReadOnlyList<string> paths, string commit, string by) { }
```

Three things about it, each load-bearing:

- **It is ADDED here, not swapped.** Six types declare the two-argument member
  (`RunEventStream`, `ObserverProjection`, `ConsoleRunObserver`, `LiveRunObserver`,
  `OnTheFlyLogSiteObserver`, `OnTheFlyDiagramObserver`) and `Scheduler.cs:4927` calls it. None of those
  files is in your write scope. Removing the two-argument member is task 13's job, in the same change
  that migrates all seven. Leave it exactly as it is.
- **Its body is EMPTY, not `throw new NotImplementedException()`.** The empty body IS the defect these
  tests exist to expose: design 41 §6 says *"a default interface member hides a missed implementer"* —
  the decorator keeps compiling, the three-argument call lands on the empty body, and the event silently
  disappears. Two of your tests are negative controls that assert exactly that swallow; a throwing
  default would make them throw instead, and they would then be asserting the opposite of the shipped
  behaviour.
- **Do NOT add a doc comment claiming the member is implemented.** Task 13 writes the real one.

**The three test files, and which project each lives in.** Task 13's guardrails run ONE `dotnet test`
per project, because a single project path would silently miss half the evidence:

| file | project |
|---|---|
| `tests/Guardrails.Core.Tests/Supply/SuppliedObserverEventTests.cs` | `Guardrails.Core.Tests` |
| `tests/Guardrails.Integration.Tests/Supply/SuppliedObserverCliForwardingTests.cs` | `Guardrails.Integration.Tests` |
| `tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs` | `Guardrails.Integration.Tests` |

`Guardrails.Core.Tests` references `Guardrails.Core` and nothing else, so a CLI type cannot compile
there. Do not move a test between these files to make something convenient.

**The plan trait.** On `SuppliedObserverEventTests` and `SuppliedObserverCliForwardingTests`, REPLACE the
class-level `[Trait("Category", "Supply")]` with `[Trait("Category", "OverwatchSupply")]` — every test in
both classes is rewritten by this task, and task 13's filter is what runs them.
`ObserverForwardingSweepTests` carries no class-level trait: ADD `[Trait("Category", "OverwatchSupply")]`
beside the existing `[Trait("Category", "RunEvents")]` on the ONE test you change there, and leave its
other two tests alone.

**The guards must compare PARAMETER LISTS, not member names.** Today all three files ask only whether a
type declares a method of that NAME. Once the three-argument member exists, a decorator that kept the
two-argument one satisfies a name-only guard perfectly — which is precisely the defect. Replace each
file's `Declares(Type, string)` helper with one that takes the `MethodInfo` of the interface member and
requires an exact parameter-type match:

```csharp
private static bool Declares(Type type, MethodInfo member) =>
    type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Any(m => (m.Name == member.Name || m.Name.EndsWith("." + member.Name, StringComparison.Ordinal))
                  && m.GetParameters().Select(p => p.ParameterType)
                       .SequenceEqual(member.GetParameters().Select(p => p.ParameterType)));
```

The `EndsWith` half is not decoration — an explicit interface implementation is named
`Guardrails.Core.Execution.IRunObserver.SuppliedResourcesCommitted`. Keep the non-vacuity floors the
three files already have (an unresolved type name fails LOUDLY and by name; the member itself must
exist) and keep driving `RunCommand.BuildObserverChain` rather than hand-stacking decorators.

**Migrate every existing call.** All of these files call the two-argument form today; each becomes the
three-argument form (use `"operator"` where the caller is the ordinary boundary drain and
`"overwatcher"` where the test is about the auto-resolve). Their `RecordingObserver` doubles must declare
**only** the three-argument method — replaced, never overloaded, for the same reason the interface is.

**Pin these behaviours to these EXACT method names.**

In `SuppliedObserverEventTests` (Core):

- `Event_CarriesThePathsTheCommitAndTheSupplier` — `RunEventStream` appends a
  `supplied-resources-committed` row carrying `paths`, `commit` AND `by`.
- `EveryDecorator_ForwardsTheEventWithTheSupplier` — the parameter-aware declaration census over
  `RunEventStream` and `ObserverProjection`, plus the behavioural forward through the real Core chain,
  asserting `by` arrives intact.
- `TheTwoArgumentMember_IsReplacedNotOverloaded` — assert
  `typeof(IRunObserver).GetMethod(name, [typeof(IReadOnlyList<string>), typeof(string)])` is **null**,
  with a non-vacuity floor asserting the three-argument overload **is** found. This is the test that
  makes "replaced, never overloaded" a fact rather than an intention; it is red while your own stub
  stands and goes green when task 13 deletes the old member. Write it anyway — it is the one row that
  fails if task 13 takes the cheap route.

In `SuppliedObserverCliForwardingTests` (Integration):

- `ConsoleRunObserver_ForwardsTheEventWithTheSupplier`
- `LiveRunObserver_RendersTheSupplierInTheLiveTable` — the live table prints
  `supplied by overwatcher: 1 resource(s) committed <sha> — <paths>`.
- `NoUi_PrintsTheSuppliedResourcesLineNamingTheSupplier` — `--no-ui` prints
  `[supplied] by overwatcher: 1 resource(s) committed <sha>: <paths>`. Assert the literal
  `[supplied] by overwatcher:` — the wording is design 41 §6's, and an operator greps for it.
- `EveryCliDecorator_DeclaresAndForwardsTheEventWithTheSupplier` — the parameter-aware census over
  `OnTheFlyDiagramObserver` and `OnTheFlyLogSiteObserver`, plus the real-chain forward proof.
- `OverwatcherSupply_ReachesTheEventsJsonlRowAndTheNoUiLine` — **the artifact test.** Build the REAL
  chain with `RunCommand.BuildObserverChain(new ConsoleRunObserver(writer), logsRoot, runId, plan,
  logUrlForTask: null, diagramSeed: null)`, raise the event with `by: "overwatcher"`, then assert
  against what was PRODUCED: the `logs/<runId>/events.jsonl` row's `by` field is `"overwatcher"` and
  the console output contains `[supplied] by overwatcher:`. Read the JSON, do not grep a source file —
  design 41 §6 requires this proven at the artifact.

In `ObserverForwardingSweepTests` (Integration):

- `EveryTransparentDecorator_DeclaresEveryIRunObserverMember_WithItsExactParameterList` — this is the
  existing `EveryTransparentDecorator_DeclaresEveryIRunObserverMember`, RENAMED and upgraded to compare
  parameter lists across all four transparent decorators and every `IRunObserver` member. Report a
  missing pair as `<type> : <member>(<parameter types>)` so the failure names the signature, not just
  the member.

**TWO tests are DECLARED EXEMPT from the red census** — both are negative controls that hold GREEN on
your stub tree and after task 13, and both must still EXIST and be migrated to the three-argument call:

- `ADecoratorThatDropsTheEvent_IsCaught` (Core) — `DroppingObserver` declares nothing, so the call lands
  on the empty default and the inner observer hears nothing.
- `NullObserver_DoesNotDeclareTheEvent_BecauseItsContractIsToSwallowEverything` (Core) — `NullObserver`'s
  whole contract is to swallow every event, so it correctly declares neither form.

The nine pinned tests MUST COMPILE and FAIL. Reflection-based assertions compile against today's code
and fail against it, which is what makes this red honest.

**If the parameter-aware sweep turns up a mismatch that has nothing to do with `by`** — some decorator
declaring an unrelated member with a different parameter list — that is a real finding on the shipped
code, not a reason to loosen the comparison. Write
`{"needsHuman": {"question": "<type>.<member> declares <sig-a> against the interface's <sig-b>", "kind": "blocked-work"}}`
and stop.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs`, `tests/Guardrails.Core.Tests/Supply/SuppliedObserverEventTests.cs`, `tests/Guardrails.Integration.Tests/Supply/SuppliedObserverCliForwardingTests.cs`, and `src/Guardrails.Core/Execution/IRunObserver.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
