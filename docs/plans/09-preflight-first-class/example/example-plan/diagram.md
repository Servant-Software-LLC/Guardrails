<!-- guardrails:graph v1 source-sha256=ee7b7ab5e2a0116f5335f5b2fd1b386429997b975f33e783c87e9286af4cf9bf -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["Plan-level Full Flight Check (POSITIVE baseline): the touched areas (Acme.Payments.Core.Tests, Acme.Payments.Api build) are already green before this plan's first task runs."]:::preflight
    plan_preflights_1["Plan-level Full Flight Check (NEGATIVE / assert-absent baseline): ChargeResult.RequestId does not exist yet, so a later 'present' gate is provably this plan's doing."]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_03_author_correlation_tests["03-author-correlation-tests"]
    task_03_author_correlation_tests_gr_0["01-new-tests-exist"]:::guardrail
    task_03_author_correlation_tests_gr_1["Anti-tautology: the new RequestId tests are RED against current code (the per-task polarity that the plan-level negative preflight generalizes). scope:local — never re-run at a union."]:::guardrail
  end
  style task_03_author_correlation_tests fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_correlation["04-implement-correlation"]
    task_04_implement_correlation_gr_0["The new RequestId tests now PASS and the pre-existing Core tests STILL pass (the green that the plan-level negative preflight makes attributable to this task). scope:local."]:::guardrail
  end
  style task_04_implement_correlation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_wire_api_correlation_middleware["05-wire-api-correlation-middleware"]
    task_05_wire_api_correlation_middleware_pf_0["Task-level JIT dependency-delivery…"]:::preflight
    task_05_wire_api_correlation_middleware_gr_0["01-health-still-200"]:::guardrail
    task_05_wire_api_correlation_middleware_gr_1["02-request-id-flows"]:::guardrail
  end
  style task_05_wire_api_correlation_middleware fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["Terminal Gate: the whole repo builds on the merged plan-branch HEAD (the re-homed GR2018 whole-repo re-run, form 1)."]:::guardrail
    plan_guardrails_1["Terminal Gate: the full touched-area test suite passes on the merged plan-branch HEAD (the re-homed GR2018 whole-repo re-run, form 1)."]:::guardrail
    plan_guardrails_2["Terminal Gate: a genuine union invariant — the merged plan-branch HEAD's out/ tree carries no unresolved git conflict markers (the re-homed GR2018 whole-repo re-run, form 2)."]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_03_author_correlation_tests
  task_03_author_correlation_tests --> task_04_implement_correlation
  task_04_implement_correlation --> task_05_wire_api_correlation_middleware
  task_05_wire_api_correlation_middleware --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
