## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "10-author-tests-model-in-row": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
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

Author **failing xUnit.v3 tests** pinning issue #524: the run records which model ran and never
surfaces it anywhere that persists.

**Write exactly two files:**

1. `tests/Guardrails.Integration.Tests/ModelTiering/ModelInRowTests.cs` — the test file. The test
   class MUST be named **`ModelInRowTests`** and every test MUST carry
   `[Trait("Category", "BacklogSlate")]`. Both are load-bearing: this task pair's guardrails filter
   on that class name conjoined with that trait.
2. `src/Guardrails.Cli/Ui/LiveRunObserver.cs` — **one** minimal skeleton stub, described under "The
   one stub, and only it" below, whose body is `throw new NotImplementedException();`.

**Scope boundary (harness-enforced):** Write only to those two paths. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — including
`src/Guardrails.Cli/Ui/LogSiteRenderer.cs`, `src/Guardrails.Cli/ConsoleRunObserver.cs`,
`src/Guardrails.Cli/Ui/LiveTableRows.cs`, the neighbouring `LogSiteExportTests.cs` /
`AttemptModelRenderingTests.cs`, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT
edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### The HTML half needs no stub, and that is deliberate

Every API the log-site tests drive is already public and already does something:
`LogSiteRenderer.ExportSite`, `LogSiteRenderer.WriteTaskPageIfHasAttempts`, `JournalDocument` /
`TaskJournalEntry` / `AttemptRecord` / `AttemptProvenance`. Those tests **compile against today's
code and fail against today's OUTPUT** — the model is simply not in the HTML. That is a stronger red
than a stub tree, so do not add stubs for them.

### The one stub, and only it

The live console table needs a **pure, testable seam** for its model cell, because the table itself
is not reachable from a test (see "What this task deliberately does NOT pin"). Add exactly one
member to `LiveRunObserver`:

```csharp
public static string ModelCell(string? model, string? requestedModel) =>
    throw new NotImplementedException();
```

Place it beside the existing `public static string StatusMarkup(...)` and
`public static string AttemptModelSummary(string, string?)` — that trio is the file's established
convention for exactly this reason: a pure formatter a test can drive without touching the live
region. Do not implement it, do not call it from anywhere, and do not add a second member. Nothing
calls it yet, so adding it changes no behaviour and breaks no existing test.

### What is measured today — this is the defect, and it is also the trap

- The run-level `logs/<runId>/index.html` has header row
  `<thead><tr><th>Task</th><th>Status</th><th>Description</th></tr></thead>` and per-task cells
  `Task | Status | Description`. There is **no Model column and no cost or duration column**.
- `src/Guardrails.Cli/Ui/LogSiteRenderer.cs` contains **zero** rendered occurrences of the word
  "model" (the only two case-insensitive hits are the `using Guardrails.Core.Model;` namespace and
  one doc-comment sentence about the four-folder model).
- **`attempt-route.log` IS already in the task page — and that is the trap.** `AppendAttemptFiles`
  inlines *every* file in an attempt directory as a `<select>` option plus a hidden `<pre>`, so a
  test asserting only that the page CONTAINS the string `attempt-route.log` is **green today** and
  proves nothing. What is missing is a **named link with a label saying what it answers**: the page
  has no `<a>` element pointing at that file, and never says the word "model" anywhere. Assert on
  the anchor and the label, not on the bare filename.
- The journal already holds everything needed:
  `journal.Tasks[taskId].Attempts[n].Provenance.Model` (the model that actually ran) and
  `.Provenance.RequestedModel` (written **only** when the route asked for a different one, so its
  presence *is* the mismatch signal). `ExportSite` already receives the whole `JournalDocument`.

### Copy the fixture shape that already works — do not invent one

`tests/Guardrails.Integration.Tests/LogSiteExportTests.cs` builds exactly the fixture you need: a
temp `logsRoot`, hand-made `attempt-1` directories with a log file in them, an in-memory
`JournalDocument { RunId, PlanHash, Tasks = { [id] = new() { Status = … } } }`, a call to
`LogSiteRenderer.ExportSite(logsRoot, tasks, journal)`, and assertions over the resulting
`index.html` / `<taskId>/index.html`. Read it first and mirror it — including its `FakeTask` helper
and its `finally { Directory.Delete(logsRoot, recursive: true); }`. Those helpers are `private` to
that class, so copy what you need into `ModelInRowTests`; do NOT edit `LogSiteExportTests.cs`.

Your journal fixtures additionally populate `Attempts` with an `AttemptRecord` carrying a
`Provenance`. Read `src/Guardrails.Core/Journal/JournalModel.cs` for the exact record shapes rather
than guessing at the constructor.

### Group A — the behaviours that must be RED, each bound to a PINNED test method name

