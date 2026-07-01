# Action: author the request-id correlation tests (TDD red)

<!-- Harness-contract header (every prompt ACTION carries this so a human can run it outside
     the harness; the harness also injects the state + fragment + feedback contracts at runtime).
     SIMULATION: this is an illustrative sample task, not a runnable plan. -->

You are adding NEW unit tests to the existing `Acme.Payments.Core.Tests` project. Do NOT touch
production code in `Acme.Payments.Core` — only the test project.

## What to write

Add tests asserting that the charge pipeline threads a `RequestId`:

- A charge invoked with a request id surfaces that same id on the `ChargeResult`.
- A charge invoked without a request id surfaces a generated, non-empty `RequestId`.

## TDD-red requirement

These tests MUST FAIL against the current code, because `ChargeResult.RequestId` does not exist
yet. That red state is the point — it is what the `02-baseline-correlation-absent` Bucket-B
baseline already proved, and what this task's `tests-fail-on-current-code` guardrail re-asserts.
The later green (after `04-implement-correlation`) then PROVES the implementation did the work.

## Output

Write only test files under `Acme.Payments.Core.Tests/`. Your `writeScope` is exactly that
folder, and the implementation task (`04`) excludes it — so the harness deterministically
enforces that the implementation may not edit the tests.
