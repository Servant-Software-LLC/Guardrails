# Worked example: the preflight PARTITION — #183

> **This folder is MOSTLY REAL.** It illustrates the preflight **partition** from
> [`../09-preflight-first-class.md`](../09-preflight-first-class.md) (#183). The partition splits
> the preflight space into three buckets, and **two of them ship as doctrine and validate today**:
> Buckets **A** (shared positive baseline) and **B** (negative / assert-absent) are ordinary
> no-op-root tasks with normal `scope:"local"` guardrails — `guardrails validate` on this folder
> **PASSES**. Only **Bucket C** (a per-task JIT dependency-delivery precondition) is still
> **DEFERRED/SIMULATED**, and it is carried by a single `preflights/` folder on task `05` that the
> loader ignores. There is **no** `scope:"precondition"` value, **no** pre-DAG phase, and **no**
> validating twin — those were all WITHDRAWN when the partition was adopted.

## What's in here

```
example/
├── README.md            ← you are here
├── example-plan.md      ← the BROWNFIELD plan that motivates the breakdown
└── example-plan/        ← the breakdown task folder (A/B real, C simulated)
    ├── guardrails.json  (header notes what is real vs simulated)
    ├── state/seed.json
    ├── diagram.html     ← the REAL DAG render, +1 hand-added Bucket-C node (open in a browser)
    ├── diagram.md       ← the same DAG, rendered on GitHub
    └── tasks/<NN-…>/     task.json + an action + guardrails/*.{ps1,prompt.md,json}
                          (05 also has a SIMULATED preflights/ folder — the Bucket-C slice)
```

The plan (`example-plan.md`) is a deliberately **brownfield** change — "add request-id
correlation to the Payments service" — that touches **two already-verified things**:

1. **`Acme.Payments.Core`**, an existing library with an existing **green unit-test project**.
2. **`Acme.Payments.Api`**, an existing HTTP service with an existing **`GET /health`**
   endpoint that already returns `200`.

Because both pre-exist and are *modified* (not created), both are *baselineable* — and they
warrant **different baseline shapes**, which is the whole point of this example: it exercises the
partition **beyond unit tests**, across all three buckets.

## The three buckets (how each maps onto a task)

| Bucket | Task | Polarity | What it shows | Status |
|---|---|---|---|---|
| **A — shared positive baseline** | `00-baseline-core-tests-green` | positive | The canonical #181 instance: the touched library's existing tests are **already green**. Red-before-start ⇒ fail fast on a broken start. A no-op-root task with a `scope:"local"` guardrail. | **REAL doctrine — validates today** |
| **A — shared positive baseline (non-test)** | `01-baseline-api-endpoint-up` | positive | The **generalization** the design is about — a baseline that is *not* a unit-test run. Modeled as a cheap **deterministic byte check** (the `/health` route is wired), **not** a live probe — see BLOCKER (e) below. | **REAL doctrine — validates today** |
| **B — negative / assert-absent baseline** | `02-baseline-correlation-absent` | negative | The new `RequestId` field is **absent** now, so a later "present" gate is provably this plan's doing. **One-shot at run start**; `scope:"local"` so it is **never** re-run at a union/terminal gate. A cross-reference to, not a fork of, `tests-fail-on-current-code`. | **REAL doctrine — validates today** |
| **C — per-task JIT dependency-delivery precondition** | `05-wire-api-correlation-middleware`'s `preflights/` folder | positive | The one genuinely first-class slice: verifies the producer (`04`) actually delivered the `RequestId` threading `05` builds against, **in 05's own worktree at `taskBase`, before 05's action**. The state no pre-DAG phase could see. | **DEFERRED / SIMULATED (#183)** |

Buckets A and B are each a **no-op `exit 0` action** task — the guardrail *is* the work. The work
tasks (`03`–`05`) transitively `dependsOn` the relevant baseline(s); `06-integration-gate` is the
terminal `integrationGate` sink.

## What exactly is MOCKED (and only this)

The partition makes the example **more** real, not less. The **only** simulated thing is:

- **Task `05`'s `preflights/` folder** (`preflights/01-requestid-delivered-by-04.ps1` + its `.json`
  sidecar) — the deferred **Bucket-C** per-task JIT dependency-delivery precondition. The harness's
  loader only enumerates the `guardrails/` directory, so a sibling `preflights/` folder is **ignored
  entirely** — it does not load, does not validate, and does not run. It exists purely to **show the
  Bucket-C shape on disk** (Option 1 of the open folder-vs-flag decision). Its body is stubbed
  `Write-Output …; exit 0`; the file's header carries the real deterministic byte-check it stands in
  for (`Select-String 'RequestId' Acme.Payments.Core/ChargeResult.cs`) and the full Bucket-C
  semantics (`precondition-failed` → `needs-human` → exit 2, **no retry burned**).

Everything else is real shape:

- **`guardrails validate` PASSES** on this folder (Buckets A + B are ordinary `scope:"local"`
  guardrails). The simulated Bucket-C `preflights/` folder is the only non-functional piece, and it
  is a folder the loader skips — so validation is clean.
