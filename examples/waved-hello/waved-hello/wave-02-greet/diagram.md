<!-- guardrails:graph v1 source-sha256=80b235d217bd9810dc49fea71cc175e8d72ffdb75fd84673bbd1488d879dab73 body-sha256=9b173df8ef565ad6c92df995b5ab3c4e17e237af0b66d987f970ee20ab1bd3de -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-scaffold-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_02_greet_01_generate_greeting["wave-02-greet/01-generate-greeting"]
    task_wave_02_greet_01_generate_greeting_gr_0["01-greeting-exists"]:::guardrail
  end
  style task_wave_02_greet_01_generate_greeting fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_greet_02_write_report["wave-02-greet/02-write-report"]
    task_wave_02_greet_02_write_report_gr_0["01-report-quotes-greeting"]:::guardrail
  end
  style task_wave_02_greet_02_write_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-greeting-complete"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_02_greet_01_generate_greeting
  task_wave_02_greet_01_generate_greeting --> task_wave_02_greet_02_write_report
  task_wave_02_greet_02_write_report --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
