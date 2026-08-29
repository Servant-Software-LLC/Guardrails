## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "06-author-tests-model-in-row": { "someKey": "someValue" } }`. The harness
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
2. `src/Guardrails.Cli/Ui/LiveRunObserver.cs` — **two** minimal skeleton stubs, described under "The
   two stubs, and only them" below, each with the body `throw new NotImplementedException();`.

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

### What `05-raise-attempt-route-resolved` already landed, and why it changes what you assert

`05-raise-attempt-route-resolved` (your dependency, already merged) added
**`IRunObserver.AttemptRouteResolved(TaskNode task, int attempt, string runner, string model,
string? tier, string? requestedTier)`** — raised at attempt **LAUNCH**, before the action runs, and
forwarded by both transparent decorators. Read its landed signature out of
`src/Guardrails.Core/Execution/IRunObserver.cs` (and out of your state-in fragment, where that task
published it) rather than trusting this paragraph. (This plan was RENUMBERED after these prompts were
drafted, so task folders are referred to by NAME — never trust a bare ordinal you find in prose here.)

That event is why the live cell's contract is **not** two model-id strings. The design of record is
`docs/plans/29-model-visibility-ux.md` §4.1–§4.3; read those three short sections before you write a
line, and §1.1 for the measurement behind them. The two facts that matter here:

- **`AttemptModelResolved` fires only after the attempt's action returns** — MEASURED at 14m02s and
  longer per attempt on `docs/plans/24-plan-source-provenance/state/run.json`. A cell fed only from it
  is a placeholder for the whole attempt, which is the same "still resolving" lie this prompt already
  forbids in the settled-task direction.
- **The live cell carries the `promptRunners` BLOCK NAME, never the model id and never the mismatch
  sentence.** `AttemptModelSummary`'s wording is 61 characters (measured); at `Width(8)` in a Spectre
  table one such cell re-lays-out every other row. The model id stays on the log-site row, where HTML
  has no width crisis.

### The two stubs, and only them

The live console table needs a **pure, testable seam** for its model cell, because the table itself
is not reachable from a test (see "What this task deliberately does NOT pin"). Add exactly two
members to `LiveRunObserver`:

```csharp
public static string ModelCell(
    string? runner, string? tier, bool climbed, bool substituted, bool isScript) =>
    throw new NotImplementedException();

public static string ModelCellFromRoute(string runner, string? tier, string? requestedTier) =>
    throw new NotImplementedException();
```

