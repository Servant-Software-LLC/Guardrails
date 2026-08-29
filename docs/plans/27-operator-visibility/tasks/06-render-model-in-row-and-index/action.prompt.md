## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "06-render-model-in-row-and-index": { "someKey": "someValue" } }`. The harness
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
task page. Turn the tests task 05 authored green — **without changing them**.

**The tests are the specification.** Read
`tests/Guardrails.Integration.Tests/ModelTiering/ModelInRowTests.cs` first; it is not in your write
scope and you may not edit it. Your guardrail runs the SAME filter that task 05's red census ran.

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

TWO siblings ran before you and both matter here:

- **Task 04** added `IRunObserver.AttemptRouteResolved(TaskNode task, int attempt, string runner,
  string model, string? tier, string? requestedTier)` — raised at attempt **LAUNCH**, before the
  action runs, and forwarded by both transparent decorators. It published its landed signature to the
  state you were handed; read that, and read `src/Guardrails.Core/Execution/IRunObserver.cs`, rather
  than trusting this prompt's spelling of it. **You do not add, move or change that event** — it is
  not in your write scope. You HANDLE it.
- **Task 05** added `LiveRunObserver.ModelCell(...)` as a `throw new NotImplementedException();` stub
  and authored the tests that drive it.

Everything below reflects the state at plan-authoring time; verify it before assuming.
`git log --oneline`, `git show` and a read of the three files are the fastest way to see what actually
landed. If a landed shape makes an instruction below impossible as written, implement the **intent**
and say so in your summary.

The design of record for this half is `docs/plans/29-model-visibility-ux.md` §1.1 and §4.1–§4.3. Read
those four short sections first. They are not optional context: they decide the column's width, what
the cell may contain, and which event fills it.

### Half A — the live task row (`LiveRunObserver.cs`)

Measured today: the table is built in the constructor as exactly three columns —
`AddColumn("Task")`, `AddColumn("Status")`, `AddColumn("Detail")`. Elapsed time is rendered *inside*
the Status cell by `Tick()`; **there is no cost column at all** — do not go looking for one to sit
beside.

1. **Implement `ModelCell(string? runner, string? tier, bool climbed, bool substituted,
   bool isScript)`.** It is already unit-tested by `ModelInRowTests`; make those tests pass. The cell
   carries the `promptRunners` **BLOCK NAME**, never the model id and never a mismatch sentence — the
   full disclosure is the line above the live region, and the cell is an *index into that line*, not a
   copy of it. `AttemptModelSummary` stays exactly as it is and stays the shared wording for the
   console line and the log site; it is simply not what the cell renders. (§3.3: the model id is 15–25
   characters and the mismatch sentence is 61 — MEASURED — and one such cell re-lays-out every other
   row in the table.)
2. **Append a `Model` column as the LAST column** —
   `_table.AddColumn(new TableColumn("Model").Width(8));` — so the cell is index **3**. Two things
   here, both load-bearing:
   - **Appending, not inserting.** `Update(...)` writes hard-coded cell indices 1 and 2 today, and
     `Tick()` writes index 1; inserting a column ahead of those would silently re-target every one of
     them, a rendering bug no test in this plan would catch.
   - **`Width(8)` was measured, not assumed.** Auto-sized, a 14-character block key steals 16 columns
     from every row for the whole run. Pinned at 8 it wraps inside its own cell and the cost is bounded
     to the affected row — at 80 columns the Task column keeps 23 characters with `Width(8)` and drops
     to 19 without it. Do **not** add `.NoWrap()`: Spectre wraps rather than truncating, and a
     truncated block name is a lie about which model ran.
