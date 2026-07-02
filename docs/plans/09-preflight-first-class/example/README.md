# Worked example: the four-folder model

> **This folder is MOSTLY REAL.** It illustrates the **four-folder model** from
> [`../../09-preflight-first-class.md`](../../09-preflight-first-class.md): preflights and guardrails
> are first-class at TWO scopes — plan-level and task-level — mirrored by four folder kinds on
> disk. `guardrails validate` on this folder **PASSES** under the four-folder loader. There is
> no no-op ROOT/END task and no `integrationGate: true` task anywhere — the terminal whole-repo
> checks live in the plan-level `guardrails/` folder. The guardrail `scope` field still
> recognises exactly two values, `local` and `integration`; no third value exists.

## What's in here

```
example/
├── README.md            ← you are here
├── example-plan.md      ← the BROWNFIELD plan that motivates the breakdown
└── example-plan/        ← the breakdown task folder (all four folder kinds present)
    ├── guardrails.json  (header notes what is real vs simulated)
    ├── state/seed.json
    ├── diagram.html     ← the REAL container-model DAG render (open in a browser)
    ├── diagram.md        ← the same DAG, rendered on GitHub
    ├── preflights/        ← PLAN-LEVEL Full Flight Checks (positive + negative baseline)
    ├── guardrails/         ← PLAN-LEVEL Terminal Gate (a real integration-set re-run)
    └── tasks/<NN-…>/       task.json + an action + guardrails/*.{ps1,prompt.md,json}
                          (05 also has a tasks/<id>/preflights/ — the task-level JIT
                          dependency-delivery precondition)
```

The plan (`example-plan.md`) is a deliberately **brownfield** change — "add request-id
correlation to the Payments service" — that touches **two already-verified things**:

1. **`Acme.Payments.Core`**, an existing library with an existing **green unit-test project**.
2. **`Acme.Payments.Api`**, an existing HTTP service with an existing **`GET /health`**
   endpoint that already returns `200`.

Because both pre-exist and are *modified* (not created), both are *baselineable* — and that
baseline now lives as a first-class folder, not a no-op task.

## The four folder kinds (how each maps onto this example)

| Folder | Where | What it shows |
|---|---|---|
| **`preflights/`** | plan root (`example-plan/preflights/`) | The **Full Flight Checks**: `01-all-repo-tests-green.ps1` (POSITIVE — the touched areas already build/test green) and `02-correlation-id-absent.ps1` (NEGATIVE / assert-absent — the new `RequestId` field is provably absent before this plan starts). Each runs ONCE, plan-wide, before any task's first wave. |
| **`guardrails/`** | plan root (`example-plan/guardrails/`) | The **Terminal Gate**: `01-whole-repo-builds.ps1` + `02-full-suite-passes.ps1` (a real whole-repo build/suite re-run) and `03-union-invariant-no-conflict-markers.ps1` (a genuinely executable union-invariant check, mirroring `examples/parallel-hello`). Carries the re-homed GR2018 obligation — at least one REAL integration-set re-run, not a tautological `exit 0`. |
| **`tasks/<id>/preflights/`** | task-level (`tasks/05-wire-api-correlation-middleware/preflights/`) | The **JIT dependency-delivery precondition**: verifies the producer (`04`) actually delivered the `RequestId` threading `05` builds against, in `05`'s own worktree at `taskBase`, before `05`'s action — the state no pre-DAG phase could see. Keyed to the `04 -> 05` `dependsOn` edge. |
| **`tasks/<id>/guardrails/`** | task-level (every task) | Ordinary postconditions — unchanged from before the four-folder model. |

The three work tasks (`03`–`05`) form a linear chain (`03 -> 04 -> 05`); `03` has no `dependsOn`
edges of its own because the plan-wide preflights now cover what the old no-op-root baseline
tasks used to gate.

## What is SIMULATED (and only this)

This is an illustrative sample plan, not wired to a real repo. **All guardrail and preflight
bodies are stubbed** to a fixed `Write-Output …; exit 0` (or, for the negative preflight, the
polarity that would fail if the field were already present), each carrying a comment showing the
*real* command it stands in for (e.g. `dotnet test …`, `Select-String … 'RequestId'`). The one
exception is `guardrails/03-union-invariant-no-conflict-markers.ps1`, which is genuinely
executable (it scans an `out/` directory that this plan never produces, so it always finds
nothing to flag and exits 0 — the same shape `examples/parallel-hello` uses for real).

Everything else is real shape:

- **`guardrails validate` PASSES** on this folder under the four-folder loader.
- All four folder kinds are present, structurally validated, and every guardrail-shaped file
  opens with a `catches:` declaration (SSOT §4).
- The diagram (`diagram.html` / `diagram.md`) is the byte-for-byte output of
  `guardrails graph` on this folder — no hand edits.

## How to read the diagram

Open **`example-plan/diagram.html`** in a browser (or view **`diagram.md`** on GitHub). It uses
the container model: each task is a `subgraph` container with its own `Guardrails` (and, for
`05`, `Preflights`) sub-container; the plan-level `preflights/` and `guardrails/` folders are
their own top-level containers. Invisible anchor nodes carry the actual dependency edges between
containers, so the arrows show task-to-task and folder-to-task flow without exploding into a
guardrail-level fan-out.

## Provenance

- Design of record: [`../../09-preflight-first-class.md`](../../09-preflight-first-class.md).