**Why the second one exists, and why it is not redundant.** `ModelCell` is the formatter; the
`AttemptRouteResolved` handler still has to *translate* the launch event into that formatter's
arguments, and that translation carries the one rule the whole event turns on — **`climbed` is
`requestedTier is not null`**, because `requestedTier` is written ONLY when a §6.2 climb moved the
rung, so its PRESENCE is the signal. If that translation lives inline in the handler it is
unreachable from any test (the handler is on a type no test may construct), so the only thing left to
check it is a regex that cannot see whether the handler's body does anything at all — which is
exactly how a handler that logs a line and drops the event on the floor ships green. Pulling the
translation into a `static` pure function converts an untestable wiring hop into a **testable pure
function** (the #468 demotion, in the direction it is meant to be applied), and shrinks what no test
can reach to two statements: call it, write the result into the cell.

`ModelCellFromRoute` must **delegate** — it computes `climbed` and calls `ModelCell`; it does not
re-implement the formatting. Its test asserts that AGREEMENT, not a second copy of the expected
strings, so an inlined divergent copy fails the moment the two disagree.

**Five parameters, not two, because the cell has SIX distinguishable states and not one of them is
expressible from two model-id strings** (design §4.2). This is the table; it is what your tests
assert:

| Moment | Condition | Cell |
|---|---|---|
| row built | prompt task, rung known at load (`ActionDefinition.Tier`) | `(medium)` |
| row built | prompt task, untagged (no rung) | `—` |
| row built | script action | `(script)` |
| route resolved (launch) | served rung == requested rung | `sonnet` |
| route resolved (launch) | a §6.2 climb moved the rung | `sonnet !` |
| model observed (attempt end) | the provider served a different model | `sonnet !` |

The parenthesis convention is the repo's own, not an invention: `AttemptProvenance.Model` already
spells a stand-in as `"(cli default)"`. `(medium)` reads the same way — *planned, not yet actual* —
and it is what makes the column **never blank**. `!` is a pointer, not a code: it means "the route did
not get what it asked for", it never appears without a full-prose line above the live region saying
which of the two causes it was, and it is the only flag.

**One §4.2 row is deliberately absent from that table, and you must not add a test for it.** §4.2
also lists a `no route` cell for the §6.2 no-candidate outcome. It is **not reachable** through this
signature or through the event that fills the cell: `TaskExecutor` settles a no-route attempt and
**returns** before the raise site §4.3 pins for `AttemptRouteResolved`, and a no-route resolution has
no runner name and no model to hand the event anyway. A test asserting a `no route` cell would be
asserting a state the harness cannot produce — a check no correct implementation can satisfy. Leave
it out; it is a follow-on for whoever implements the rest of design 29, not a gap in this task.

Place them beside the existing `public static string StatusMarkup(...)` and
`public static string AttemptModelSummary(string, string?)` — that group is the file's established
convention for exactly this reason: a pure formatter a test can drive without touching the live
region. Do not implement either, do not call them from anywhere, and do not add a third member.
Nothing calls them yet, so adding them changes no behaviour and breaks no existing test.

**Do NOT change `AttemptModelSummary`.** It stays exactly as it is and stays the shared wording for
the log site and the console line. What the design drops is its use **in the cell** — one surface,
not the vocabulary.

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

Author exactly these eight methods, named verbatim — the red census greps for these names and
requires each one to be observed **Failed** against the current tree. The first five are red
because the renderer does not yet emit the model; the last three are red because `ModelCell` and
`ModelCellFromRoute` are the stubs you just wrote:

| Test method name | Behaviour |
|---|---|
| `RunLevelIndex_HasAModelColumn_BesideStatusAndDescription` | After `ExportSite`, the run-level `index.html` header row declares a **Model** column alongside the existing Task / Status / Description ones. |
| `RunLevelIndex_ShowsTheModelThatActuallyRan_PerTask` | Two tasks whose journal entries record **different** models (e.g. `claude-sonnet-5` and `claude-opus-5`): each task's own row carries its own model. Assert per row, not per document — a page-wide `Assert.Contains` would pass on a single hard-coded value. |
| `RunLevelIndex_DisclosesTheMismatch_WhenTheRouteRequestedADifferentModel` | A task whose `Provenance.RequestedModel` is non-null and differs from `Provenance.Model`: the row discloses **both**, so an operator can see the route did not get what it asked for. `LiveRunObserver.AttemptModelSummary(model, requestedModel)` is the shipped formatter for exactly this and is visible from this test project — pin the shared wording rather than inventing a second one. |
| `RunLevelIndex_MarksATaskWithNoRecordedModel_RatherThanRepeatingItsNeighbours` | A never-run task (no attempts, no provenance) listed beside a task that ran: its Model cell is a neutral placeholder, and specifically is **not** the neighbouring task's model. The cheapest wrong implementation carries the last value forward; this is the test that catches it. |
| `TaskPage_LinksAttemptRouteLogByName_WithALabelSayingWhatItAnswers` | Write a real `attempt-route.log` into the attempt directory. After `ExportSite`, the task page contains an **`<a>` element whose href names `attempt-route.log`**, and whose visible label names what it answers (it must contain the word `model`). Do NOT assert merely that the string `attempt-route.log` appears — it already does, as an inlined `<select>` option, and that assertion is green today. |
| `LiveTableModelCell_NamesTheModel_AndDisclosesTheRouteMismatch` | The three RESOLVED states, driven through `LiveRunObserver.ModelCell(...)` directly. A route whose served rung equals the requested one gives the bare block name (`sonnet`). A §6.2 climb (`climbed: true`) and a provider substitution (`substituted: true`) each append the single flag `!` and nothing else (`sonnet !`). Assert **the width bound** — every cell this test produces is ≤ 8 visible characters — and assert the two things the cell must NEVER be: it does not contain `AttemptModelSummary`'s mismatch sentence, and it does not contain a model id such as `claude-sonnet-5`. |
| `LiveTableModelCell_RendersAPlaceholder_WhenNoModelIsRecorded` | The three ROW-BUILD states, which are the **common** live state and not the exceptional one. `(medium)` / `(easy)` / `(hard)` when a rung is known at load; `(script)` when `isScript` is true; `—` **only** when nothing at all is known — no block, no rung, not a script. Never an empty string, never a crash. An empty cell in a live table reads as "still resolving", which is a wrong claim about a task that already finished *and* about a task that is running healthily on a route the harness resolved before it launched. |
| `LiveTableModelCellFromRoute_MapsTheLaunchEvent_AndFlagsAClimb` | The launch-event translation, driven through `LiveRunObserver.ModelCellFromRoute(runner, tier, requestedTier)` directly. Assert it as an **AGREEMENT property**, not as a second copy of the expected strings: over a small matrix of real block names (`haiku`, `sonnet`, `opus`) × rungs (`easy`, `medium`, `hard`, `null`) × `requestedTier` (`null`, and a *different* rung), `ModelCellFromRoute(runner, tier, requestedTier)` must equal `ModelCell(runner, tier, climbed: requestedTier is not null, substituted: false, isScript: false)` for **every** input. Then pin the rule the agreement is about, in two concrete cases: `requestedTier: null` gives the bare block name (`sonnet`), and a non-null `requestedTier` gives `sonnet !`. An equality-over-the-domain assertion is what makes an inlined divergent copy fail the moment the two disagree — a hand-written expected-string list would keep passing until someone changed one of them. |

**Three mechanics for those last three rows, so you do not have to guess them.**

- **"Visible characters" means after Spectre markup is removed.** Whether `ModelCell` returns bare
  text or `[grey]…[/]`-wrapped markup is `07-render-model-in-row-and-index`'s call — the file's sibling `AttemptModelSummary`
  returns plain text and its caller adds the colour, but `docs/plans/29-model-visibility-ux.md` §4.2
  assigns a colour per state, so either shape is defensible. Measure width through a tiny local
  helper that strips `\[[^\]]*\]` before counting, so the assertion is true under both and cannot be
  satisfied by an implementation that pads the cell with markup.
- **The ≤ 8 bound is asserted over the states this test drives, and the design says why that is not
  the whole story.** §4.1's own worked example renders a 14-character block key (`local-qwen-32b`)
  wrapping *inside* its `Width(8)` cell, and §10 risk 3 accepts that wrap rather than truncating —
  truncation would misname the model. So ≤ 8 is a property of every block this repo actually
  configures (`haiku`, `sonnet`, `opus` — 5, 6 and 4 characters) and of every parenthesised rung
  (`(medium)` is exactly 8), **not** an invariant over arbitrary operator-chosen keys. Drive it with
  those real block names. The bound that IS unconditional is the pair of negative assertions above:
  no mismatch sentence, no model id. Assert both; they are what §3.3 exists to protect.
- **`ModelCellFromRoute` is asserted by AGREEMENT, and that is what makes it worth having.** Do not
  write `Assert.Equal("sonnet !", ModelCellFromRoute("sonnet", "hard", "medium"))` and stop — that is
  a second, independent copy of the formatter's contract, and it goes stale silently. Compare the two
  functions across the whole small input domain and assert they agree. An implementation that inlines
  a divergent copy of the formatting into `ModelCellFromRoute` passes a string-literal test today and
  fails an agreement test the moment the two drift, which is the only moment the rule matters.

### Group B — a pin that is ALREADY GREEN today, and is NOT in the census

Author **two** pins here. Both pass against the tree you are handed, so both are deliberately
excluded from the red census — they are regression pins, not evidence of the defect. Say so in a
comment above each one, so the next reader does not think the census forgot them.

1. `RunLevelIndex_StillCarriesTaskStatusAndDescription_SoTheModelColumnIsAdditive`: the index still
   declares its Task, Status and Description columns and still renders a settled task as a link to
   its page with its `data-status` attribute.
2. `BothDecorators_ForwardAttemptRouteResolved_ToTheirInnerObserver`: `05-raise-attempt-route-resolved` added
   `IRunObserver.AttemptRouteResolved` with a **default no-op body**, so a decorator that stops
   forwarding it still compiles, still satisfies the interface, and silently drops the disclosure for
   every operator in every mode. That task's own guardrail is a source grep; this is the runtime pin
   that outlives it, and it is the check that catches a *later* change breaking the forward.
   `tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelForwardingTests.cs` is the exact
   pattern — read it and mirror it: build each decorator over a `RecordingObserver` inner, invoke
   **through the `IRunObserver` interface** and never the concrete type, and drive it in **both**
   shapes (`requestedTier` present, `requestedTier` null), because a decorator that hard-coded null
   would satisfy a one-shape test while destroying the climb signal. Constructing these two
   decorators is safe and is not the forbidden construction below — the ban is on `LiveRunObserver`,
   whose constructor starts a process-wide Spectre live region. Do NOT edit
   `AttemptModelForwardingTests.cs`; it is outside your write scope. Copy the shape into
   `ModelInRowTests`.

### What this task deliberately does NOT pin, and why you must not try

The live console **table itself** — that it declares a Model column and that the column is actually
populated — is **not testable**, which is why the pure `ModelCell` seam exists. The table is a
private Spectre `Table` field; the `LiveRunObserver` constructor immediately starts an
`AnsiConsole.Live` region plus a one-second `Timer`; and Spectre's live-display lock is
**process-wide**, so constructing one inside a suite that runs in parallel corrupts other tests'
output (the repo has already had to serialize its live-display tests for exactly this reason). So:

- **Do NOT construct a `LiveRunObserver`** in these tests, in any test, for any reason.
- **Do NOT add a reflection probe into an instance of it** — do not call a member-lookup method
  (`GetMethod` / `GetField` / `GetProperty` / `GetMember`) on `typeof(LiveRunObserver)`, and do not
  pass the name of one of its private members (`RebuildRows`, `Update`, `Tick`, `_table`,
  `_rowByKey`) to any of those lookups.
- Test `ModelCell` and `ModelCellFromRoute` as the pure functions they are. Wiring those seams into
  the table is proven by a structural guardrail on the implementation task, and the residual — that a
  regex sees the call, not the cell — is stated there rather than papered over here.

**Both of those prohibitions are BACKED BY A GUARDRAIL** — `03-tests-do-not-construct-the-live-observer.ps1`
fails on a construction of the observer, on a member lookup against its `typeof`, and on a quoted
private member name passed to reflection (#221: a prohibition with nothing behind it is free to ignore, and this one
is not cosmetic — a violation surfaces as a FLAKE in an unrelated test at the terminal Integration
gate, attributed to whatever ran last). Two things it deliberately does **not** ban, so you are not
guessing at the edges:

- **Constructing the two DECORATORS is fine** — `OnTheFlyDiagramObserver` and `OnTheFlyLogSiteObserver`
  touch no live region, and the Group B forwarding pin needs them.
- **`BindingFlags` and a TYPE-level reflection sweep are fine**, including
  `typeof(LiveRunObserver).Assembly` — that is the pattern `AttemptModelForwardingTests` already ships
  and which this prompt points you at. The ban is on reaching into an *instance* of `LiveRunObserver`,
  never on the reflection API.

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
