# Plan: correlate payments requests with a request-id (BROWNFIELD)

> **This is an ILLUSTRATIVE sample plan.** It shows what a breakdown looks like under the
> preflight **partition** (`../09-preflight-first-class.md`, #183): Buckets A + B ship as
> doctrine (and validate today), and a single Bucket-C slice is the deferred per-task JIT
> precondition. It is *not* wired to a real repo and is *not* an input to `/plan-breakdown`.
> See `./README.md` for what is real and what is simulated.

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

## Why this plan exercises the partition beyond unit tests

This is the case the design is *about* — it exercises all three buckets, and only one of them is
a unit-test baseline:

- **Bucket A — a positive unit-test baseline** on `Acme.Payments.Core` (task `00`): the touched
  library's existing tests are **already green**. If they are red *before* we start, the run
  halts with "your starting point is broken" rather than letting the implementation task burn its
  whole retry budget trying to make a gate green that was never green to begin with. This is
  real doctrine — a no-op-root task with a `scope:"local"` guardrail.
- **Bucket A — a non-test, positive "endpoint-up" baseline** on `Acme.Payments.Api` (task `01`):
  `GET /health` already returns `200`. This is the generalization the design formalizes — a
  baseline that is *not* a unit-test run (an endpoint already responding). In the sample folder
  it is mocked as a **deterministic byte-check** that the route is wired (the volume-control gate
  forbids a *real* live endpoint hit in a baseline — BLOCKER (e)).
- **Bucket B — a negative baseline** for attribution (task `02`): the new `RequestId` field is
  **absent** from the charge result today. Proving its absence *now* means a later "it's present"
  gate is provably *this plan's* doing, not pre-existing. It runs **one-shot at run start** and is
  `scope:"local"` so it is never re-run at a union/terminal gate (#165/#132 union-inversion). It
  cross-references the existing `tests-fail-on-current-code` anti-tautology archetype rather than
  forking it.
- **Bucket C — a per-task JIT dependency-delivery precondition** (carried by task `05`): the one
  genuinely first-class slice, **still DEFERRED/SIMULATED**. It verifies that the producer (`04`)
  actually delivered the `RequestId` threading `05` builds against, in `05`'s own worktree at
  `taskBase`, before `05`'s action. It lives in `05`'s `preflights/` folder, which the loader
  ignores — so it does not affect validation.

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
