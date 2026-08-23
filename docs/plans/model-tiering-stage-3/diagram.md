<!-- guardrails:graph v1 source-sha256=b0b129a6eca258d3d95ae082e3933f92894613562ef00bba9e028ea280308f43 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-toolchain-current"]:::preflight
    plan_preflights_1["02-baseline-core-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1_preflights["Wave 1 Entry Gate"]
  end
  style wave_1_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1["Wave 1 — config-net"]
    subgraph task_wave_01_config_net_01_allocate_diagnostic_codes["01-allocate-diagnostic-codes"]
      task_wave_01_config_net_01_allocate_diagnostic_codes_gr_0["01-build-passes"]:::guardrail
      task_wave_01_config_net_01_allocate_diagnostic_codes_gr_1["02-codes-allocated"]:::guardrail
    end
    style task_wave_01_config_net_01_allocate_diagnostic_codes fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_02_author_tests_registry_warnings["02-author-tests-registry-warnings"]
      task_wave_01_config_net_02_author_tests_registry_warnings_gr_0["01-build-passes"]:::guardrail
      task_wave_01_config_net_02_author_tests_registry_warnings_gr_1["02-tests-fail-on-current-code"]:::guardrail
    end
    style task_wave_01_config_net_02_author_tests_registry_warnings fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_03_implement_registry_warnings["03-implement-registry-warnings"]
      task_wave_01_config_net_03_implement_registry_warnings_gr_0["01-tests-pass"]:::guardrail
    end
    style task_wave_01_config_net_03_implement_registry_warnings fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_04_author_tests_pin_and_tier_coexist["04-author-tests-pin-and-tier-coexist"]
      task_wave_01_config_net_04_author_tests_pin_and_tier_coexist_gr_0["01-build-passes"]:::guardrail
      task_wave_01_config_net_04_author_tests_pin_and_tier_coexist_gr_1["02-tests-fail-on-current-code"]:::guardrail
    end
    style task_wave_01_config_net_04_author_tests_pin_and_tier_coexist fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_05_implement_pin_and_tier_coexist["05-implement-pin-and-tier-coexist"]
      task_wave_01_config_net_05_implement_pin_and_tier_coexist_gr_0["01-tests-pass"]:::guardrail
    end
    style task_wave_01_config_net_05_implement_pin_and_tier_coexist fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_06_update_ssot_and_domain_knowledge["06-update-ssot-and-domain-knowledge"]
      task_wave_01_config_net_06_update_ssot_and_domain_knowledge_gr_0["01-docs-record-the-codes"]:::guardrail
      task_wave_01_config_net_06_update_ssot_and_domain_knowledge_gr_1["02-schema-drift-tests-pass"]:::guardrail
    end
    style task_wave_01_config_net_06_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_1 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_1_guardrails["Wave 1 Exit Gate"]
    wave_1_guardrails_0["01-solution-builds"]:::guardrail
    wave_1_guardrails_1["02-core-tests-pass"]:::guardrail
    wave_1_guardrails_2["03-wave-deliverables-present"]:::guardrail
  end
  style wave_1_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2_preflights["Wave 2 Entry Gate"]
  end
  style wave_2_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2["Wave 2 — capture-and-persist"]
    wave_2_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_2_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_2 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_2_guardrails["Wave 2 Exit Gate"]
  end
  style wave_2_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3_preflights["Wave 3 Entry Gate"]
  end
  style wave_3_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3["Wave 3 — operator-surfaces"]
    wave_3_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_3_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_3 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_3_guardrails["Wave 3 Exit Gate"]
  end
  style wave_3_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4_preflights["Wave 4 Entry Gate"]
  end
  style wave_4_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4["Wave 4 — report-and-cleanup"]
    wave_4_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_4_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_4 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_4_guardrails["Wave 4 Exit Gate"]
  end
  style wave_4_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_5_preflights["Wave 5 Entry Gate"]
  end
  style wave_5_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_5["Wave 5 — review-net"]
    wave_5_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_5_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_5 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_5_guardrails["Wave 5 Exit Gate"]
  end
  style wave_5_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph plan_guardrails["Terminal Gate"]
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> wave_1_preflights
  wave_1_preflights --> task_wave_01_config_net_01_allocate_diagnostic_codes
  task_wave_01_config_net_01_allocate_diagnostic_codes --> task_wave_01_config_net_02_author_tests_registry_warnings
  task_wave_01_config_net_01_allocate_diagnostic_codes --> task_wave_01_config_net_04_author_tests_pin_and_tier_coexist
  task_wave_01_config_net_02_author_tests_registry_warnings --> task_wave_01_config_net_03_implement_registry_warnings
  task_wave_01_config_net_03_implement_registry_warnings --> task_wave_01_config_net_05_implement_pin_and_tier_coexist
  task_wave_01_config_net_04_author_tests_pin_and_tier_coexist --> task_wave_01_config_net_05_implement_pin_and_tier_coexist
  task_wave_01_config_net_05_implement_pin_and_tier_coexist --> task_wave_01_config_net_06_update_ssot_and_domain_knowledge
  task_wave_01_config_net_06_update_ssot_and_domain_knowledge --> wave_1_guardrails
  wave_2_preflights --> wave_2_stub
  wave_2_stub --> wave_2_guardrails
  wave_3_preflights --> wave_3_stub
  wave_3_stub --> wave_3_guardrails
  wave_4_preflights --> wave_4_stub
  wave_4_stub --> wave_4_guardrails
  wave_5_preflights --> wave_5_stub
  wave_5_stub --> wave_5_guardrails
  wave_1_guardrails -.->|"🔒 wave barrier"| wave_2_preflights
  wave_2_guardrails -.->|"🔒 wave barrier"| wave_3_preflights
  wave_3_guardrails -.->|"🔒 wave barrier"| wave_4_preflights
  wave_4_guardrails -.->|"🔒 wave barrier"| wave_5_preflights
  wave_5_guardrails --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
