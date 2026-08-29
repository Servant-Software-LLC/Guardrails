## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "03-replace-meta-refresh": { "someKey": "someValue" } }`. The harness
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

Replace the live diagram's whole-document `<meta http-equiv="refresh" content="3">` with an
**in-place status update**, so pan/zoom and scroll survive, clicks stop being racy, and the page
stops polling once the run reaches a terminal state.

**Files you may write:**

1. `src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs` — the change.
2. `tests/Guardrails.Core.Tests/Graph/DiagramRefreshTests.cs` — the new tests (class
   **`DiagramRefreshTests`**, every test carrying `[Trait("Category", "BacklogSlate")]`; both are
   load-bearing, this task's guardrail filters on them).
3. `tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs` — **only** to retire the one assertion
   your change makes false (see "The two existing tests that block this" below).
4. `tests/Guardrails.Integration.Tests/OnTheFlyDiagramTests.cs` — same, for its one assertion.
5. `tests/Guardrails.Integration.Tests/RunCommandFinalSiteSettleTests.cs` — same, for the ONE
   during-run **diagram** assertion only. Its neighbouring **index** assertion on the very next
   line stays; read the table below before touching this file.

**Scope boundary (harness-enforced):** Write only to those five paths. After this task completes,
the harness runs a `git diff` check and rejects any edit outside them — including
`MermaidRenderer.cs`, `OnTheFlyDiagramObserver.cs`, `LogServer.cs`, `LogSiteRenderer.cs` or any
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a
compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### What is wrong, precisely

`HtmlDiagramRenderer.Render(..., duringRun: true)` substitutes `__DURING_RUN_REFRESH__` with
`<meta http-equiv="refresh" content="3">`. That is a **whole-document reload every three seconds**:
the browser discards the SVG, re-runs `mermaid.render` over the full DAG, and re-initialises
`svg-pan-zoom` — so the operator's pan, zoom and scroll are destroyed on every tick, a click landing
mid-tick can be swallowed, and all of it is paid for a graph whose status changes only at task
boundaries.

### The design, and why it depends on task 02

`HtmlDiagramRenderer` is **pure** — it maps strings to a string with no I/O, and that must stay
true. So the page cannot read a status file itself; it has to ask a server. Task 02 (your
dependency, already merged) makes the log-site server serve this page at `/diagram.html`, which is
what makes an in-place update possible at all: the page can `fetch` its own URL, take the
`#node-status` JSON out of the returned document, and re-badge the existing SVG **without touching
the Mermaid render**.

The page already has everything this needs:

- the status map arrives as `<script type="application/json" id="node-status">`, read via
  `textContent`;
- `addStatusBadges(svgEl)` already walks that map and appends a badge per node;
- `GR_DURING_RUN` is already a JS boolean literal substituted from `__DURING_RUN__`;
- `resolveStatusNode` already resolves a node id to its SVG element.

So the update loop is: re-read the status JSON, remove the existing `.gr-status-badge` groups, and
call the badge builder again. **Nothing re-renders Mermaid, so the pan-zoom viewport is untouched.**

### The three outcomes, and the contract tokens the tests pin

Your tests and this task's guardrails agree on two literal names. Use them exactly.

1. **No whole-document reload.** `Render(..., duringRun: true)` must emit **no**
   `http-equiv="refresh"` at all. (`duringRun: false` already emits none — that stays true.)
2. **A bounded, named poll interval.** The during-run page carries a JS constant
   **`GR_LIVE_POLL_MS`** holding the poll interval in milliseconds, and its value must be **at least
   5000**. Three seconds was chosen for a whole-document reload; a DAG's status changes at task
   boundaries, which are minutes apart, so an in-place badge refresh has no reason to be that eager.
   Name the constant, do not inline the number — the tests read it, and a named constant is how
   `GR_DURING_RUN` already works in this template.
3. **It stops at a terminal state, and it says so when it cannot poll.** The **final** page
   (`duringRun: false`) must carry **no** `GR_LIVE_POLL_MS` at all, so a browser left open on the
   settled page polls nothing forever. And the during-run page, opened over `file://` where `fetch`
   is blocked, must show a plain notice rather than silently appearing live: give that notice the
   element id **`gr-live-offline`**, hidden by default and revealed when a poll fails, saying in
   words that this is not the live view and naming the served copy as the one that is.

Also stop the loop when a poll returns a document whose own `GR_DURING_RUN` is `false` — that is the
run settling, written by `OnTheFlyDiagramObserver.WriteFinalStatic`, and it is the terminal signal
the page can actually observe.

### Hash neutrality — the one thing that must not move

`source-sha256` is computed **upstream** over `MermaidRenderer.SemanticContent` and passed IN.
The provenance comment is the FIRST line of the file, before `<!doctype html>`, and `graph --check`
reads it with an `\A`-anchored reader. Everything you add is page chrome, exactly like `#bar`,
`#legend` and `#search` already are: it must never change the embedded Mermaid source, the
`source-sha256` line, or the hash inputs. Do not recompute the hash and do not feed the status map
into it.

### The three existing tests that block this, and the ONLY edit they may receive

Three tests assert the very thing you are removing. All three are in your write scope **for this
one purpose**; they are not yours to reorganise.

| File | Test | What to do |
|---|---|---|
| `tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs` | `Render_DuringRunTrue_InjectsMetaRefresh_AndActiveSpinner` | Its `Assert.Contains("http-equiv=\"refresh\"", …)` is now false. Retire that assertion and rename the test to what it still proves (the active spinner / `GR_DURING_RUN = true`). Keep the spinner half. |
| `tests/Guardrails.Integration.Tests/OnTheFlyDiagramTests.cs` | `DuringRun_Diagram_ShowsSpinnerThenSettledBadges_WithRefresh_ThenFinalHasNone` | Same: the during-run `Assert.Contains("http-equiv=\"refresh\"", d0)` is now false. The final page's `Assert.DoesNotContain(...)` stays true and stays. Rename the test to match what it now proves. |
| `tests/Guardrails.Integration.Tests/RunCommandFinalSiteSettleTests.cs` | `SettleAfterFault_SettlesBothPages_NoRefresh_NoFrozenSpinner` | **Read this row twice — it is the one place a careless edit destroys real coverage.** Its arrange phase asserts BOTH pages refresh during a run. Retire ONLY `Assert.Contains("http-equiv=\"refresh\"", temp.ReadDiagram())` (the diagram). The line immediately after it asserts the same of `temp.ReadIndex()` — that one is **still true and MUST stay**: #523 is diagram-only and the log-site index keeps its meta-refresh. The two later `Assert.DoesNotContain(...)` checks on the settled diagram stay as well; your change makes them more true, not less. Do not rename this test — what it proves is unchanged. |

**Do not touch anything else in those three files.** In particular
`Render_DuringRunFalse_HasNoMetaRefresh_AndInactiveSpinner`,
`Render_3ArgOverload_StillWorks_EmptyStatus_NoRefresh` and
`FinalStatic_SettlesStillRunningNodes_AsInterrupted_NotAFrozenSpinner` are all still true, still
valuable, and a guardrail fails this task if any of them disappears. Deleting a test to make a
suite green is the failure mode this task is most exposed to, precisely because the tests are in
scope.

### The behaviours to encode in `DiagramRefreshTests`, each bound to a PINNED test method name

`HtmlDiagramRenderer.Render` is a pure function from strings to a string, so its OUTPUT is the
observable — asserting on the returned document is the real behaviour here, not a proxy for it.
Author exactly these four methods, named verbatim (this task's guardrail greps for these names):

| Test method name | Behaviour |
|---|---|
| `DuringRunPage_HasNoMetaRefresh_SoPanZoomAndScrollSurvive` | `Render(source, hash, targets, status, duringRun: true)` contains **no** `http-equiv` at all. |
| `LivePoll_IsPresentDuringTheRun_AndAbsentOnTheFinalSettledPage` | The during-run page contains `GR_LIVE_POLL_MS`; the `duringRun: false` page does **not**. One test, both halves — the contrast IS the property, and asserting only the absence half would pass today against a page that has no poll at all. |
| `LivePollInterval_IsAtLeastFiveSeconds_ForADagThatChangesAtTaskBoundaries` | Parse the number out of the during-run page's `GR_LIVE_POLL_MS` assignment and assert it is `>= 5000`. Assert on the parsed integer, not on a literal string, so reformatting the constant cannot break it. |
| `FileViewFallback_IsPresentAndHidden_SoAnUnpollablePageSaysItIsNotLive` | The during-run page contains the `gr-live-offline` element, and it is hidden in the page's initial state (the notice appears only when a poll fails, never on a page that is polling fine). |

### One more test, deliberately NOT in that list

Also author `SourceSha256AndEmbeddedSource_AreUnchangedByTheLiveUpdateChanges`: render the same
inputs and assert the `<!-- guardrails:graph v1 source-sha256=… -->` first line and the embedded
`id="graph-source"` payload are exactly what was passed in. It **passes today**, which is why it is
not in the pinned list above — it is a regression pin, not evidence of the defect. It is here
because a change to this template that quietly moved the provenance line or re-encoded the source
would make `graph --check` report every plan in the repo stale, and nothing else in this plan would
notice.

### The bar

- Read the class remarks at the top of `HtmlDiagramRenderer.cs` before editing. They record why the
  Mermaid source is embedded verbatim in a `text/plain` script, why the overlays are appended
  post-render, and why the badges ride the pan-zoom transform for free. Your loop must keep every
  one of those properties.
- Keep the template a raw string literal with `__PLACEHOLDER__` substitution, and keep the final
  `\r\n` → `\n` normalisation — the file is asserted byte-identical across OSes.
- The badge refresh must be idempotent: polling twice with an unchanged status map must leave the
  DOM in the same state, not accumulate duplicate badges.
