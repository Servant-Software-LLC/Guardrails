## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "05-render-model-in-row-and-index": { "someKey": "someValue" } }`. The harness
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

Surface the model that actually ran, in the two places that persist after the task finishes: the
**live task row** and the **run-level log index**, plus a named link to `attempt-route.log` on the
task page. Turn the tests task 04 authored green — **without changing them**.

**The tests are the specification.** Read
`tests/Guardrails.Integration.Tests/ModelTiering/ModelInRowTests.cs` first; it is not in your write
scope and you may not edit it. Your guardrail runs the SAME filter that task 04's red census ran.

**Files you may write:**

1. `src/Guardrails.Cli/Ui/LiveRunObserver.cs` — implement `ModelCell`, add the live table's Model
   column, and populate it.
2. `src/Guardrails.Cli/Ui/LogSiteRenderer.cs` — the run-level index's Model column and the task
   page's `attempt-route.log` link.
3. `src/Guardrails.Cli/ConsoleRunObserver.cs` — see "Probably nothing" below.

**Scope boundary (harness-enforced):** Write only to those three paths. After this task completes,
the harness runs a `git diff` check and rejects any edit outside them — including
`ModelInRowTests.cs`, `LiveTableRows.cs`, `OnTheFlyLogSiteObserver.cs`, anything under
`src/Guardrails.Core/`, or any `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Read the landed half FIRST — and treat this section as authoring-time state

`LiveRunObserver.ModelCell` was added as a `throw new NotImplementedException();` stub by task 04,
a sibling that ran before you. Everything below reflects the state at plan-authoring time; verify it
before assuming. `git log --oneline`, `git show` and a read of the three files are the fastest way to
see what actually landed. If a landed shape makes an instruction below impossible as written,
implement the **intent** and say so in your summary.

### Half A — the live task row (`LiveRunObserver.cs`)

Measured today: the table is built in the constructor as exactly three columns —
`AddColumn("Task")`, `AddColumn("Status")`, `AddColumn("Detail")`. Elapsed time is rendered *inside*
the Status cell by `Tick()`; **there is no cost column at all** — do not go looking for one to sit
beside.

1. **Implement `ModelCell(string? model, string? requestedModel)`.** It is already unit-tested by
   `ModelInRowTests`; make those tests pass. Delegate the mismatch wording to the existing
   `AttemptModelSummary(model, requestedModel)` rather than writing a second disclosure vocabulary.
2. **Append a `Model` column as the LAST column** — `AddColumn("Model")`, so the model is the 4th
   cell (index **3**). Appending, not inserting: `Update(...)` writes hard-coded cell indices 1 and
   2 today, and `Tick()` writes index 1; inserting a column ahead of those would silently
   re-target every one of them, which is a rendering bug no test in this plan would catch.
3. **Populate it.** Every `AddRow` in `RebuildRows()` must now pass four cells (a Spectre table
   throws when the count does not match the column count, so the compiler will not tell you —
   the run will). `AttemptModelResolved(task, attempt, model, requestedModel)` is where the model
   arrives; it currently only writes a line above the live region, which is the whole of #524's
   complaint. Make it write the model into that task's row **as well** — keep the existing console
   line, it is what a `--no-ui` operator sees.
4. A task with no resolved model yet shows `ModelCell`'s placeholder, never a blank cell.

### Half B — the run-level index and the task page (`LogSiteRenderer.cs`)

Measured today: `IndexHtml` emits
`<thead><tr><th>Task</th><th>Status</th><th>Description</th></tr></thead>` and three `<td>`s per
task. `LogSiteRenderer.cs` contains **zero** rendered occurrences of the word "model".

1. **A `Model` column in the run-level index**, per task, carrying the model that actually ran and
   disclosing a route mismatch the same way Half A does.
2. **Where the data comes from, and the one signature constraint that matters.** `IndexHtml` and
   `WriteIndex` take **resolver delegates**, not the journal. `ExportSite` — in this same file, and
   therefore in your write scope — already has the whole `JournalDocument`, and the model lives at
   `journal.Tasks[taskId].Attempts[n].Provenance.Model` / `.RequestedModel`. So `ExportSite` can
   build a model resolver and pass it down with no change to any caller outside this file.
   **`WriteIndex` is public and is also called by `OnTheFlyLogSiteObserver`, which is NOT in your
   write scope** — so any parameter you add to it must be **optional with a default**, or that
   caller stops compiling and you are stuck.
3. **State the boundary rather than letting it be discovered.** With `ExportSite` supplying the
   resolver, the **final / `--export`** index shows the model and the **during-run** index (written
   by `OnTheFlyLogSiteObserver`) does not. That is the right half to fix here: #524 was raised about
   a task that had **already finished**, and the during-run index is exactly the transient surface
   the issue says cannot answer the question. Note the split in your summary so the follow-on is a
   decision someone makes, not a gap someone trips over.
4. **Link `attempt-route.log` by name from the task page.** `TaskPage` already inlines *every* file
   in an attempt directory as a `<select>` option plus a hidden `<pre>` — so the filename is
   technically on the page and answers nobody. Add a real `<a>` element pointing at that file, with
   a label that says what it answers (it must name the **model**). Put it where a reader looking at
   an attempt would find it, in the page's existing idiom — the `<div class="bar">` rows and the
   Source section are both established patterns; do not invent a third.

### Probably nothing — `ConsoleRunObserver.cs`

Its `AttemptModelResolved` already writes
`[model] {task.Id} attempt {attempt}: {LiveRunObserver.AttemptModelSummary(model, requestedModel)}`,
which is correct and already covered by `AttemptModelRenderingTests`. It is in your write scope only
so that a change to the shared formatter can be reflected here in the same change-set. **If you do
not need to touch it, do not** — a gratuitous edit to an observer file is a merge hazard this plan's
whole serial shape exists to avoid.

### The bar

- Do not edit the tests. If a test looks wrong, that is a `needsHuman` with the two quotes the
  harness contract asks for, not an edit.
- One vocabulary for one fact: the live row, the index cell and the console line must all describe a
  route mismatch the same way. `AttemptModelSummary` is the shipped wording — route the others
  through it.
- The index's existing Task / Status / Description columns, its `data-status` attributes, its
  link-vs-plain-text rule and the wave index's shape must all still work. This adds a column; it
  changes none of them.
