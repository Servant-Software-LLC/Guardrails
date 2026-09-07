<!-- guardrails:graph v1 source-sha256=8025b207daaac9a514bb32e39f34dc9e77de0908ebb386d6eb82b3c796ee167a body-sha256=b6b19d0d9bc30ec924de889e099a4690a3e0192f20b878aba05b14767f9222f9 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1_preflights["Wave 1 Entry Gate"]
    wave_1_preflights_0["01-fresh-scaffold-start"]:::preflight
  end
  style wave_1_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1["Wave 1 — scaffold"]
    subgraph task_wave_01_scaffold_01_write_greet_script["01-write-greet-script"]
      task_wave_01_scaffold_01_write_greet_script_gr_0["01-greet-script-runs"]:::guardrail
    end
    style task_wave_01_scaffold_01_write_greet_script fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_scaffold_02_write_config["02-write-config"]
      task_wave_01_scaffold_02_write_config_gr_0["01-config-valid"]:::guardrail
    end
    style task_wave_01_scaffold_02_write_config fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_1 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_1_guardrails["Wave 1 Exit Gate"]
    wave_1_guardrails_0["01-scaffold-union-clean"]:::guardrail
  end
  style wave_1_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2_preflights["Wave 2 Entry Gate"]
    wave_2_preflights_0["01-scaffold-materialized"]:::preflight
  end
  style wave_2_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2["Wave 2 — greet"]
    subgraph task_wave_02_greet_01_generate_greeting["01-generate-greeting"]
      task_wave_02_greet_01_generate_greeting_gr_0["01-greeting-exists"]:::guardrail
    end
    style task_wave_02_greet_01_generate_greeting fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_greet_02_write_report["02-write-report"]
      task_wave_02_greet_02_write_report_gr_0["01-report-quotes-greeting"]:::guardrail
    end
    style task_wave_02_greet_02_write_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_2 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_2_guardrails["Wave 2 Exit Gate"]
    wave_2_guardrails_0["01-greeting-complete"]:::guardrail
  end
  style wave_2_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph plan_guardrails["Terminal Gate"]
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> wave_1_preflights
  wave_1_preflights --> task_wave_01_scaffold_01_write_greet_script
  wave_1_preflights --> task_wave_01_scaffold_02_write_config
  task_wave_01_scaffold_01_write_greet_script --> wave_1_guardrails
  task_wave_01_scaffold_02_write_config --> wave_1_guardrails
  wave_2_preflights --> task_wave_02_greet_01_generate_greeting
  task_wave_02_greet_01_generate_greeting --> task_wave_02_greet_02_write_report
  task_wave_02_greet_02_write_report --> wave_2_guardrails
  wave_1_guardrails -.->|"🔒 wave barrier"| wave_2_preflights
  wave_2_guardrails --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
