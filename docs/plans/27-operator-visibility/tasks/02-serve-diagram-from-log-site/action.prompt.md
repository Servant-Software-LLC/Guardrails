## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "02-serve-diagram-from-log-site": { "someKey": "someValue" } }`. The harness
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

Make the log-site server serve the live diagram, and make the routes the diagram already links
resolve. Turn the tests task 01 authored green — **without changing them**.

**The tests are the specification.** Read
`tests/Guardrails.Integration.Tests/LogSite/ServeDiagramTests.cs` first; it is not in your write
scope and you may not edit it. Your guardrail runs the SAME filter that task 01's red census ran.

**Files you may write:**

1. `src/Guardrails.Cli/Ui/LogServer.cs` — the routing change (this is where the fix lives).
2. `src/Guardrails.Cli/Ui/LogSiteRenderer.cs` — only if the run-level log index should link the
   served diagram (see "Optional, and only if it is honest" below).
3. `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs` — only if the diagram's write location needs
   to change, which it very likely does not (see below).

**Scope boundary (harness-enforced):** Write only to those three paths. After this task completes,
the harness runs a `git diff` check and rejects any edit outside them — including
`ServeDiagramTests.cs`, `LogServerTests.cs`, `src/Guardrails.Core/Graph/*`, or any `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### The defect, measured

`GET /tasks/<id>/` → **200**. `GET /diagram.html` → **404**. So the server serves the route the
diagram's task-container overlay links, but not the diagram itself — which forces the operator onto
`file://`, where `tasks/<id>/guardrails/<file>.ps1` resolves against the flat, script-free
`logs/<runId>/` tree and every click 404s. A second link convention is what created this bug and is
**not** the fix: the hrefs the renderer already authors must resolve as authored.

Navigate by symbol name, not line number — grep for these:

- **`LogServer.Handle`** is the router. Today it 404s any request whose first path segment is not
  `tasks`, and 404s any `/tasks/{id}/…` whose third segment is not one of `files` / `file` /
  `source` / `sourcefile` / `escalations` / `answer`.
- **`LogServer._logsRoot`** is the `logs/<runId>/` tree the run writes into;
  `OnTheFlyDiagramObserver` writes `diagram.html` there already, so the file the new route must
  serve is **already on disk at the right path** — that is why `OnTheFlyDiagramObserver` probably
  needs no change at all. Verify before you touch it; the observer is also wanted by task 04's
  chain and a gratuitous edit here is a merge hazard for no gain.
- **`LogServer._sourcesByTask`** is the precomputed task-id → (filename → `SourceFile`) map built by
  `BuildSourceMap` from the plan's `TaskNode` definitions. The existing `sourcefile` route resolves
  a requested name **only through that map**, so an unknown or traversal name simply has no entry.
  That is the security property the class doc calls out, and it is the mechanism you should reuse.

### Half A — serve the diagram

`GET /diagram.html` must return **200** with the bytes of `<logsRoot>/diagram.html`, and a
content type a browser will render as HTML. When that file does not exist, **404** — not an empty
200, not a stub page.

### Half B — make the diagram's own hrefs resolve

`GET /tasks/<id>/guardrails/<file>` and `GET /tasks/<id>/preflights/<file>` must return the
requested script's content with **200**. Two requirements that are not negotiable, and one of them
is the reason this task exists rather than a one-line route:

- **Resolve through the known-source set, never by joining the request onto a directory path.** A
  name that is not one of that task's declared sources must 404. `_sourcesByTask` already holds
  exactly the set the diagram can link (the action file, every guardrail script, every preflight
  script, and their `.json` sidecars) because `MermaidRenderer` emits a `click … href` only for
  those. If the map's current keying (bare filename) cannot distinguish a `guardrails/01-x.ps1`
  from a `preflights/01-x.ps1` of the same name, fix the LOOKUP so the folder segment participates —
  do not fall back to path arithmetic on the request.
- **Do NOT serve the `logs/<runId>/` tree as static files.** The cheapest wrong implementation of
  this issue is a blanket file server rooted at `_logsRoot`; it would expose every attempt log — the
  ones the class doc warns may echo secrets — to anything that can reach the port, and
  `ServeDiagramTests` pins that it must not.

The existing `/tasks/{id}` (task page), `/tasks/{id}/files`, `/file`, `/source`, `/sourcefile`,
`/escalations` and `POST /answer` routes must keep behaving exactly as they do today. This adds
routes; it removes none.

### Optional, and only if it is honest

If the run-level static index (`LogSiteRenderer.WriteIndex` / `IndexHtml`) gains a link to the
served diagram, it must point at the **server** URL while the run is live — that is the only URL at
which the diagram's own hrefs work. A `file://`-relative link to `diagram.html` from a page the
operator opened over `http` would recreate the exact split this issue is about. If you cannot see
the live base URL from the renderer without changing a signature a caller outside your write scope
implements, **skip this half and say so in your summary** — the plan's Done-when is the server
serving the diagram, not a new link.

### The bar

- Do not edit the tests. If a test looks wrong, that is a `needsHuman` with the two quotes the
  harness contract asks for, not an edit.
- Keep `Handle`'s existing shape: it is a small router with an explicit segment switch and an
  explicit 404 default. Add cases; do not replace it with a catch-all.
- Every response path still goes through the existing `TrySetStatus` / `WriteHtml` / `WriteFile`
  helpers and their `finally { context.Response.Close(); }` discipline.
