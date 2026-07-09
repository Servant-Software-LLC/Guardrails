<!-- guardrails:graph v1 source-sha256=4799a4c471356032d4fe860532e7ab55922cd51978ffeaed3e37ecc8611f8a36 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-all-repo-tests-green"]:::preflight
    plan_preflights_1["02-correlation-id-absent"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_03_author_correlation_tests["03-author-correlation-tests"]
    task_03_author_correlation_tests_gr_0["01-new-tests-exist"]:::guardrail
    task_03_author_correlation_tests_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_03_author_correlation_tests fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_correlation["04-implement-correlation"]
    task_04_implement_correlation_gr_0["01-correlation-tests-green"]:::guardrail
  end
  style task_04_implement_correlation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_wire_api_correlation_middleware["05-wire-api-correlation-middleware"]
    task_05_wire_api_correlation_middleware_pf_0["01-requestid-delivered-by-04"]:::preflight
    task_05_wire_api_correlation_middleware_gr_0["01-health-still-200"]:::guardrail
    task_05_wire_api_correlation_middleware_gr_1["02-request-id-flows"]:::guardrail
  end
  style task_05_wire_api_correlation_middleware fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-whole-repo-builds"]:::guardrail
    plan_guardrails_1["02-full-suite-passes"]:::guardrail
    plan_guardrails_2["03-union-invariant-no-conflict-markers"]:::guardrail
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
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
