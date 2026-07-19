<!-- guardrails:graph v1 source-sha256=8d0e46db53b1408403a2781f867e24f36b688465c587f9486a4622a769fff067 -->

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
  end
  style wave_3_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3["Wave 3 — classify-and-escalate"]
    wave_3_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_3_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_3 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_3_guardrails["Wave 3 Exit Gate"]
  end
  style wave_3_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
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
  wave_3_preflights --> wave_3_stub
  wave_3_stub --> wave_3_guardrails
  wave_1_guardrails -.->|"🔒 wave barrier"| wave_2_preflights
  wave_2_guardrails -.->|"🔒 wave barrier"| wave_3_preflights
  wave_3_guardrails --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
