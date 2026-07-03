# Plan: correlate payments requests with a request-id (BROWNFIELD)

> **This is an ILLUSTRATIVE sample plan.** It shows what a breakdown looks like under the
> **four-folder model** (`../../09-preflight-first-class.md`): a plan-level `preflights/`
> (Full Flight Checks) and `guardrails/` (Terminal Gate), plus a task-level `preflights/` (JIT
> dependency-delivery precondition) alongside the ordinary task-level `guardrails/`. It is *not*
> wired to a real repo and is *not* an input to `/plan-breakdown`. See `./README.md` for what is
> real and what is simulated.

## Context

`Acme.Payments` is an existing service that is already deployed and already verified. Two
parts of it are touched by this change — and **both already exist and already pass their own
checks today**, which is precisely what makes them *baselineable*:

- **`Acme.Payments.Core`** — an existing class library with an existing, green unit-test
  project (`Acme.Payments.Core.Tests`). This plan **modifies** it (adds a `RequestId` to the
  charge pipeline); it does not create it.
- **`Acme.Payments.Api`** — an existing ASP.NET HTTP service that already exposes a
  `GET /health` endpoint returning `200 OK`. This plan **modifies** it (adds request-id
  correlation middleware); it does not create it.

## Goal

Every charge processed by `Acme.Payments` carries a `RequestId` that is:

1. generated (or read from the inbound `X-Request-Id` header) by API middleware, and
2. threaded through `Acme.Payments.Core`'s charge pipeline so it appears in the charge result.

## Why this plan exercises all four folder kinds

This is the case the design is *about* — both baseline polarities, at both scopes:

- **A positive Full Flight Check** on the touched areas (`preflights/01-all-repo-tests-green.ps1`):
  `Acme.Payments.Core.Tests` and the `Acme.Payments.Api` build are **already green**. If they were
  red *before* we start, the run halts with "your starting point is broken" rather than letting
  the implementation task burn its whole retry budget trying to make a gate green that was never
  green to begin with. A deterministic build/test re-run — never a live network probe.
- **A negative Full Flight Check** for attribution (`preflights/02-correlation-id-absent.ps1`):
  the new `RequestId` field is **absent** from the charge result today. Proving its absence *now*
  means a later "it's present" gate is provably *this plan's* doing, not pre-existing. It runs
  **one-shot before the first task's wave** and is never re-run at a union/terminal gate. It
  cross-references the existing `tests-fail-on-current-code` anti-tautology archetype on task `03`
  rather than forking it.
- **A task-level JIT dependency-delivery precondition** (carried by task `05`'s
  `preflights/` folder): verifies that the producer (`04`) actually delivered the `RequestId`
  threading `05` builds against, in `05`'s own worktree at `taskBase`, before `05`'s action —
  keyed to the `04 -> 05` `dependsOn` edge.
- **The Terminal Gate** (`guardrails/`): a real whole-repo build + full-suite re-run plus a
  genuine union-invariant check, re-run on the final merged HEAD.

## Work (the real tasks)

1. **Author the correlation unit tests (TDD-red).** Add `RequestId`-threading tests to
   `Acme.Payments.Core.Tests`. They must be **red against the current code** (the field does
   not exist yet).
2. **Implement request-id threading in `Acme.Payments.Core`.** Add `RequestId` to the charge
   pipeline so the new tests go green and the *existing* tests stay green.
3. **Wire the API correlation middleware in `Acme.Payments.Api`.** Read/generate
   `X-Request-Id`, attach it to the charge call, and keep `GET /health` returning `200`.

## Acceptance

- The whole repo builds.
- All `Acme.Payments.Core.Tests` pass (new + pre-existing).
- `GET /health` still returns `200` after the middleware change.
- A charge result exposes the threaded `RequestId`.
