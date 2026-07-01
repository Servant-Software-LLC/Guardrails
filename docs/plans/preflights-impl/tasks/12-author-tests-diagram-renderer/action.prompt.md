## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `12-author-tests-diagram-renderer`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "12-author-tests-diagram-renderer": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Author failing goldens for the container-model diagram renderer (Deliverable 7, test-group #10). Drive the
real `MermaidRenderer` / `GraphSourceHash` / the `graph --check` path over a committed fixture plan (one that
has plan-level `<plan>/preflights/` + `<plan>/guardrails/` and a task-level `tasks/<id>/preflights/`, which the
loader now understands). RED against the current renderer (which emits `done_<id>` nodes and `task-->guardrail`
fan-out edges and whose `source-sha256` does not fold plan-level checks).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ContainerDiagramTests.cs`. After this task the harness runs a `git diff` check
and rejects any edit outside that path — the renderer source, the CLI, other tests, existing golden
`diagram.*` fixtures. An out-of-scope edit fails the task and consumes a retry. If a compile error comes from
a missing symbol elsewhere, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` and stop.

Assertions to author (tag the class `[Trait("Category","Preflights")]`):
- The emitted Mermaid contains task-container subgraphs (`subgraph task_<id>`) each holding nested
  `Preflights` and `Guardrails` subgraphs with the individual check nodes INSIDE the container.
- Two plan-level subgraphs: `plan_preflights` ("Full Flight Checks") at the TOP and `plan_guardrails`
  ("Terminal Gate") at the BOTTOM.
- Invisible ANCHOR nodes (`<id>_anchor[" "]:::invisible`, a `classDef invisible`) and DAG edges drawn
  **anchor→anchor** (`task_A_anchor --> task_B_anchor` per `dependsOn`; plan-preflights anchor → root-task
  anchors; leaf-task anchors → plan-guardrails anchor).
- A task-level preflight STILL renders its gated `dependsOn` container→container edge AND renders the
  preflight as a check node inside the consumer's Preflights subgraph.
- **ABSENCE assertions:** NO `done_` node anywhere; NO `task-->guardrail` fan-out edge.
- **Determinism:** a byte-identical re-render on unchanged input (stable ordinal ordering).
- **Staleness (the load-bearing one):** editing a `<plan>/guardrails/` check → `graph --check` reports
  **stale (exit 2)** — proving `source-sha256` folds the PLAN-LEVEL folder checks, not just `tasks{}`.

REUSE the existing renderer/graph test conventions (the current `MermaidRenderer` tests, `GraphSourceHash`
tests, `TestPaths`, the fixture-plan builders). The tests MUST **compile** and **fail** (the current renderer
produces the old model). Failing is intentional, NOT compiling is a mistake. Do NOT rewrite the renderer and
do NOT touch existing golden `diagram.*` fixtures. Keep warning-clean (`TreatWarningsAsErrors=true`). Publish
nothing to state.
