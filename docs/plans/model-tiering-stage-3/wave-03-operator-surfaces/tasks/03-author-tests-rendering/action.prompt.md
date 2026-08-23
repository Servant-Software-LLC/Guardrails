## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-03-operator-surfaces/03-author-tests-rendering": { "someKey": "someValue" } }`.
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

Author the **failing tests** for one operator surface of #349: the two renderers — the shared formatter, and both surfaces going through it.

Write ONE file: `tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelRenderingTests.cs`, holding
`public sealed class AttemptModelRenderingTests`. Task 01 has already added the three inert stubs these tests compile
against (the `IRunObserver.AttemptModelResolved` default member, the throwing
`LiveRunObserver.AttemptModelSummary`, and `Stage2PlanHarness.RunAsync`'s optional observer) — read
them before you start; do not add to them and do not implement anything.

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

### The behaviours, and the EXACT test-method name each must carry

A census guardrail reads a TRX result file and requires **every** name below to be present and
`Failed`. Name them exactly, and put every one of them in `AttemptModelRenderingTests` — the class name is
load-bearing, because `06-render-attempt-model-in-live-and-console` filters on it and a behaviour that
drifts into another class is invisible to both.

| test method | behaviour |
|---|---|
| `Summary_NamesBothModels_WhenTheRequestedModelIsPresent` | `LiveRunObserver.AttemptModelSummary(observed, requested)` contains BOTH strings |
| `Summary_OmitsTheRequestedModel_WhenItIsAbsent` | `AttemptModelSummary(observed, null)` contains the observed model, does **not** contain the requested one, and is **not equal** to the two-argument form — the one-string and two-string cases must be distinguishable |
| `ConsoleObserver_WritesTheSharedSummary_ForAttemptModelResolved` | a real `ConsoleRunObserver` over a `StringWriter`, called **through `IRunObserver`**, writes a line CONTAINING `LiveRunObserver.AttemptModelSummary(model, requestedModel)` — the agreement property that stops the plain surface growing its own second copy of the wording |
| `LiveObserver_DeclaresAttemptModelResolved_RatherThanInheritingTheEmptyDefault` | reflection: `typeof(LiveRunObserver).GetMethod("AttemptModelResolved", ...)` is non-null. A class that merely *implements* `IRunObserver` gets no member for a default method, so this is exactly the "silently inherited the empty body" failure |


### Scope boundary (harness-enforced)

Write only to `tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelRenderingTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that one path — including the two sibling
`AttemptModel*Tests.cs` files, `Stage2PlanHarness.cs`, `IRunObserver.cs`, `LiveRunObserver.cs`, any
implementation file, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in a file you may not write, do NOT edit
that file — write `{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the
state-out path and stop.

### The red must COMPILE

Failing is the point; **not compiling is a mistake to fix.** Task 01's stubs make every test above
compile: the interface member exists, `AttemptModelSummary` exists (and throws), and the harness accepts
an observer. Do NOT implement the behaviour — that is `06-render-attempt-model-in-live-and-console`.

Every one of these must be RED for a reason that is about this wave. A test that fails because it does
not compile, or because it asserts something already true, is not a red — the census names each one
individually so a hollow assertion cannot hide behind a genuinely-failing sibling.