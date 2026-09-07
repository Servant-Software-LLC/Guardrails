<!-- guardrails:graph v1 source-sha256=d2efa4be9ed7e8029dcf89b8d583d16303721e7d4e4043c02f79cbad374abd1d body-sha256=647ac4b71758c44342c9644299d8791ef38cdd32cf52cc06f236c624e45ce3a8 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-fresh-scaffold-start"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_01_scaffold_01_write_greet_script["wave-01-scaffold/01-write-greet-script"]
    task_wave_01_scaffold_01_write_greet_script_gr_0["01-greet-script-runs"]:::guardrail
  end
  style task_wave_01_scaffold_01_write_greet_script fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_scaffold_02_write_config["wave-01-scaffold/02-write-config"]
    task_wave_01_scaffold_02_write_config_gr_0["01-config-valid"]:::guardrail
  end
  style task_wave_01_scaffold_02_write_config fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-scaffold-union-clean"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_01_scaffold_01_write_greet_script
  plan_preflights --> task_wave_01_scaffold_02_write_config
  task_wave_01_scaffold_01_write_greet_script --> plan_guardrails
  task_wave_01_scaffold_02_write_config --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
