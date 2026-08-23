<!-- guardrails:graph v1 source-sha256=300edc993202c5dc0f34155466ac40808f16bd694dde099e4f2d1acc0e278079 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave2-surfaces-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_03_operator_surfaces_01_author_tests_attempt_model_surfaces["wave-03-operator-surfaces/01-author-tests-attempt-model-surfaces"]
    task_wave_03_operator_surfaces_01_author_tests_attempt_model_surfaces_gr_0["01-tests-build"]:::guardrail
    task_wave_03_operator_surfaces_01_author_tests_attempt_model_surfaces_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_03_operator_surfaces_01_author_tests_attempt_model_surfaces fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_02_implement_route_log_and_observer_raise["wave-03-operator-surfaces/02-implement-route-log-and-observer-raise"]
    task_wave_03_operator_surfaces_02_implement_route_log_and_observer_raise_gr_0["01-disclosure-tests-pass"]:::guardrail
    task_wave_03_operator_surfaces_02_implement_route_log_and_observer_raise_gr_1["02-consumes-not-rederives"]:::guardrail
  end
  style task_wave_03_operator_surfaces_02_implement_route_log_and_observer_raise fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_03_render_attempt_model_in_live_and_console["wave-03-operator-surfaces/03-render-attempt-model-in-live-and-console"]
    task_wave_03_operator_surfaces_03_render_attempt_model_in_live_and_console_gr_0["01-rendering-tests-pass"]:::guardrail
    task_wave_03_operator_surfaces_03_render_attempt_model_in_live_and_console_gr_1["02-live-renders-through-the-shared-summary"]:::guardrail
  end
  style task_wave_03_operator_surfaces_03_render_attempt_model_in_live_and_console fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_04_forward_attempt_model_in_decorators["wave-03-operator-surfaces/04-forward-attempt-model-in-decorators"]
    task_wave_03_operator_surfaces_04_forward_attempt_model_in_decorators_gr_0["01-forwarding-tests-pass"]:::guardrail
  end
  style task_wave_03_operator_surfaces_04_forward_attempt_model_in_decorators fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_05_update_ssot_and_domain_knowledge["wave-03-operator-surfaces/05-update-ssot-and-domain-knowledge"]
    task_wave_03_operator_surfaces_05_update_ssot_and_domain_knowledge_gr_0["01-contract-delta-present"]:::guardrail
  end
  style task_wave_03_operator_surfaces_05_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-suites-pass"]:::guardrail
    plan_guardrails_2["03-wave-deliverables-present"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_03_operator_surfaces_01_author_tests_attempt_model_surfaces
  task_wave_03_operator_surfaces_01_author_tests_attempt_model_surfaces --> task_wave_03_operator_surfaces_02_implement_route_log_and_observer_raise
  task_wave_03_operator_surfaces_01_author_tests_attempt_model_surfaces --> task_wave_03_operator_surfaces_03_render_attempt_model_in_live_and_console
  task_wave_03_operator_surfaces_01_author_tests_attempt_model_surfaces --> task_wave_03_operator_surfaces_04_forward_attempt_model_in_decorators
  task_wave_03_operator_surfaces_02_implement_route_log_and_observer_raise --> task_wave_03_operator_surfaces_05_update_ssot_and_domain_knowledge
  task_wave_03_operator_surfaces_03_render_attempt_model_in_live_and_console --> task_wave_03_operator_surfaces_05_update_ssot_and_domain_knowledge
  task_wave_03_operator_surfaces_04_forward_attempt_model_in_decorators --> task_wave_03_operator_surfaces_05_update_ssot_and_domain_knowledge
  task_wave_03_operator_surfaces_05_update_ssot_and_domain_knowledge --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
