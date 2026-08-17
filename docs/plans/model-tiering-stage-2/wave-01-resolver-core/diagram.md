<!-- guardrails:graph v1 source-sha256=0296951e03b800850ccc2d946a4d8cf07cec1f95e92b8e4e0160056f34f1da8d -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_01_resolver_core_01_author_tests_candidate_selection["wave-01-resolver-core/01-author-tests-candidate-selection"]
    task_wave_01_resolver_core_01_author_tests_candidate_selection_gr_0["01-build-passes"]:::guardrail
    task_wave_01_resolver_core_01_author_tests_candidate_selection_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_01_resolver_core_01_author_tests_candidate_selection_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_01_resolver_core_01_author_tests_candidate_selection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_resolver_core_02_implement_candidate_selection["wave-01-resolver-core/02-implement-candidate-selection"]
    task_wave_01_resolver_core_02_implement_candidate_selection_gr_0["01-selection-tests-pass"]:::guardrail
  end
  style task_wave_01_resolver_core_02_implement_candidate_selection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_resolver_core_03_author_tests_resolution_precedence["wave-01-resolver-core/03-author-tests-resolution-precedence"]
    task_wave_01_resolver_core_03_author_tests_resolution_precedence_gr_0["01-build-passes"]:::guardrail
    task_wave_01_resolver_core_03_author_tests_resolution_precedence_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_01_resolver_core_03_author_tests_resolution_precedence_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_01_resolver_core_03_author_tests_resolution_precedence fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_resolver_core_04_implement_resolution_precedence["wave-01-resolver-core/04-implement-resolution-precedence"]
    task_wave_01_resolver_core_04_implement_resolution_precedence_gr_0["01-precedence-tests-pass"]:::guardrail
  end
  style task_wave_01_resolver_core_04_implement_resolution_precedence fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-resolver-core-complete"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_01_resolver_core_01_author_tests_candidate_selection
  task_wave_01_resolver_core_01_author_tests_candidate_selection --> task_wave_01_resolver_core_02_implement_candidate_selection
  task_wave_01_resolver_core_02_implement_candidate_selection --> task_wave_01_resolver_core_03_author_tests_resolution_precedence
  task_wave_01_resolver_core_03_author_tests_resolution_precedence --> task_wave_01_resolver_core_04_implement_resolution_precedence
  task_wave_01_resolver_core_04_implement_resolution_precedence --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
