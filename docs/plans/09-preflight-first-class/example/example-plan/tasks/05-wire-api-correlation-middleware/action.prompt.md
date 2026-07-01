# Action: wire correlation middleware in Acme.Payments.Api

<!-- Harness-contract header (see 03's note). SIMULATION: illustrative sample, not runnable. -->

Add middleware to `Acme.Payments.Api` that:

- reads an inbound `X-Request-Id` header (or generates one when absent),
- passes that id into the `Acme.Payments.Core` charge call (the threading added in `04`), and
- leaves `GET /health` still returning `200`.

## Constraints

- Edit only `Acme.Payments.Api/`. Your `writeScope` is exactly that folder.
- This task `dependsOn` `01-baseline-api-endpoint-up`: the run already proved `/health` was up
  BEFORE the change, so this task's "/health still 200" guardrail is provably testing the effect
  of YOUR middleware, not a pre-existing break. That is the attribution the Bucket-A endpoint
  baseline buys.
