<!-- guardrails:graph v1 source-sha256=063d12078987ce4a2d1627347b451ccde7dd2ee824550b6190323030b9900e31 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave2-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_03_classify_and_escalate_01_ssot_phase3_delta["wave-03-classify-and-escalate/01-ssot-phase3-delta"]
    task_wave_03_classify_and_escalate_01_ssot_phase3_delta_gr_0["01-ssot-phase3-documented"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_01_ssot_phase3_delta fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_02_author_tests_forensic_records["wave-03-classify-and-escalate/02-author-tests-forensic-records"]
    task_wave_03_classify_and_escalate_02_author_tests_forensic_records_gr_0["01-tests-build"]:::guardrail
    task_wave_03_classify_and_escalate_02_author_tests_forensic_records_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_classify_and_escalate_02_author_tests_forensic_records_gr_2["03-covers-forensic-roundtrip"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_02_author_tests_forensic_records fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_03_implement_forensic_records["wave-03-classify-and-escalate/03-implement-forensic-records"]
    task_wave_03_classify_and_escalate_03_implement_forensic_records_gr_0["01-forensic-records-tests-pass"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_03_implement_forensic_records fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_04_author_tests_gate_classifier["wave-03-classify-and-escalate/04-author-tests-gate-classifier"]
    task_wave_03_classify_and_escalate_04_author_tests_gate_classifier_gr_0["01-tests-build"]:::guardrail
    task_wave_03_classify_and_escalate_04_author_tests_gate_classifier_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_classify_and_escalate_04_author_tests_gate_classifier_gr_2["03-covers-classification-signals"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_04_author_tests_gate_classifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_05_implement_gate_classifier["wave-03-classify-and-escalate/05-implement-gate-classifier"]
    task_wave_03_classify_and_escalate_05_implement_gate_classifier_gr_0["01-gate-classifier-tests-pass"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_05_implement_gate_classifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_06_author_tests_blocker_retry["wave-03-classify-and-escalate/06-author-tests-blocker-retry"]
    task_wave_03_classify_and_escalate_06_author_tests_blocker_retry_gr_0["01-tests-build"]:::guardrail
    task_wave_03_classify_and_escalate_06_author_tests_blocker_retry_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_classify_and_escalate_06_author_tests_blocker_retry_gr_2["03-covers-blocker-bounds"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_06_author_tests_blocker_retry fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_07_implement_blocker_retry["wave-03-classify-and-escalate/07-implement-blocker-retry"]
    task_wave_03_classify_and_escalate_07_implement_blocker_retry_gr_0["01-blocker-retry-tests-pass"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_07_implement_blocker_retry fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment["wave-03-classify-and-escalate/08-author-tests-criticality-assessment"]
    task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment_gr_0["01-tests-build"]:::guardrail
    task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment_gr_2["03-covers-assessment-invariants"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_09_implement_criticality_assessment["wave-03-classify-and-escalate/09-implement-criticality-assessment"]
    task_wave_03_classify_and_escalate_09_implement_criticality_assessment_gr_0["01-criticality-assessment-tests-pass"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_09_implement_criticality_assessment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_10_author_tests_escalation_sink["wave-03-classify-and-escalate/10-author-tests-escalation-sink"]
    task_wave_03_classify_and_escalate_10_author_tests_escalation_sink_gr_0["01-tests-build"]:::guardrail
    task_wave_03_classify_and_escalate_10_author_tests_escalation_sink_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_classify_and_escalate_10_author_tests_escalation_sink_gr_2["03-covers-escalation-invariants"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_10_author_tests_escalation_sink fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_11_implement_escalation_sink["wave-03-classify-and-escalate/11-implement-escalation-sink"]
    task_wave_03_classify_and_escalate_11_implement_escalation_sink_gr_0["01-escalation-sink-tests-pass"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_11_implement_escalation_sink fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_12_author_tests_answer_consumption["wave-03-classify-and-escalate/12-author-tests-answer-consumption"]
    task_wave_03_classify_and_escalate_12_author_tests_answer_consumption_gr_0["01-tests-build"]:::guardrail
    task_wave_03_classify_and_escalate_12_author_tests_answer_consumption_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_classify_and_escalate_12_author_tests_answer_consumption_gr_2["03-covers-security-matrix"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_12_author_tests_answer_consumption fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_13_implement_answer_consumption["wave-03-classify-and-escalate/13-implement-answer-consumption"]
    task_wave_03_classify_and_escalate_13_implement_answer_consumption_gr_0["01-answer-consumption-tests-pass"]:::guardrail
    task_wave_03_classify_and_escalate_13_implement_answer_consumption_gr_1["02-answer-injection-untrusted-delimited"]:::guardrail
    task_wave_03_classify_and_escalate_13_implement_answer_consumption_gr_2["03-no-review-attested-kind"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_13_implement_answer_consumption fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring["wave-03-classify-and-escalate/14-author-tests-scheduler-escalation-wiring"]
    task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring_gr_0["01-tests-build"]:::guardrail
    task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring_gr_2["03-covers-wiring-behaviors"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory["wave-03-classify-and-escalate/15-wire-escalation-components-into-factory"]
    task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory_gr_0["01-wire-escalation-structural"]:::guardrail
    task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory_gr_1["02-solution-builds"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler["wave-03-classify-and-escalate/16-wire-classify-then-act-into-scheduler"]
    task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler_gr_0["01-solution-builds"]:::guardrail
    task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler_gr_1["02-classify-then-act-wiring-tests-pass"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root["wave-03-classify-and-escalate/17-wire-exit-code-and-verify-composition-root"]
    task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root_gr_0["01-exit-code-escalations-pending"]:::guardrail
    task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root_gr_1["02-factory-wires-escalation-tests-pass"]:::guardrail
  end
  style task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-wave3-union-clean"]:::guardrail
    plan_guardrails_1["02-wave3-solution-builds"]:::guardrail
    plan_guardrails_2["03-wave3-classify-escalate-tests-pass"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_03_classify_and_escalate_01_ssot_phase3_delta
  plan_preflights --> task_wave_03_classify_and_escalate_02_author_tests_forensic_records
  plan_preflights --> task_wave_03_classify_and_escalate_04_author_tests_gate_classifier
  plan_preflights --> task_wave_03_classify_and_escalate_06_author_tests_blocker_retry
  plan_preflights --> task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment
  task_wave_03_classify_and_escalate_02_author_tests_forensic_records --> task_wave_03_classify_and_escalate_03_implement_forensic_records
  task_wave_03_classify_and_escalate_02_author_tests_forensic_records --> task_wave_03_classify_and_escalate_10_author_tests_escalation_sink
  task_wave_03_classify_and_escalate_03_implement_forensic_records --> task_wave_03_classify_and_escalate_11_implement_escalation_sink
  task_wave_03_classify_and_escalate_04_author_tests_gate_classifier --> task_wave_03_classify_and_escalate_05_implement_gate_classifier
  task_wave_03_classify_and_escalate_05_implement_gate_classifier --> task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring
  task_wave_03_classify_and_escalate_05_implement_gate_classifier --> task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory
  task_wave_03_classify_and_escalate_06_author_tests_blocker_retry --> task_wave_03_classify_and_escalate_07_implement_blocker_retry
  task_wave_03_classify_and_escalate_07_implement_blocker_retry --> task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring
  task_wave_03_classify_and_escalate_07_implement_blocker_retry --> task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory
  task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment --> task_wave_03_classify_and_escalate_09_implement_criticality_assessment
  task_wave_03_classify_and_escalate_09_implement_criticality_assessment --> task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring
  task_wave_03_classify_and_escalate_09_implement_criticality_assessment --> task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory
  task_wave_03_classify_and_escalate_10_author_tests_escalation_sink --> task_wave_03_classify_and_escalate_11_implement_escalation_sink
  task_wave_03_classify_and_escalate_10_author_tests_escalation_sink --> task_wave_03_classify_and_escalate_12_author_tests_answer_consumption
  task_wave_03_classify_and_escalate_11_implement_escalation_sink --> task_wave_03_classify_and_escalate_13_implement_answer_consumption
  task_wave_03_classify_and_escalate_11_implement_escalation_sink --> task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring
  task_wave_03_classify_and_escalate_11_implement_escalation_sink --> task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory
  task_wave_03_classify_and_escalate_12_author_tests_answer_consumption --> task_wave_03_classify_and_escalate_13_implement_answer_consumption
  task_wave_03_classify_and_escalate_13_implement_answer_consumption --> task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring
  task_wave_03_classify_and_escalate_13_implement_answer_consumption --> task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory
  task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring --> task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler
  task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring --> task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root
  task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory --> task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler
  task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory --> task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root
  task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler --> task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root
  task_wave_03_classify_and_escalate_01_ssot_phase3_delta --> plan_guardrails
  task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
