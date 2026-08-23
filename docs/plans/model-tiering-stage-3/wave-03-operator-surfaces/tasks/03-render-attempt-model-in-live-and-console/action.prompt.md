## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-03-operator-surfaces/03-render-attempt-model-in-live-and-console": { "someKey": "someValue" } }`.
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

Make `AttemptModelRenderingTests` pass: implement the one shared summary formatter, and render it from
both observers when `AttemptModelResolved` fires.

Task 01 declared the event on `IRunObserver` and left a throwing stub for the formatter. You are
filling both in — in `src/Guardrails.Cli/Ui/LiveRunObserver.cs` and
`src/Guardrails.Cli/ConsoleRunObserver.cs`, and nowhere else.

### 1. `LiveRunObserver.AttemptModelSummary` — the ONE wording

```csharp
public static string AttemptModelSummary(string model, string? requestedModel)
```

Replace the `throw new NotImplementedException()` with the real formatter. The properties the tests pin:

- with a **non-null** `requestedModel`, the returned string contains **both** strings;
- with a **null** `requestedModel`, it contains `model`, does **not** contain what would have been the
  requested one, and is **not equal** to the two-argument form for the same `model`.

That last clause is the point of the whole wave: `requestedModel` is non-null **only** when the route
asked for something else, so its presence *is* the mismatch signal (read the `RequestedModel` doc
comment in `src/Guardrails.Core/Journal/JournalModel.cs`). A formatter that always renders one string
throws away the entire reason #349 exists; one that always renders two makes the two-string form carry
no information. **Pick the wording yourself** — the tests constrain the outcome, not the phrasing — but
make the mismatch case unmistakable to someone reading a log at 3am who did not ask for a second model.

It lives on `LiveRunObserver` as a public static for the same reason `StatusMarkup` and
`PostMortemPagePath` do: no live terminal renders in a non-interactive test, so a public pure function
is the only test seam the Spectre path has.

### 2. Both observers render it — through that one formatter

- **`ConsoleRunObserver.AttemptModelResolved`** — the plain/`--no-ui` surface. Write ONE line to
  `_output` under `_gate`, containing `LiveRunObserver.AttemptModelSummary(model, requestedModel)`
  verbatim. Follow the tagged idiom its neighbours already use (`[verifier-advisory] …`,
  `[overwatch] no verdict — …`; grep for `VerifierAdvisoryFound` in that file) rather than inventing a
  banner. `ConsoleRunObserver` already has `using Guardrails.Cli.Ui;`, so no new import is needed.
- **`LiveRunObserver.AttemptModelResolved`** — write it above the live region under `_gate`, exactly as
  `VerifierAdvisoryFound` and `OverwatchNoVerdict` do (grep for either). A raw `Console.Write` inside an
  active Spectre `Live` region corrupts the task table (#145/#372), which is why every one of those
  members goes through `AnsiConsole.MarkupLine` under the gate. **Escape** the harness-supplied strings
  with `Markup.Escape` — a model name containing a bracket would otherwise be read as Spectre markup —
  and build the rendered text from `AttemptModelSummary`, not from a second copy of the wording.

**Do not inline a second copy of the wording in `ConsoleRunObserver`.** One of the tests is an
agreement property — it asserts the console line CONTAINS the formatter's own output for the same
inputs — so a copy that is character-identical today passes and fails the moment either side is
edited, which is the only moment the rule matters. A separate deterministic guardrail also checks that
the live renderer builds its markup from the shared summary; that check exists because a Spectre write
is not observable from a headless test, so nothing else can see it.

**Do NOT touch the two on-the-fly decorators.** `OnTheFlyLogSiteObserver` and `OnTheFlyDiagramObserver`
are task 04's deliverable, and they are outside your writeScope.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/Ui/LiveRunObserver.cs` and
`src/Guardrails.Cli/ConsoleRunObserver.cs`. The harness runs a `git diff` check after this task and
rejects any edit outside those two paths — including the three test files, `IRunObserver.cs`,
`TaskExecutor.cs` and the two decorators. An out-of-scope edit fails the task immediately and consumes
a retry. **Do NOT edit the authored tests.** If a test in `AttemptModelRenderingTests` is genuinely
wrong or incompatible, write `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the
state-out path rather than changing it.

### Done when

`dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~AttemptModelRenderingTests"`
passes — four tests. `AttemptModelDisclosureTests` and `AttemptModelForwardingTests` belong to tasks 02
and 04, run in parallel with you, and your guardrail does not select them.
