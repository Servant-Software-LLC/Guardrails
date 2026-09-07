<!-- guardrails:graph v1 source-sha256=478997e14fde2b05d49d2c4c8961e348deadd94915578444eebad960397ce042 body-sha256=b37cee378fbd6ee559ffb8c7e5332280b680156910a47207977fd556ea39b91c -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_write_hello["01-write-hello"]
    task_01_write_hello_gr_0["01-hello-exists"]:::guardrail
  end
  style task_01_write_hello fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_write_world["02-write-world"]
    task_02_write_world_gr_0["01-world-exists"]:::guardrail
  end
  style task_02_write_world fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_combine_greeting["03-combine-greeting"]
    task_03_combine_greeting_gr_0["02-combined-greeting"]:::guardrail
  end
  style task_03_combine_greeting fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-whole-repo-greeting"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_write_hello
  plan_preflights --> task_02_write_world
  task_01_write_hello --> task_03_combine_greeting
  task_02_write_world --> task_03_combine_greeting
  task_03_combine_greeting --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
