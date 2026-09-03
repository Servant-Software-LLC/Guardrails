## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `02-author-tests-observer-forwarding`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "02-author-tests-observer-forwarding": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "02-author-tests-observer-forwarding": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

Author the forwarding tests that catch a decorator silently swallowing an event.

`IRunObserver`'s optional members have default empty bodies, so a decorator that omits one **compiles,
satisfies the interface, and swallows the event in every mode**. The chain is

    OnTheFlyDiagramObserver -> OnTheFlyLogSiteObserver -> ObserverProjection -> RunEventStream -> renderer

so a missing forward at the OUTERMOST decorator means `events.jsonl` never hears the event at all.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs`,
`tests/Guardrails.Integration.Tests/WaveGateForwardingTests.cs` and
`tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelForwardingTests.cs`. After this task
completes, the harness runs a `git diff` check and rejects any edit outside these paths — including
changes to production files or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### The file and the class

Create `tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs`, class
**`ObserverForwardingSweepTests`**, every test carrying `[Trait("Category", "RunEvents")]`. The class
name is pinned: this task's guardrails filter on it.

### The three tests — these exact method names

**1. `EveryTransparentDecorator_DeclaresEveryIRunObserverMember`** — MUST BE RED.

One exhaustive sweep over `typeof(IRunObserver).GetMethods()`, across **both assemblies**, asserting
each of the four transparent decorators declares every member:
`Guardrails.Core.Execution.RunEventStream`, `Guardrails.Core.Execution.ObserverProjection`,
`Guardrails.Cli.Ui.OnTheFlyDiagramObserver`, `Guardrails.Cli.Ui.OnTheFlyLogSiteObserver`.

The two existing reflection guards sweep **only the CLI assembly**, so the two Core projections are
unguarded today. That is the hole this closes.

**Non-vacuity floor — this is what stops the test passing by discovering nothing:** assert the member
list is non-empty AND that all four decorator types were actually resolved. A reflection sweep whose
type lookup silently returned nothing passes every assertion while proving nothing. Report the missing
`(type, member)` pairs in the failure message, one per line, so a retry knows exactly what to add.

At authoring time all four declare all 20 current members, so this test is green on today's code and
goes red **only** on the newly-added `RunFinished`. Verify that yourself rather than assuming it.

**2. `RunFinished_ReachesTheEventStream_ThroughTheWholeChain`** — MUST BE RED.

A behavioural forward test, not a reflection one: build the real chain, raise `RunFinished` at the
outermost decorator, and assert it arrives at the innermost observer. Use a recording `IRunObserver`
as the innermost. This is the test that would still catch a decorator that declares the member and
then does not call `_inner`.

**3. `TheTwoRenderers_DoNotDeclareRunFinished_BecauseTheyAreDisposedFirst`** — will be GREEN, and that
is correct.

Assert by reflection that `LiveRunObserver` and `ConsoleRunObserver` do **NOT** declare `RunFinished`.
This is not a style rule: `await using var liveObserver` is scoped to the `if (live)` block in
`RunCommand`, so `RunFinished` is the first `IRunObserver` call ever made on the chain **after** the
Spectre live loop has been torn down. Declaring it on a renderer is a use-after-dispose. Say that in
the test's own comment — a style rationale ("a renderer that doesn't render it") would be accepted on
its merits by the next reader who thinks a completion line would look nice.

**This test is a DECLARED EXEMPTION from the red census**: a correct implementation leaves it green,
so demanding it be red would demand a correct implementation fail. This task's census guardrail
requires it to have EXECUTED, not to have failed.

### Retire the two superseded reflection halves

`WaveGateForwardingTests` and `AttemptModelForwardingTests` each carry a reflection half that the new
exhaustive sweep subsumes. **Remove those reflection halves** rather than leaving three overlapping
sweeps. Keep each file's behavioural tests exactly as they are — read each file first and remove only
the reflection-over-`IRunObserver`-members portion.

### Done when

The test project **compiles** (a test that does not compile is not a red — it is a broken test), tests
1 and 2 **fail**, and test 3 executes. Do NOT implement `RunFinished` on any decorator: that is task 03.