- **The other guardrail bodies** (in every `guardrails/` folder) are stubbed to
  `Write-Output …; exit 0`, each carrying a comment showing the *real* check it stands in for (e.g.
  `dotnet test … --filter`, `Select-String … 'MapGet("/health")'`). Nothing here runs against a real
  repo — it is an illustrative sample plan, not wired to a real codebase.

There is **no** `scope:"precondition"` value, **no** pre-DAG preflight phase, and **no** validating
twin anywhere in this folder.

## How to read the diagram

Open **`example-plan/diagram.html`** in a browser (or view **`diagram.md`** on GitHub).

- **Nodes `00`/`01`/`02`** are the **Bucket-A/B baseline** tasks. They are **ordinary first-wave
  nodes** — *not* a separate phase or lane. They carry a subtle dashed outline (`:::baseline`) so a
  reviewer can spot them, but structurally they are just tasks that the work tasks `dependsOn`.
- **Blue** nodes are ordinary work tasks, **amber** are their guardrails, **green** are the
  per-task "Finished" nodes — the standard `guardrails graph` palette.
- **Exactly one dashed-violet node** (`:::precond`) represents task `05`'s **Bucket-C** `preflights/`
  check. It is attached so it visually **gates only `05`** (and thus only 05's transitive cone) —
  not the whole plan. It is labeled "Bucket C (SIMULATED #183) — dependency-delivery precondition at
  taskBase".

**How the diagram was produced (honestly).** It **is** a real `guardrails graph` render — there is
no validating twin. The DAG shape and node labels (everything the `source-sha256` staleness hash
covers) are the byte-for-byte output of `guardrails graph` on this partition-validating folder.
Then **two minimal, honest, cosmetic edits** were made by hand: (1) the three baseline task nodes
were re-tagged `:::baseline` (a subtle outline — they remain ordinary nodes); and (2) **a single
node** for 05's Bucket-C `preflights/` check was hand-added, gating only `05`. That one node is the
**only** hand-added DAG element — it is not part of the real render and is **not** covered by the
`source-sha256` (so `graph --check` stays green). The file's top comment records all of this.

## Doctrine (A + B, ships) vs Bucket C (deferred, folder-or-flag open)

The principle — "a brownfield task that *modifies* a verified thing can have its gate evaluated
*before* the task, as a baseline" — **ships now as DOCTRINE** for Buckets A and B (Phase 0 of the
design), with **no schema or harness change**. A doctrine baseline is expressed entirely with
**existing primitives**. Bucket C is the only slice that would need a first-class contract, and it
is deferred behind a real captured instance and an unresolved realization decision:

| | Doctrine — Buckets A + B (ships now, #181) | Bucket C (DEFERRED, #183) |
|---|---|---|
| What it *is* | a **no-op-action ROOT task** carrying a normal `scope:"local"` guardrail, with a `dependsOn` edge from every modifier | a per-task JIT **precondition** that runs in the consumer's own worktree at `taskBase`, before its action |
| Claim | "this AREA starts from green" (A) / "the thing I add is absent now" (B) | "did my producer actually **deliver** the symbol I build against?" |
| Schema change | **none** — `task.json` + guardrail + `dependsOn` already express it | **open** — either a `<task>/preflights/` folder (+ `precondition-failed` + GR2027) **or** a one-line `task.json` no-burn flag |
| Where it runs | as an ordinary first-wave task, once, like any task | inside the consumer's segment worktree at `taskBase`, gating loop entry |
| On failure | a task starts, then halts (the fast no-op short-circuit, #182) | `precondition-failed` → `needs-human` → exit 2, **without burning a retry attempt** |
| `guardrails validate` | **PASSES** today | n/a (the simulated `preflights/` folder is ignored by the loader) |

The **folder-vs-flag** decision (Option 1 = a `preflights/` folder, as shown here; Option 2 = a
`task.json` no-burn flag) is **not** resolved by the design — it is decided at trigger time, informed
by a real captured instance. See `../09-preflight-first-class.md` §"The open decision".

## The honest tension worth seeing (BLOCKER (e))

The most intuitive preflight — "is the endpoint already **up**?" (the design doc's "plane on the
runway") — is *exactly* the flaky single-point-of-failure the design's **volume-control gate
forbids** as a baseline: a live network probe would fail intermittently. So:

- `01-baseline-api-endpoint-up` (Bucket A) is modeled as a **cheap deterministic byte check** (the
  route is wired in the source), **not** a live HTTP call.
- The same **live-probe ban** is lifted verbatim into **Bucket C**: 05's `preflights/` check is a
  byte-check on the inherited source (`Select-String 'RequestId' …`), never a live probe.

A genuinely live probe belongs in a *task's own* guardrail, where a flake costs only that task's
retry budget — and indeed `05`'s `01-health-still-200` guardrail is exactly where the live check
would live. This tension is part of why **Bucket C is deferred behind a trigger**, not shipped.

## Provenance

- Design of record: [`../09-preflight-first-class.md`](../09-preflight-first-class.md) (#183).
- Doctrine that actually ships (the no-op-root baseline, Buckets A + B): #181. Serial-mode
  fast-halt: #182.
