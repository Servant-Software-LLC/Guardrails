<!-- guardrails:graph v1 source-sha256=342e33804ff8d7ec60e94dfecdaf8a440d7acc3e444060312574b283ad77973a -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave2-surfaces-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_03_operator_surfaces_01_stub_the_observer_seam["wave-03-operator-surfaces/01-stub-the-observer-seam"]
    task_wave_03_operator_surfaces_01_stub_the_observer_seam_gr_0["01-stubs-declared-and-inert"]:::guardrail
  end
  style task_wave_03_operator_surfaces_01_stub_the_observer_seam fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_02_author_tests_disclosure["wave-03-operator-surfaces/02-author-tests-disclosure"]
    task_wave_03_operator_surfaces_02_author_tests_disclosure_gr_0["01-tests-build"]:::guardrail
    task_wave_03_operator_surfaces_02_author_tests_disclosure_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_03_operator_surfaces_02_author_tests_disclosure fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_03_author_tests_rendering["wave-03-operator-surfaces/03-author-tests-rendering"]
    task_wave_03_operator_surfaces_03_author_tests_rendering_gr_0["01-tests-build"]:::guardrail
    task_wave_03_operator_surfaces_03_author_tests_rendering_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_03_operator_surfaces_03_author_tests_rendering fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_04_author_tests_forwarding["wave-03-operator-surfaces/04-author-tests-forwarding"]
    task_wave_03_operator_surfaces_04_author_tests_forwarding_gr_0["01-tests-build"]:::guardrail
    task_wave_03_operator_surfaces_04_author_tests_forwarding_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_03_operator_surfaces_04_author_tests_forwarding fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise["wave-03-operator-surfaces/05-implement-route-log-and-observer-raise"]
    task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise_gr_0["01-disclosure-tests-pass"]:::guardrail
    task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise_gr_1["02-consumes-not-rederives"]:::guardrail
  end
  style task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console["wave-03-operator-surfaces/06-render-attempt-model-in-live-and-console"]
    task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console_gr_0["01-rendering-tests-pass"]:::guardrail
    task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console_gr_1["02-live-renders-through-the-shared-summary"]:::guardrail
  end
  style task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators["wave-03-operator-surfaces/07-forward-attempt-model-in-decorators"]
    task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators_gr_0["01-forwarding-tests-pass"]:::guardrail
  end
  style task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge["wave-03-operator-surfaces/08-update-ssot-and-domain-knowledge"]
    task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge_gr_0["01-contract-delta-present"]:::guardrail
  end
  style task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-suites-pass"]:::guardrail
    plan_guardrails_2["03-wave-deliverables-present"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_03_operator_surfaces_01_stub_the_observer_seam
  task_wave_03_operator_surfaces_01_stub_the_observer_seam --> task_wave_03_operator_surfaces_02_author_tests_disclosure
  task_wave_03_operator_surfaces_01_stub_the_observer_seam --> task_wave_03_operator_surfaces_03_author_tests_rendering
  task_wave_03_operator_surfaces_01_stub_the_observer_seam --> task_wave_03_operator_surfaces_04_author_tests_forwarding
  task_wave_03_operator_surfaces_02_author_tests_disclosure --> task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise
  task_wave_03_operator_surfaces_03_author_tests_rendering --> task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console
  task_wave_03_operator_surfaces_04_author_tests_forwarding --> task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators
  task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise --> task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge
  task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console --> task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge
  task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators --> task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge
  task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
