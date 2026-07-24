<!-- guardrails:graph v1 source-sha256=316a6638f2f84fe6c4b79fa0f778c08e54bcb0f421acbf98e6d0db0b6d531d7b -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1_preflights["Wave 1 Entry Gate"]
    wave_1_preflights_0["01-baseline-core-review-tests-green"]:::preflight
    wave_1_preflights_1["02-baseline-cli-review-tests-green"]:::preflight
  end
  style wave_1_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1["Wave 1 — evidence-hygiene"]
    subgraph task_wave_01_evidence_hygiene_01_ssot_review_marker_delta["01-ssot-review-marker-delta"]
      task_wave_01_evidence_hygiene_01_ssot_review_marker_delta_gr_0["01-ssot-attestation-documented"]:::guardrail
    end
    style task_wave_01_evidence_hygiene_01_ssot_review_marker_delta fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_evidence_hygiene_02_amend_open_k["02-amend-open-k"]
      task_wave_01_evidence_hygiene_02_amend_open_k_gr_0["01-open-k-resolved"]:::guardrail
    end
    style task_wave_01_evidence_hygiene_02_amend_open_k fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_evidence_hygiene_03_author_tests_review_attestation["03-author-tests-review-attestation"]
      task_wave_01_evidence_hygiene_03_author_tests_review_attestation_gr_0["01-tests-build"]:::guardrail
      task_wave_01_evidence_hygiene_03_author_tests_review_attestation_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_01_evidence_hygiene_03_author_tests_review_attestation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_evidence_hygiene_04_implement_review_attestation["04-implement-review-attestation"]
      task_wave_01_evidence_hygiene_04_implement_review_attestation_gr_0["01-attestation-tests-pass"]:::guardrail
    end
    style task_wave_01_evidence_hygiene_04_implement_review_attestation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2["05-author-tests-mark-reviewed-f2"]
      task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2_gr_0["01-tests-build"]:::guardrail
      task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2 fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2["06-implement-mark-reviewed-f2"]
      task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2_gr_0["01-mark-reviewed-f2-tests-pass"]:::guardrail
    end
    style task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2 fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_evidence_hygiene_07_author_tests_plan_hash["07-author-tests-plan-hash"]
      task_wave_01_evidence_hygiene_07_author_tests_plan_hash_gr_0["01-tests-build"]:::guardrail
      task_wave_01_evidence_hygiene_07_author_tests_plan_hash_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_01_evidence_hygiene_07_author_tests_plan_hash fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_evidence_hygiene_08_implement_plan_hash["08-implement-plan-hash"]
      task_wave_01_evidence_hygiene_08_implement_plan_hash_gr_0["01-plan-hash-tests-pass"]:::guardrail
      task_wave_01_evidence_hygiene_08_implement_plan_hash_gr_1["02-plan-hash-registered"]:::guardrail
    end
    style task_wave_01_evidence_hygiene_08_implement_plan_hash fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_evidence_hygiene_09_update_guardrails_review_skill["09-update-guardrails-review-skill"]
      task_wave_01_evidence_hygiene_09_update_guardrails_review_skill_gr_0["01-review-skill-updated"]:::guardrail
    end
    style task_wave_01_evidence_hygiene_09_update_guardrails_review_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_1 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_1_guardrails["Wave 1 Exit Gate"]
    wave_1_guardrails_0["01-wave1-union-clean"]:::guardrail
    wave_1_guardrails_1["02-wave1-solution-builds"]:::guardrail
    wave_1_guardrails_2["03-wave1-evidence-tests-pass"]:::guardrail
  end
  style wave_1_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2_preflights["Wave 2 Entry Gate"]
    wave_2_preflights_0["01-wave1-materialized"]:::preflight
  end
  style wave_2_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2["Wave 2 — dial-config"]
    subgraph task_wave_02_dial_config_01_ssot_autonomy_delta["01-ssot-autonomy-delta"]
      task_wave_02_dial_config_01_ssot_autonomy_delta_gr_0["01-ssot-autonomy-documented"]:::guardrail
    end
    style task_wave_02_dial_config_01_ssot_autonomy_delta fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_dial_config_02_author_tests_autonomy_config["02-author-tests-autonomy-config"]
      task_wave_02_dial_config_02_author_tests_autonomy_config_gr_0["01-tests-build"]:::guardrail
      task_wave_02_dial_config_02_author_tests_autonomy_config_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_02_dial_config_02_author_tests_autonomy_config fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_dial_config_03_implement_autonomy_config["03-implement-autonomy-config"]
      task_wave_02_dial_config_03_implement_autonomy_config_gr_0["01-autonomy-config-tests-pass"]:::guardrail
    end
    style task_wave_02_dial_config_03_implement_autonomy_config fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_dial_config_04_author_tests_autonomy_validation["04-author-tests-autonomy-validation"]
      task_wave_02_dial_config_04_author_tests_autonomy_validation_gr_0["01-tests-build"]:::guardrail
      task_wave_02_dial_config_04_author_tests_autonomy_validation_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_02_dial_config_04_author_tests_autonomy_validation_gr_2["03-covers-gr2040-cases"]:::guardrail
    end
    style task_wave_02_dial_config_04_author_tests_autonomy_validation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_dial_config_05_implement_autonomy_validation["05-implement-autonomy-validation"]
      task_wave_02_dial_config_05_implement_autonomy_validation_gr_0["01-autonomy-validation-tests-pass"]:::guardrail
    end
    style task_wave_02_dial_config_05_implement_autonomy_validation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_dial_config_06_author_tests_autonomous_cli["06-author-tests-autonomous-cli"]
      task_wave_02_dial_config_06_author_tests_autonomous_cli_gr_0["01-tests-build"]:::guardrail
      task_wave_02_dial_config_06_author_tests_autonomous_cli_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_02_dial_config_06_author_tests_autonomous_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_dial_config_07_implement_autonomous_cli["07-implement-autonomous-cli"]
      task_wave_02_dial_config_07_implement_autonomous_cli_gr_0["01-autonomous-cli-tests-pass"]:::guardrail
    end
    style task_wave_02_dial_config_07_implement_autonomous_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_2 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_2_guardrails["Wave 2 Exit Gate"]
    wave_2_guardrails_0["01-wave2-union-clean"]:::guardrail
    wave_2_guardrails_1["02-wave2-solution-builds"]:::guardrail
    wave_2_guardrails_2["03-wave2-dial-tests-pass"]:::guardrail
  end
  style wave_2_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3_preflights["Wave 3 Entry Gate"]
    wave_3_preflights_0["01-wave2-materialized"]:::preflight
  end
  style wave_3_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3["Wave 3 — classify-and-escalate"]
    subgraph task_wave_03_classify_and_escalate_01_ssot_phase3_delta["01-ssot-phase3-delta"]
      task_wave_03_classify_and_escalate_01_ssot_phase3_delta_gr_0["01-ssot-phase3-documented"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_01_ssot_phase3_delta fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_02_author_tests_forensic_records["02-author-tests-forensic-records"]
      task_wave_03_classify_and_escalate_02_author_tests_forensic_records_gr_0["01-tests-build"]:::guardrail
      task_wave_03_classify_and_escalate_02_author_tests_forensic_records_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_03_classify_and_escalate_02_author_tests_forensic_records_gr_2["03-covers-forensic-roundtrip"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_02_author_tests_forensic_records fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_03_implement_forensic_records["03-implement-forensic-records"]
      task_wave_03_classify_and_escalate_03_implement_forensic_records_gr_0["01-forensic-records-tests-pass"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_03_implement_forensic_records fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_04_author_tests_gate_classifier["04-author-tests-gate-classifier"]
      task_wave_03_classify_and_escalate_04_author_tests_gate_classifier_gr_0["01-tests-build"]:::guardrail
      task_wave_03_classify_and_escalate_04_author_tests_gate_classifier_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_03_classify_and_escalate_04_author_tests_gate_classifier_gr_2["03-covers-classification-signals"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_04_author_tests_gate_classifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_05_implement_gate_classifier["05-implement-gate-classifier"]
      task_wave_03_classify_and_escalate_05_implement_gate_classifier_gr_0["01-gate-classifier-tests-pass"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_05_implement_gate_classifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_06_author_tests_blocker_retry["06-author-tests-blocker-retry"]
      task_wave_03_classify_and_escalate_06_author_tests_blocker_retry_gr_0["01-tests-build"]:::guardrail
      task_wave_03_classify_and_escalate_06_author_tests_blocker_retry_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_03_classify_and_escalate_06_author_tests_blocker_retry_gr_2["03-covers-blocker-bounds"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_06_author_tests_blocker_retry fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_07_implement_blocker_retry["07-implement-blocker-retry"]
      task_wave_03_classify_and_escalate_07_implement_blocker_retry_gr_0["01-blocker-retry-tests-pass"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_07_implement_blocker_retry fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment["08-author-tests-criticality-assessment"]
      task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment_gr_0["01-tests-build"]:::guardrail
      task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment_gr_2["03-covers-assessment-invariants"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_09_implement_criticality_assessment["09-implement-criticality-assessment"]
      task_wave_03_classify_and_escalate_09_implement_criticality_assessment_gr_0["01-criticality-assessment-tests-pass"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_09_implement_criticality_assessment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_10_author_tests_escalation_sink["10-author-tests-escalation-sink"]
      task_wave_03_classify_and_escalate_10_author_tests_escalation_sink_gr_0["01-tests-build"]:::guardrail
      task_wave_03_classify_and_escalate_10_author_tests_escalation_sink_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_03_classify_and_escalate_10_author_tests_escalation_sink_gr_2["03-covers-escalation-invariants"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_10_author_tests_escalation_sink fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_11_implement_escalation_sink["11-implement-escalation-sink"]
      task_wave_03_classify_and_escalate_11_implement_escalation_sink_gr_0["01-escalation-sink-tests-pass"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_11_implement_escalation_sink fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_12_author_tests_answer_consumption["12-author-tests-answer-consumption"]
      task_wave_03_classify_and_escalate_12_author_tests_answer_consumption_gr_0["01-tests-build"]:::guardrail
      task_wave_03_classify_and_escalate_12_author_tests_answer_consumption_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_03_classify_and_escalate_12_author_tests_answer_consumption_gr_2["03-covers-security-matrix"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_12_author_tests_answer_consumption fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_13_implement_answer_consumption["13-implement-answer-consumption"]
      task_wave_03_classify_and_escalate_13_implement_answer_consumption_gr_0["01-answer-consumption-tests-pass"]:::guardrail
      task_wave_03_classify_and_escalate_13_implement_answer_consumption_gr_1["02-answer-injection-untrusted-delimited"]:::guardrail
      task_wave_03_classify_and_escalate_13_implement_answer_consumption_gr_2["03-no-review-attested-kind"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_13_implement_answer_consumption fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring["14-author-tests-scheduler-escalation-wiring"]
      task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring_gr_0["01-tests-build"]:::guardrail
      task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring_gr_2["03-covers-wiring-behaviors"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_14_author_tests_scheduler_escalation_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory["15-wire-escalation-components-into-factory"]
      task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory_gr_0["01-wire-escalation-structural"]:::guardrail
      task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory_gr_1["02-solution-builds"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_15_wire_escalation_components_into_factory fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler["16-wire-classify-then-act-into-scheduler"]
      task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler_gr_0["01-solution-builds"]:::guardrail
      task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler_gr_1["02-classify-then-act-wiring-tests-pass"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_16_wire_classify_then_act_into_scheduler fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root["17-wire-exit-code-and-verify-composition-root"]
      task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root_gr_0["01-exit-code-escalations-pending"]:::guardrail
      task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root_gr_1["02-factory-wires-escalation-tests-pass"]:::guardrail
    end
    style task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_3 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_3_guardrails["Wave 3 Exit Gate"]
    wave_3_guardrails_0["01-wave3-union-clean"]:::guardrail
    wave_3_guardrails_1["02-wave3-solution-builds"]:::guardrail
    wave_3_guardrails_2["03-wave3-classify-escalate-tests-pass"]:::guardrail
  end
  style wave_3_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4_preflights["Wave 4 Entry Gate"]
    wave_4_preflights_0["01-wave3-materialized"]:::preflight
  end
  style wave_4_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4["Wave 4 — review-gate-policy"]
    subgraph task_wave_04_review_gate_policy_01_ssot_phase4_delta["01-ssot-phase4-delta"]
      task_wave_04_review_gate_policy_01_ssot_phase4_delta_gr_0["01-ssot-phase4-documented"]:::guardrail
    end
    style task_wave_04_review_gate_policy_01_ssot_phase4_delta fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy["02-author-tests-run-outcome-policy"]
      task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy_gr_0["01-tests-build"]:::guardrail
      task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy_gr_2["03-covers-outcome-cases"]:::guardrail
    end
    style task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_review_gate_policy_03_implement_run_outcome_policy["03-implement-run-outcome-policy"]
      task_wave_04_review_gate_policy_03_implement_run_outcome_policy_gr_0["01-run-outcome-policy-tests-pass"]:::guardrail
    end
    style task_wave_04_review_gate_policy_03_implement_run_outcome_policy fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution["04-author-tests-review-gate-resolution"]
      task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution_gr_0["01-tests-build"]:::guardrail
      task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution_gr_2["03-covers-review-gate-cases"]:::guardrail
    end
    style task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop["05-wire-review-gate-resolution-into-wave-loop"]
      task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop_gr_0["01-review-gate-resolution-structural"]:::guardrail
      task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop_gr_1["02-no-forged-review-marker"]:::guardrail
      task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop_gr_2["03-review-gate-resolution-tests-pass"]:::guardrail
    end
    style task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier["06-author-tests-overwatch-auto-tier"]
      task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier_gr_0["01-tests-build"]:::guardrail
      task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier_gr_2["03-covers-auto-tier-cases"]:::guardrail
    end
    style task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier["07-implement-overwatch-auto-tier"]
      task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier_gr_0["01-auto-tier-structural"]:::guardrail
      task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier_gr_1["02-overwatch-auto-tier-tests-pass"]:::guardrail
    end
    style task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring["08-author-tests-run-outcome-wiring"]
      task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring_gr_0["01-tests-build"]:::guardrail
      task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring_gr_2["03-covers-wiring-facts"]:::guardrail
    end
    style task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize["09-wire-run-outcome-into-finalize"]
      task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize_gr_0["01-solution-builds"]:::guardrail
      task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize_gr_1["02-finalize-run-outcome-structural"]:::guardrail
    end
    style task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify["10-wire-proceeded-unreviewed-exit-and-verify"]
      task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify_gr_0["01-exit-code-proceeded-unreviewed-structural"]:::guardrail
      task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify_gr_1["02-run-outcome-wiring-tests-pass"]:::guardrail
    end
    style task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_4 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_4_guardrails["Wave 4 Exit Gate"]
    wave_4_guardrails_0["01-wave4-union-clean"]:::guardrail
    wave_4_guardrails_1["02-wave4-solution-builds"]:::guardrail
    wave_4_guardrails_2["03-wave4-review-gate-policy-tests-pass"]:::guardrail
  end
  style wave_4_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph plan_guardrails["Terminal Gate"]
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> wave_1_preflights
  wave_1_preflights --> task_wave_01_evidence_hygiene_01_ssot_review_marker_delta
  wave_1_preflights --> task_wave_01_evidence_hygiene_02_amend_open_k
  wave_1_preflights --> task_wave_01_evidence_hygiene_03_author_tests_review_attestation
  wave_1_preflights --> task_wave_01_evidence_hygiene_07_author_tests_plan_hash
  task_wave_01_evidence_hygiene_03_author_tests_review_attestation --> task_wave_01_evidence_hygiene_04_implement_review_attestation
  task_wave_01_evidence_hygiene_04_implement_review_attestation --> task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2
  task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2 --> task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2
  task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2 --> task_wave_01_evidence_hygiene_09_update_guardrails_review_skill
  task_wave_01_evidence_hygiene_07_author_tests_plan_hash --> task_wave_01_evidence_hygiene_08_implement_plan_hash
  task_wave_01_evidence_hygiene_08_implement_plan_hash --> task_wave_01_evidence_hygiene_09_update_guardrails_review_skill
  task_wave_01_evidence_hygiene_01_ssot_review_marker_delta --> wave_1_guardrails
  task_wave_01_evidence_hygiene_02_amend_open_k --> wave_1_guardrails
  task_wave_01_evidence_hygiene_09_update_guardrails_review_skill --> wave_1_guardrails
  wave_2_preflights --> task_wave_02_dial_config_01_ssot_autonomy_delta
  wave_2_preflights --> task_wave_02_dial_config_02_author_tests_autonomy_config
  task_wave_02_dial_config_02_author_tests_autonomy_config --> task_wave_02_dial_config_03_implement_autonomy_config
  task_wave_02_dial_config_03_implement_autonomy_config --> task_wave_02_dial_config_04_author_tests_autonomy_validation
  task_wave_02_dial_config_03_implement_autonomy_config --> task_wave_02_dial_config_06_author_tests_autonomous_cli
  task_wave_02_dial_config_04_author_tests_autonomy_validation --> task_wave_02_dial_config_05_implement_autonomy_validation
  task_wave_02_dial_config_05_implement_autonomy_validation --> task_wave_02_dial_config_07_implement_autonomous_cli
  task_wave_02_dial_config_06_author_tests_autonomous_cli --> task_wave_02_dial_config_07_implement_autonomous_cli
  task_wave_02_dial_config_01_ssot_autonomy_delta --> wave_2_guardrails
  task_wave_02_dial_config_07_implement_autonomous_cli --> wave_2_guardrails
  wave_3_preflights --> task_wave_03_classify_and_escalate_01_ssot_phase3_delta
  wave_3_preflights --> task_wave_03_classify_and_escalate_02_author_tests_forensic_records
  wave_3_preflights --> task_wave_03_classify_and_escalate_04_author_tests_gate_classifier
  wave_3_preflights --> task_wave_03_classify_and_escalate_06_author_tests_blocker_retry
  wave_3_preflights --> task_wave_03_classify_and_escalate_08_author_tests_criticality_assessment
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
  task_wave_03_classify_and_escalate_01_ssot_phase3_delta --> wave_3_guardrails
  task_wave_03_classify_and_escalate_17_wire_exit_code_and_verify_composition_root --> wave_3_guardrails
  wave_4_preflights --> task_wave_04_review_gate_policy_01_ssot_phase4_delta
  wave_4_preflights --> task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy
  wave_4_preflights --> task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution
  wave_4_preflights --> task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier
  wave_4_preflights --> task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring
  task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy --> task_wave_04_review_gate_policy_03_implement_run_outcome_policy
  task_wave_04_review_gate_policy_03_implement_run_outcome_policy --> task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize
  task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution --> task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop
  task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop --> task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize
  task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier --> task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier
  task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring --> task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify
  task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize --> task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify
  task_wave_04_review_gate_policy_01_ssot_phase4_delta --> wave_4_guardrails
  task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier --> wave_4_guardrails
  task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify --> wave_4_guardrails
  wave_1_guardrails -.->|"🔒 wave barrier"| wave_2_preflights
  wave_2_guardrails -.->|"🔒 wave barrier"| wave_3_preflights
  wave_3_guardrails -.->|"🔒 wave barrier"| wave_4_preflights
  wave_4_guardrails --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
