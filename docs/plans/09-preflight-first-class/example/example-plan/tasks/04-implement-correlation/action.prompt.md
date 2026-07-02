# Action: implement RequestId threading in Acme.Payments.Core

<!-- Harness-contract header (see 03's note). SIMULATION: illustrative sample, not runnable. -->

Add a `RequestId` to `Acme.Payments.Core`'s charge pipeline so that:

- the new tests authored in `03-author-correlation-tests` go GREEN, and
- the pre-existing `Acme.Payments.Core.Tests` STAY green (the plan-level positive preflight,
  `<plan>/preflights/01-all-repo-tests-green.ps1`, established that they were green at the start).

## Constraints

- Edit only production code under `Acme.Payments.Core/`. Your `writeScope` is exactly that
  folder and EXCLUDES `Acme.Payments.Core.Tests/` — the harness deterministically enforces that
  you cannot edit the tests to make them pass. (This is the TDD test-protection replacement for
  the old captureHashes/restoreOnRetry triad — see the schemas reference §writeScope.)
- A charge with a supplied request id surfaces that id on `ChargeResult.RequestId`; a charge
  without one surfaces a generated, non-empty id.

## Why this task's soundness rests on the plan-level preflights

It `dependsOn` `03-author-correlation-tests`. Before EITHER task's first wave runs, the plan-level
`preflights/` folder already proved the starting point was sound (`01-all-repo-tests-green.ps1`)
and that the new field was provably absent (`02-correlation-id-absent.ps1`) — the attribution
chain the preflight doctrine is built to guarantee.
