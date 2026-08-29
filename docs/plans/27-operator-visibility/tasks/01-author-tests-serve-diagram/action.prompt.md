## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "01-author-tests-serve-diagram": { "someKey": "someValue" } }`. The harness
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

Author **failing xUnit.v3 tests** pinning what issue #522 is actually about: the live diagram
emits plan-folder-relative hrefs, and the log-site server does not serve the routes those hrefs
name — so the diagram can only be opened over `file://`, where the same paths resolve against the
flat, script-free `logs/<runId>/` layout and every click 404s.

**Write exactly ONE file:**

1. `tests/Guardrails.Integration.Tests/LogSite/ServeDiagramTests.cs` — the test file. The test class
   MUST be named **`ServeDiagramTests`** and every test MUST carry
   `[Trait("Category", "BacklogSlate")]`. Both are load-bearing: this task pair's guardrails filter
   on that class name conjoined with that trait.

**Scope boundary (harness-enforced):** After this task completes, the harness runs a `git diff` check
against this task's `writeScope` and rejects any edit outside it — including
`src/Guardrails.Cli/Ui/LogServer.cs`, the neighbouring `LogServerTests.cs` / `LogSiteExportTests.cs`,
anything under `src/Guardrails.Core/`, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

**This task writes exactly ONE file and nothing else**, and `writeScope` now says so: it lists that
one test file, so the harness's post-action `git diff` check ENFORCES the boundary rather than
merely asking for it. `src/Guardrails.Cli/Ui/LogSiteRenderer.cs` was removed from the scope on
2026-08-29 for exactly that reason — it was granted but forbidden in prose, and a production edit
here would be invisible to this task's guardrails and would land ahead of the task that is supposed
to make it (task 02). If you find yourself needing a production edit, that is the signal to write
`{"needsHuman": "<what is missing>"}` to the state-out path — not to reach for a wider scope.

### There is NO stub file, and that is deliberate — read this before you reach for one

The usual test-author task writes minimal `NotImplementedException` stubs so the test project
compiles. **This one does not need them.** Every API these tests drive already exists and is already
public:

- `LogServer.TryStart(planDir, runId, tasks, port: 0, TextWriter.Null)` returns a live loopback
  server, or `null` if it cannot bind.
- `server.BaseUrl` is the `http://127.0.0.1:<port>/` prefix.
- `TaskNode`, `ActionDefinition`, `GuardrailDefinition` are the plan model types the fixture needs.

So the tests **compile against today's code and fail against today's BEHAVIOUR** — the routes 404.
That is a stronger red than a stub tree: the failure is the real defect, not a thrown placeholder.
**Do not add a stub, do not add a `NotImplementedException`, and do not create a new production
member** — a new member would be an out-of-scope edit and would consume a retry.

### Copy the fixture shape that already works — do not invent one

`tests/Guardrails.Integration.Tests/LogServerTests.cs` already exercises this exact server end to
end. Read it first and mirror its helpers rather than re-deriving them:

- its private `TempPlan` class (a throwaway plan directory under the temp path, a fixed
  `RunId = "test-run"`, `WriteLog(taskId, attempt, fileName, content)`, `IDisposable` cleanup),
- its `Start(planDir, tasks)` helper that calls `LogServer.TryStart(..., port: 0, TextWriter.Null)`
  and asserts the result is non-null,
- its `TaskWithRealSources(temp, id, ...)` helper, which writes a REAL `action.ps1` and a REAL
  `guardrails/01-check.ps1` under `<temp>/tasks/<id>/` and returns a `TaskNode` whose
  `Action.Path` / `Guardrails[].Path` are the absolute paths of those files.

`ServeDiagramTests` is a **separate class in a separate file** — you may copy those helpers into it
(they are `private` to `LogServerTests` and not visible across classes), but do NOT edit
`LogServerTests.cs` to share them. Use a static `HttpClient` with a short timeout, `await using` the
server, and dispose the temp directory in a `finally` or `IDisposable`.

### What the diagram actually authors — the hrefs these tests must match

Measured, so you do not have to re-derive it:

- `MermaidRenderer.TaskFolderTargets(plan)` maps each task container to
  **`tasks/<task-folder>/`** — plan-folder-relative, forward slashes, trailing slash.