3. **Populate it from the LAUNCH event, with the post-action event as the CORRECTION.** This is the
   whole change, and the reason task 04 exists:
   - **`AttemptRouteResolved(task, attempt, runner, model, tier, requestedTier)` is the primary
     source.** It fires *before* the action runs, so the cell names the block from the moment the
     attempt starts. Its `requestedTier` is non-null ONLY when a §6.2 climb moved the rung — its
     presence IS the climb signal — so that is what feeds `climbed`.
   - **`AttemptModelResolved(task, attempt, model, requestedModel)` stays where it is and becomes the
     confirmation or correction.** Its `requestedModel` is non-null ONLY when the provider served
     something else, so that is what feeds `substituted`. Keep the console line it already writes —
     that is what a `--no-ui` operator sees.
   - **Feeding the column from `AttemptModelResolved` ALONE is the defect this task exists to avoid.**
     That event cannot fire until the runner has reported what it ran on: MEASURED at 14m02s and longer
     per attempt on `docs/plans/24-plan-source-provenance/state/run.json`. A column fed only from it
     reads as a placeholder for the entire attempt and fills in exactly when the operator no longer
     needs it live — the same "still resolving" lie this plan already refuses in the other direction.
4. **Every `AddRow` in `RebuildRows()` must now pass four cells**, and the fourth is never blank. A
   Spectre table throws at RUN time — not compile time — when the cell count does not match the column
   count, so the compiler will not tell you. Seed the pending cell from what is already known at load:
   `(medium)` / `(easy)` / `(hard)` from the task's `Action.Tier` via the existing `_tasks` list,
   `(script)` for a script action, and `—` only when nothing is known. The parenthesis convention is
   the repo's own — `AttemptProvenance.Model` already spells a stand-in as `"(cli default)"` — and it
   is what makes the column never blank and never a placeholder that means nothing.
5. **Colour is redundant by construction, and introduces no new semantics.** Grey where the cell
   agrees, yellow where it carries `!` — exactly the pair `AttemptModelResolved` already spends today.
   Every one of those states is already said in text by `(…)`, `!` or the word itself, so a colourblind
   operator on a colour-capable terminal loses nothing. The Status column's palette is untouched.

**Explicitly NOT in this task, and do not add them.** The design of record also specifies a `[route]`
line for `ConsoleRunObserver` (§4.7) and a tiering-saving projection in `PrintSummary` (§5). Neither is
part of plan 27: §7 of that design lists exactly what changes here and neither is on it. Adding either
would put an untested surface in your diff and would not turn a single test green.

### Half B — the run-level index and the task page (`LogSiteRenderer.cs`)

Measured today: `IndexHtml` emits
`<thead><tr><th>Task</th><th>Status</th><th>Description</th></tr></thead>` and three `<td>`s per
task. `LogSiteRenderer.cs` contains **zero** rendered occurrences of the word "model".

1. **A `Model` column in the run-level index**, per task, carrying the **full model id** that
   actually ran and disclosing a route mismatch through the shipped
   `LiveRunObserver.AttemptModelSummary(model, requestedModel)` wording.
   **This half is unchanged by the live-table redesign, and the asymmetry is deliberate.** The live
   cell shows the eight-character block name because a Spectre table has a width crisis; HTML does
   not, and the log site is the AUDIT surface, so the id belongs here. Two resolutions of one fact,
   not two vocabularies: both are journaled fields (`provenance.Runner`, `provenance.Model`), neither
   is re-derived, and `attempt-route.log` names the pair together on every attempt.
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
- One vocabulary for one fact — with ONE deliberate exception, and it is not a licence to invent a
  second. The **index cell and the console line** must describe a route mismatch identically:
  `AttemptModelSummary` is the shipped wording, so route both through it and do not write a second
  disclosure sentence anywhere. The **live cell** is the exception: it carries the block name plus at
  most a `!` flag, because the sentence is 61 characters and would re-lay-out the whole table. The
  rule that keeps those two honest is that the cell says nothing the line does not — `sonnet` is a
  literal substring of what the line above the live region prints — so a reader cannot take them for
  two different facts.
- The index's existing Task / Status / Description columns, its `data-status` attributes, its
  link-vs-plain-text rule and the wave index's shape must all still work. This adds a column; it
  changes none of them.