Author exactly these seven methods, named verbatim — the red census greps for these names and
requires each one to be observed **Failed** against the current tree. The first five are red
because the renderer does not yet emit the model; the last two are red because `ModelCell` is the
stub you just wrote:

| Test method name | Behaviour |
|---|---|
| `RunLevelIndex_HasAModelColumn_BesideStatusAndDescription` | After `ExportSite`, the run-level `index.html` header row declares a **Model** column alongside the existing Task / Status / Description ones. |
| `RunLevelIndex_ShowsTheModelThatActuallyRan_PerTask` | Two tasks whose journal entries record **different** models (e.g. `claude-sonnet-5` and `claude-opus-5`): each task's own row carries its own model. Assert per row, not per document — a page-wide `Assert.Contains` would pass on a single hard-coded value. |
| `RunLevelIndex_DisclosesTheMismatch_WhenTheRouteRequestedADifferentModel` | A task whose `Provenance.RequestedModel` is non-null and differs from `Provenance.Model`: the row discloses **both**, so an operator can see the route did not get what it asked for. `LiveRunObserver.AttemptModelSummary(model, requestedModel)` is the shipped formatter for exactly this and is visible from this test project — pin the shared wording rather than inventing a second one. |
| `RunLevelIndex_MarksATaskWithNoRecordedModel_RatherThanRepeatingItsNeighbours` | A never-run task (no attempts, no provenance) listed beside a task that ran: its Model cell is a neutral placeholder, and specifically is **not** the neighbouring task's model. The cheapest wrong implementation carries the last value forward; this is the test that catches it. |
| `TaskPage_LinksAttemptRouteLogByName_WithALabelSayingWhatItAnswers` | Write a real `attempt-route.log` into the attempt directory. After `ExportSite`, the task page contains an **`<a>` element whose href names `attempt-route.log`**, and whose visible label names what it answers (it must contain the word `model`). Do NOT assert merely that the string `attempt-route.log` appears — it already does, as an inlined `<select>` option, and that assertion is green today. |
| `LiveTableModelCell_NamesTheModel_AndDisclosesTheRouteMismatch` | Drive `LiveRunObserver.ModelCell(model, requestedModel)` directly. With `requestedModel` null it names the model. With a `requestedModel` that differs, it names **both** — reuse `AttemptModelSummary`'s shipped wording rather than inventing a second disclosure vocabulary; two formatters for one fact is how the two drift. |
| `LiveTableModelCell_RendersAPlaceholder_WhenNoModelIsRecorded` | `ModelCell(null, null)` returns a neutral placeholder — never an empty string, never a crash. An empty cell in a live table reads as "still resolving", which is a different and wrong claim about a task that already finished. |

### Group B — a pin that is ALREADY GREEN today, and is NOT in the census

Also author `RunLevelIndex_StillCarriesTaskStatusAndDescription_SoTheModelColumnIsAdditive`: the
index still declares its Task, Status and Description columns and still renders a settled task as a
link to its page with its `data-status` attribute. It passes against the current tree, so it is
deliberately excluded from the red census — it is a regression pin, not evidence of the defect. Say
so in a comment above it so the next reader does not think the census forgot it.

### What this task deliberately does NOT pin, and why you must not try

The live console **table itself** — that it declares a Model column and that the column is actually
populated — is **not testable**, which is why the pure `ModelCell` seam exists. The table is a
private Spectre `Table` field; the `LiveRunObserver` constructor immediately starts an
`AnsiConsole.Live` region plus a one-second `Timer`; and Spectre's live-display lock is
**process-wide**, so constructing one inside a suite that runs in parallel corrupts other tests'
output (the repo has already had to serialize its live-display tests for exactly this reason). So:

- **Do NOT construct a `LiveRunObserver`** in these tests, in any test, for any reason.
- **Do NOT add a reflection probe** for its columns or its private `RebuildRows` / `Update` members.
- Test `ModelCell` as the pure function it is. Wiring that seam into the table is proven by a
  structural guardrail on the implementation task, and the residual — that a regex sees the call,
  not the cell — is stated there rather than papered over here.

### The bar

- Every assertion must go through the REAL `LogSiteRenderer` over a REAL temp `logsRoot`. Do not
  build the HTML you then assert on.
- Assert on the ROW, not the page, wherever the property is per-task. Extract the `<tr>` for the
  task id and assert inside it.
- Never write into the repository tree; build every fixture under `Path.GetTempPath()` and clean up
  in a `finally`.
- A test in Group A that PASSES today has not encoded the defect. If one of them is green when you
  run the suite, you have asserted something the current code already does — fix the test, do not
  weaken it. The `attempt-route.log` row is the one where this is most likely to happen.
