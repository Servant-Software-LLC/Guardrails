<!-- guardrails:graph v1 source-sha256=4d5dfe0c207166a6861e2ca3427796cc27e93d6fa990d72dbb4e09b7ed2dc35c -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_01_config_net_01_allocate_diagnostic_codes["wave-01-config-net/01-allocate-diagnostic-codes"]
    task_wave_01_config_net_01_allocate_diagnostic_codes_gr_0["01-build-passes"]:::guardrail
    task_wave_01_config_net_01_allocate_diagnostic_codes_gr_1["02-codes-allocated"]:::guardrail
  end
  style task_wave_01_config_net_01_allocate_diagnostic_codes fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_config_net_02_author_tests_registry_warnings["wave-01-config-net/02-author-tests-registry-warnings"]
    task_wave_01_config_net_02_author_tests_registry_warnings_gr_0["01-build-passes"]:::guardrail
    task_wave_01_config_net_02_author_tests_registry_warnings_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_wave_01_config_net_02_author_tests_registry_warnings fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_config_net_03_implement_registry_warnings["wave-01-config-net/03-implement-registry-warnings"]
    task_wave_01_config_net_03_implement_registry_warnings_gr_0["01-tests-pass"]:::guardrail
  end
  style task_wave_01_config_net_03_implement_registry_warnings fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_config_net_04_author_tests_pin_and_tier_coexist["wave-01-config-net/04-author-tests-pin-and-tier-coexist"]
    task_wave_01_config_net_04_author_tests_pin_and_tier_coexist_gr_0["01-build-passes"]:::guardrail
    task_wave_01_config_net_04_author_tests_pin_and_tier_coexist_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_wave_01_config_net_04_author_tests_pin_and_tier_coexist fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_config_net_05_implement_pin_and_tier_coexist["wave-01-config-net/05-implement-pin-and-tier-coexist"]
    task_wave_01_config_net_05_implement_pin_and_tier_coexist_gr_0["01-tests-pass"]:::guardrail
  end
  style task_wave_01_config_net_05_implement_pin_and_tier_coexist fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_config_net_06_update_ssot_and_domain_knowledge["wave-01-config-net/06-update-ssot-and-domain-knowledge"]
    task_wave_01_config_net_06_update_ssot_and_domain_knowledge_gr_0["01-docs-record-the-codes"]:::guardrail
    task_wave_01_config_net_06_update_ssot_and_domain_knowledge_gr_1["02-schema-drift-tests-pass"]:::guardrail
  end
  style task_wave_01_config_net_06_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-tests-pass"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_01_config_net_01_allocate_diagnostic_codes
  task_wave_01_config_net_01_allocate_diagnostic_codes --> task_wave_01_config_net_02_author_tests_registry_warnings
  task_wave_01_config_net_01_allocate_diagnostic_codes --> task_wave_01_config_net_04_author_tests_pin_and_tier_coexist
  task_wave_01_config_net_02_author_tests_registry_warnings --> task_wave_01_config_net_03_implement_registry_warnings
  task_wave_01_config_net_03_implement_registry_warnings --> task_wave_01_config_net_05_implement_pin_and_tier_coexist
  task_wave_01_config_net_03_implement_registry_warnings --> task_wave_01_config_net_06_update_ssot_and_domain_knowledge
  task_wave_01_config_net_04_author_tests_pin_and_tier_coexist --> task_wave_01_config_net_05_implement_pin_and_tier_coexist
  task_wave_01_config_net_05_implement_pin_and_tier_coexist --> task_wave_01_config_net_06_update_ssot_and_domain_knowledge
  task_wave_01_config_net_06_update_ssot_and_domain_knowledge --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
