## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "03-author-tests-diagram-refresh": { "someKey": "someValue" } }`. The harness
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

Author **failing xUnit.v3 tests** pinning what issue #523 is about: the live diagram page reloads
the WHOLE document every three seconds, so the operator's pan, zoom and scroll are destroyed on
every tick and a click landing mid-tick can be swallowed. The next task replaces that with an
in-place status update. **Your job is to encode the target behaviour as tests that FAIL today — not
to implement it.**

**Write exactly ONE file:**

1. `tests/Guardrails.Core.Tests/Graph/DiagramRefreshTests.cs` — the test file. The test class MUST
   be named **`DiagramRefreshTests`** and every test MUST carry
   `[Trait("Category", "BacklogSlate")]`. Both are load-bearing: this task's red census and the
   implementation task's forward check both filter on that class name conjoined with that trait, so
   a differently-named class or a missing trait makes both guardrails select zero tests. The
   `Graph/` subdirectory does not exist yet — create it; the test project globs `**/*.cs`, so no
   `.csproj` edit is needed (and one would be out of scope).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Graph/DiagramRefreshTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that one path — including
`src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs`, the neighbouring
`tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs`,
`tests/Guardrails.Integration.Tests/OnTheFlyDiagramTests.cs`,
`tests/Guardrails.Integration.Tests/RunCommandFinalSiteSettleTests.cs`, or any `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error
caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT implement the renderer change.** `HtmlDiagramRenderer.cs` is deliberately outside your
scope: task 04 makes these tests pass. A renderer edit here would both fail the write-scope check
and destroy the whole point of this task — a test that is green the moment it is written has
pinned nothing.

### There is NO stub file, and that is deliberate — read this before you reach for one

The usual test-author task writes minimal `NotImplementedException` stubs so the test project
compiles. **This one does not need them.** The only API these tests drive already exists and is
already public:

```csharp
public static string Render(
    string mermaidSource,
    string sourceHash,
    IReadOnlyDictionary<string, string> taskFolderTargets,
    IReadOnlyDictionary<string, string> statusByNodeId,
    bool duringRun)
```

`HtmlDiagramRenderer.Render` is a **pure function from strings to a string** — no disk, no clock,
no network. So your tests compile against today's code and fail against today's **OUTPUT**. That is
a stronger red than a stub tree: the failure is the real defect, not a thrown placeholder. **Do not
add a stub, do not add a `NotImplementedException`, and do not create a new production member** — a
new member would be an out-of-scope edit and would consume a retry.

### What the current template actually emits — measured, so you do not have to re-derive it

Read `src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs` before writing. Measured on the tree you
are handed:

- `Render(..., duringRun: true)` substitutes `__DURING_RUN_REFRESH__` with
  `<meta http-equiv="refresh" content="3">` (the ternary at the `string refresh = duringRun ? ...`
  line — grep for `__DURING_RUN_REFRESH__`, do not rely on a line number). `duringRun: false`
  substitutes the empty string.
- The token `GR_LIVE_POLL_MS` appears **nowhere** in `src/` or `tests/` today (measured: 0
  occurrences).
- The token `gr-live-offline` appears **nowhere** in `src/` or `tests/` today (measured: 0
  occurrences).
- The provenance comment `<!-- guardrails:graph v1 source-sha256=... -->` is the FIRST line of the
  output, before `<!doctype html>`, and the Mermaid source is embedded verbatim in
  `<script type="text/plain" id="graph-source">`.
- `const GR_DURING_RUN = true;` / `= false;` is already substituted from `__DURING_RUN__` — that is
  the naming precedent for the new constant, and it is why the constant below is *named* rather
  than inlined.

So four of your five tests fail today because the page has a meta refresh and has neither of the
two new tokens. That is the point.

### Copy the fixture shape that already works — do not invent one

`tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs` already exercises this exact function.
Read it first and mirror its fixture constants rather than re-deriving them:

- `private const string Hash = "abc123def456";`
- `private const string Source = "flowchart TD\n  task_a[\"a\"]:::task\n  classDef task fill:#cfe8ff;";`
- a `NoTargets` empty `Dictionary<string, string>(StringComparer.Ordinal)` and a `OneTarget`
  holding `["task_01_a"] = "tasks/01-a/"`,
- a small status map for the `statusByNodeId` argument.

`DiagramRefreshTests` is a **separate class in a separate file** — copy the constants you need into
it (they are `private` to `HtmlDiagramRendererTests` and not visible across classes), but do NOT
edit `HtmlDiagramRendererTests.cs`, which is outside your scope. Use
`StringComparison.Ordinal` on every string assertion, as the neighbouring file does.

### The contract tokens the next task must honour — use them EXACTLY

Your tests and the implementation task agree on two literal names. They are the contract; spelling
them differently makes the pair disagree and the implementation unable to go green.

1. **`GR_LIVE_POLL_MS`** — a JS constant on the during-run page holding the poll interval in
   milliseconds. Its value must be **at least 5000**. Three seconds was chosen for a whole-document
   reload; a DAG's status changes at task boundaries, which are minutes apart, so an in-place badge
   refresh has no reason to be that eager. It is a *named constant*, not an inlined number,
   precisely so a test can read it — `GR_DURING_RUN` already works that way in this template.
2. **`gr-live-offline`** — the element id of a notice that is **hidden in the page's initial
   state** and revealed only when a poll fails. Opened over `file://`, where `fetch` is blocked,
   the page must say in words that this is not the live view rather than silently appearing live.

