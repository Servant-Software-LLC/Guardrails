# Action: implement RequestId threading in Acme.Payments.Core

<!-- Harness-contract header (see 03's note). SIMULATION: illustrative sample, not runnable. -->

Add a `RequestId` to `Acme.Payments.Core`'s charge pipeline so that:

- the new tests authored in `03-author-correlation-tests` go GREEN, and
- the pre-existing `Acme.Payments.Core.Tests` STAY green (the `00` preflight baselined them).

## Constraints

- Edit only production code under `Acme.Payments.Core/`. Your `writeScope` is exactly that
  folder and EXCLUDES `Acme.Payments.Core.Tests/` — the harness deterministically enforces that
  you cannot edit the tests to make them pass. (This is the TDD test-protection replacement for
  the old captureHashes/restoreOnRetry triad — see the schemas reference §writeScope.)
- A charge with a supplied request id surfaces that id on `ChargeResult.RequestId`; a charge
  without one surfaces a generated, non-empty id.

## Why this task transitively depends on the preflights

It `dependsOn` `03-author-correlation-tests`, which in turn `dependsOn` both the positive core
baseline (`00`) and the negative absence baseline (`02`). So this implementation runs only after
the run proved the starting point was sound and the new field was provably absent — the
attribution chain the first-class preflight design is built to guarantee.
