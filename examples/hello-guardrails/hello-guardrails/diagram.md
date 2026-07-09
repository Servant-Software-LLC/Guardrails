<!-- guardrails:graph v1 source-sha256=fbe54bc3976bf97fdf904de27bf0090fab43aadaa9e66c88364977268a8c9a5c -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_write_greeting_script["01-write-greeting-script"]
    task_01_write_greeting_script_gr_0["01-script-exists"]:::guardrail
    task_01_write_greeting_script_gr_1["02-script-runs-clean"]:::guardrail
  end
  style task_01_write_greeting_script fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_generate_greeting["02-generate-greeting"]
    task_02_generate_greeting_gr_0["01-greeting-exists"]:::guardrail
    task_02_generate_greeting_gr_1["02-greeting-contains"]:::guardrail
  end
  style task_02_generate_greeting fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_quality_check["03-quality-check"]
    task_03_quality_check_gr_0["01-report-exists"]:::guardrail
    task_03_quality_check_gr_1["02-tone-is-friendly"]:::guardrail
  end
  style task_03_quality_check fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_write_greeting_script
  task_01_write_greeting_script --> task_02_generate_greeting
  task_02_generate_greeting --> task_03_quality_check
  task_03_quality_check --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