### Group A — the behaviours that must be RED, each bound to a PINNED test method name

Author exactly these four methods, named verbatim — the red census greps for these names and
requires each one to be observed **Failed** against the current tree:

| Test method name | Behaviour |
|---|---|
| `DuringRunPage_HasNoMetaRefresh_SoPanZoomAndScrollSurvive` | `Render(Source, Hash, OneTarget, status, duringRun: true)` contains **no** `http-equiv` at all. Assert on the returned document, which IS the observable for a pure function. |
| `LivePoll_IsPresentDuringTheRun_AndAbsentOnTheFinalSettledPage` | The during-run page contains `GR_LIVE_POLL_MS`; the `duringRun: false` page does **not**. One test, both halves — the contrast IS the property, and asserting only the absence half would pass today against a page that has no poll at all. |
| `LivePollInterval_IsAtLeastFiveSeconds_ForADagThatChangesAtTaskBoundaries` | Parse the number out of the during-run page's `GR_LIVE_POLL_MS` assignment and assert it is `>= 5000`. Assert on the **parsed integer**, not on a literal string, so reformatting the constant cannot break it. Today there is nothing to parse, so make the "no match" path a clear assertion failure naming the missing constant — not an unhandled `NullReferenceException` or an out-of-range crash, which reads as a broken test rather than a verdict. |
| `FileViewFallback_IsPresentAndHidden_SoAnUnpollablePageSaysItIsNotLive` | The during-run page contains the `gr-live-offline` element, and it is hidden in the page's initial state (the notice appears only when a poll fails, never on a page that is polling fine). Assert both halves: presence alone would be satisfied by a notice that is visible on every page. |

### Group B — the ONE pin that is already GREEN today, and is NOT in the red census

This one must also be in the file. It **passes against the current tree**, so it is deliberately
excluded from the red census — it is a regression pin, not evidence of the defect. Say so in a
comment above it so the next reader does not think the census forgot it.

| Test method name | What it pins |
|---|---|
| `SourceSha256AndEmbeddedSource_AreUnchangedByTheLiveUpdateChanges` | Render the same inputs and assert the `<!-- guardrails:graph v1 source-sha256=... -->` FIRST line and the embedded `id="graph-source"` payload are exactly what was passed in. The next task adds page chrome; if it quietly moved the provenance line or re-encoded the source, `graph --check` would report every plan in the repo stale, and nothing else in this plan would notice. |

The census checks this one in the **opposite polarity**: it must be observed **Passed** in the same
run. Writing it hollow (`Assert.True(true)`) would satisfy that clause — do not; it is the only
thing standing between a chrome change and a repo-wide stale-diagram report.

### The bar

- **Every assertion goes through a real `HtmlDiagramRenderer.Render` call.** Do not assert on a
  string you built yourself, and do not construct the document you then check.
- A Group A test that PASSES today has not encoded the defect. If one of them is green when you run
  the suite, you have asserted something the current code already does — fix the test, do not
  weaken it. The census will tell you which one, by name.
- Do not assert on the meta refresh's exact `content="3"` value in Group A: the property is that
  the during-run page performs **no whole-document reload at all**, and pinning the interval would
  pass against a page that merely slowed the reload down.
- Keep the tests independent of line numbers and of the surrounding CSS — the next task adds page
  chrome around them, and a test keyed on document position will break for the wrong reason.