- `MermaidRenderer` emits one Mermaid `click <node> href "<path>"` directive per CHECK node, where
  `<path>` is the check script's plan-folder-relative path — i.e.
  **`tasks/<task-folder>/guardrails/<file>.ps1`** and, for a task preflight,
  **`tasks/<task-folder>/preflights/<file>.ps1`**.
- `OnTheFlyDiagramObserver` writes the live page to **`logs/<runId>/diagram.html`**.
- `LogServer.Handle` 404s any request whose FIRST path segment is not `tasks`, and 404s any
  `/tasks/{id}/...` whose THIRD segment is not one of `files` / `file` / `source` / `sourcefile` /
  `escalations` / `answer`.

So today: `GET /tasks/<id>/` → **200** (the task page), `GET /diagram.html` → **404**,
`GET /tasks/<id>/guardrails/01-check.ps1` → **404**.

### Group A — the behaviours that must be RED, each bound to a PINNED test method name

Author exactly these three methods, named verbatim — the red census greps for these names and
requires each one to be observed **Failed** against the current tree:

| Test method name | Behaviour |
|---|---|
| `Diagram_IsServedByTheLogSiteServer_NotA404` | Write a `logs/test-run/diagram.html` fixture whose content carries the real provenance first line (`<!-- guardrails:graph v1 source-sha256=abc123 -->`). Start the server. `GET {BaseUrl}diagram.html` returns **200** AND the body contains that provenance marker — so a 200 from an error page or an empty file cannot satisfy it. |
| `ServedDiagram_ResolvesAGuardrailScriptHref_ExactlyAsTheDiagramAuthorsIt` | Build a task with a REAL `guardrails/01-check.ps1` on disk holding known content. `GET {BaseUrl}tasks/<id>/guardrails/01-check.ps1` returns **200** and the body **equals that file's content**. This is the whole of #522: the href the diagram already authors must resolve, unchanged. |
| `ServedDiagram_ResolvesAPreflightScriptHref_ExactlyAsTheDiagramAuthorsIt` | The same assertion for a task **preflight** — a real `preflights/01-baseline.ps1` on disk, `GET {BaseUrl}tasks/<id>/preflights/01-baseline.ps1` → **200**, body equal. Pinned separately on purpose: a fix that hard-codes the literal segment `guardrails` passes the row above and fails this one. |

### Group B — pins that are ALREADY GREEN today, and are NOT in the census

These four must also be in the file. They pass against the current tree, so they are deliberately
**excluded from the red census** — they are regression and abuse pins, not evidence of the defect.
Say so in a comment above them so the next reader does not think the census forgot them.

| Test method name | What it pins |
|---|---|
| `TaskContainerHref_StillResolves_AfterTheDiagramRouteIsAdded` | `GET {BaseUrl}tasks/<id>/` still returns **200** and still renders the task page. The one route the diagram links that already works — a routing change must not cost it. |
| `UnknownTopLevelPath_IsStill404_SoTheDiagramRouteIsNotAWildcard` | `GET {BaseUrl}nope.html` returns **404**. |
| `LogsTreeFiles_OtherThanTheDiagram_AreNotServed` | Write `logs/test-run/secret.txt` alongside the diagram; `GET {BaseUrl}secret.txt` returns **404**. The cheapest wrong implementation of #522 is a blanket static file server over `logs/<runId>/`, which would leak every attempt log — including the ones the class doc warns may echo secrets — to anything that can reach the port. |
| `AGuardrailHrefNamingAFileTheTaskDoesNotDeclare_Is404` | For the same task, `GET {BaseUrl}tasks/<id>/guardrails/not-a-declared-check.ps1` returns **404**. The existing `sourcefile` route resolves a requested name ONLY through the precomputed known-source set, so an unknown or traversal name simply has no entry; the new route must keep that property rather than joining a request string onto a directory path. |

### The bar

- Every assertion must go through a REAL `LogServer` over a REAL temp plan folder. Do not assert on
  a string you built yourself, and do not construct the response you then check.
- Assert on `HttpResponseMessage.StatusCode` for the 404 cases — `GetStringAsync` throws on a 404
  and an exception-shaped assertion reads as a crash, not a verdict.
- Never write into the repository tree, and never point the server at a real plan folder.
- A test in Group A that PASSES today has not encoded the defect. If one of them is green when you
  run the suite, you have asserted something the current code already does — fix the test, do not
  weaken it.
