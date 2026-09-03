<!-- guardrails:graph v1 source-sha256=018a7e77700c0b03a8f8c6f9b954aafad90fe3c5d153054da66edeaaf6300ca3 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_attempt_completion_seam["01-author-tests-attempt-completion-seam"]
    task_01_author_tests_attempt_completion_seam_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_attempt_completion_seam_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_attempt_completion_seam fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_decorator_forwarding["02-implement-decorator-forwarding"]
    task_02_implement_decorator_forwarding_gr_0["01-build-passes"]:::guardrail
    task_02_implement_decorator_forwarding_gr_1["02-forwarding-tests-pass"]:::guardrail
  end
  style task_02_implement_decorator_forwarding fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_executor_raises_completion["03-author-tests-executor-raises-completion"]
    task_03_author_tests_executor_raises_completion_gr_0["01-build-passes"]:::guardrail
    task_03_author_tests_executor_raises_completion_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_03_author_tests_executor_raises_completion fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_executor_raises_completion["04-implement-executor-raises-completion"]
    task_04_implement_executor_raises_completion_gr_0["01-build-passes"]:::guardrail
    task_04_implement_executor_raises_completion_gr_1["02-executor-tests-pass"]:::guardrail
  end
  style task_04_implement_executor_raises_completion fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_events_projection["05-author-tests-events-projection"]
    task_05_author_tests_events_projection_gr_0["01-build-passes"]:::guardrail
    task_05_author_tests_events_projection_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_05_author_tests_events_projection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_implement_events_projection["06-implement-events-projection"]
    task_06_implement_events_projection_gr_0["01-build-passes"]:::guardrail
    task_06_implement_events_projection_gr_1["02-events-tests-pass"]:::guardrail
  end
  style task_06_implement_events_projection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_observer_projection["07-author-tests-observer-projection"]
    task_07_author_tests_observer_projection_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_observer_projection_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_07_author_tests_observer_projection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_implement_observer_projection["08-implement-observer-projection"]
    task_08_implement_observer_projection_gr_0["01-build-passes"]:::guardrail
    task_08_implement_observer_projection_gr_1["02-projection-tests-pass"]:::guardrail
  end
  style task_08_implement_observer_projection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_author_tests_attach_replay["09-author-tests-attach-replay"]
    task_09_author_tests_attach_replay_gr_0["01-build-passes"]:::guardrail
    task_09_author_tests_attach_replay_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_09_author_tests_attach_replay fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_implement_attach_command["10-implement-attach-command"]
    task_10_implement_attach_command_gr_0["01-build-passes"]:::guardrail
    task_10_implement_attach_command_gr_1["02-attach-tests-pass"]:::guardrail
  end
  style task_10_implement_attach_command fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_author_tests_events_endpoint["11-author-tests-events-endpoint"]
    task_11_author_tests_events_endpoint_gr_0["01-build-passes"]:::guardrail
    task_11_author_tests_events_endpoint_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_11_author_tests_events_endpoint fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_implement_events_endpoint["12-implement-events-endpoint"]
    task_12_implement_events_endpoint_gr_0["01-build-passes"]:::guardrail
    task_12_implement_events_endpoint_gr_1["02-endpoint-tests-pass"]:::guardrail
  end
  style task_12_implement_events_endpoint fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_extract_observer_composition_seam["13-extract-observer-composition-seam"]
    task_13_extract_observer_composition_seam_gr_0["01-build-passes"]:::guardrail
    task_13_extract_observer_composition_seam_gr_1["02-composition-seam-exists"]:::guardrail
    task_13_extract_observer_composition_seam_gr_2["03-existing-observer-tests-pass"]:::guardrail
  end
  style task_13_extract_observer_composition_seam fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_author_tests_composition_root_wiring["14-author-tests-composition-root-wiring"]
    task_14_author_tests_composition_root_wiring_gr_0["01-build-passes"]:::guardrail
    task_14_author_tests_composition_root_wiring_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_14_author_tests_composition_root_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_wire_projections_into_composition_root["15-wire-projections-into-composition-root"]
    task_15_wire_projections_into_composition_root_gr_0["01-build-passes"]:::guardrail
    task_15_wire_projections_into_composition_root_gr_1["02-wiring-tests-pass"]:::guardrail
  end
  style task_15_wire_projections_into_composition_root fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-union-intact"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_attempt_completion_seam
  task_01_author_tests_attempt_completion_seam --> task_02_implement_decorator_forwarding
  task_01_author_tests_attempt_completion_seam --> task_03_author_tests_executor_raises_completion
  task_01_author_tests_attempt_completion_seam --> task_05_author_tests_events_projection
  task_01_author_tests_attempt_completion_seam --> task_07_author_tests_observer_projection
  task_02_implement_decorator_forwarding --> task_13_extract_observer_composition_seam
  task_03_author_tests_executor_raises_completion --> task_04_implement_executor_raises_completion
  task_05_author_tests_events_projection --> task_06_implement_events_projection
  task_05_author_tests_events_projection --> task_11_author_tests_events_endpoint
  task_06_implement_events_projection --> task_12_implement_events_endpoint
  task_06_implement_events_projection --> task_14_author_tests_composition_root_wiring
  task_07_author_tests_observer_projection --> task_08_implement_observer_projection
  task_07_author_tests_observer_projection --> task_09_author_tests_attach_replay
  task_08_implement_observer_projection --> task_14_author_tests_composition_root_wiring
  task_09_author_tests_attach_replay --> task_10_implement_attach_command
  task_11_author_tests_events_endpoint --> task_12_implement_events_endpoint
  task_13_extract_observer_composition_seam --> task_14_author_tests_composition_root_wiring
  task_14_author_tests_composition_root_wiring --> task_15_wire_projections_into_composition_root
  task_04_implement_executor_raises_completion --> plan_guardrails
  task_10_implement_attach_command --> plan_guardrails
  task_12_implement_events_endpoint --> plan_guardrails
  task_15_wire_projections_into_composition_root --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
