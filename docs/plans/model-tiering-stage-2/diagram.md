<!-- guardrails:graph v1 source-sha256=95af4caea48760bccd6ae7b1c480648aac18ac0e946cc805707014f234c54255 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1_preflights["Wave 1 Entry Gate"]
  end
  style wave_1_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1["Wave 1 — resolver-core"]
    subgraph task_wave_01_resolver_core_01_author_tests_candidate_selection["01-author-tests-candidate-selection"]
      task_wave_01_resolver_core_01_author_tests_candidate_selection_gr_0["01-build-passes"]:::guardrail
      task_wave_01_resolver_core_01_author_tests_candidate_selection_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_01_resolver_core_01_author_tests_candidate_selection_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_01_resolver_core_01_author_tests_candidate_selection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_resolver_core_02_implement_candidate_selection["02-implement-candidate-selection"]
      task_wave_01_resolver_core_02_implement_candidate_selection_gr_0["01-selection-tests-pass"]:::guardrail
    end
    style task_wave_01_resolver_core_02_implement_candidate_selection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_resolver_core_03_author_tests_resolution_precedence["03-author-tests-resolution-precedence"]
      task_wave_01_resolver_core_03_author_tests_resolution_precedence_gr_0["01-build-passes"]:::guardrail
      task_wave_01_resolver_core_03_author_tests_resolution_precedence_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_01_resolver_core_03_author_tests_resolution_precedence_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_01_resolver_core_03_author_tests_resolution_precedence fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_resolver_core_04_implement_resolution_precedence["04-implement-resolution-precedence"]
      task_wave_01_resolver_core_04_implement_resolution_precedence_gr_0["01-precedence-tests-pass"]:::guardrail
    end
    style task_wave_01_resolver_core_04_implement_resolution_precedence fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_1 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_1_guardrails["Wave 1 Exit Gate"]
    wave_1_guardrails_0["01-resolver-core-complete"]:::guardrail
  end
  style wave_1_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2_preflights["Wave 2 Entry Gate"]
  end
  style wave_2_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2["Wave 2 — attempt-launch-wiring"]
    wave_2_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_2_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_2 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_2_guardrails["Wave 2 Exit Gate"]
  end
  style wave_2_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-dor-section-6-contract-landed"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> wave_1_preflights
  wave_1_preflights --> task_wave_01_resolver_core_01_author_tests_candidate_selection
  task_wave_01_resolver_core_01_author_tests_candidate_selection --> task_wave_01_resolver_core_02_implement_candidate_selection
  task_wave_01_resolver_core_02_implement_candidate_selection --> task_wave_01_resolver_core_03_author_tests_resolution_precedence
  task_wave_01_resolver_core_03_author_tests_resolution_precedence --> task_wave_01_resolver_core_04_implement_resolution_precedence
  task_wave_01_resolver_core_04_implement_resolution_precedence --> wave_1_guardrails
  wave_2_preflights --> wave_2_stub
  wave_2_stub --> wave_2_guardrails
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
