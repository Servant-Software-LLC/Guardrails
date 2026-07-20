<!-- guardrails:graph v1 source-sha256=477735f223114600726ad6811d96175c9fb2ac84455bd93c8342852319378a6f -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-review-tests-green"]:::preflight
    plan_preflights_1["02-baseline-cli-review-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_01_evidence_hygiene_01_ssot_review_marker_delta["wave-01-evidence-hygiene/01-ssot-review-marker-delta"]
    task_wave_01_evidence_hygiene_01_ssot_review_marker_delta_gr_0["01-ssot-attestation-documented"]:::guardrail
  end
  style task_wave_01_evidence_hygiene_01_ssot_review_marker_delta fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_evidence_hygiene_02_amend_open_k["wave-01-evidence-hygiene/02-amend-open-k"]
    task_wave_01_evidence_hygiene_02_amend_open_k_gr_0["01-open-k-resolved"]:::guardrail
  end
  style task_wave_01_evidence_hygiene_02_amend_open_k fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_evidence_hygiene_03_author_tests_review_attestation["wave-01-evidence-hygiene/03-author-tests-review-attestation"]
    task_wave_01_evidence_hygiene_03_author_tests_review_attestation_gr_0["01-tests-build"]:::guardrail
    task_wave_01_evidence_hygiene_03_author_tests_review_attestation_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_01_evidence_hygiene_03_author_tests_review_attestation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_evidence_hygiene_04_implement_review_attestation["wave-01-evidence-hygiene/04-implement-review-attestation"]
    task_wave_01_evidence_hygiene_04_implement_review_attestation_gr_0["01-attestation-tests-pass"]:::guardrail
  end
  style task_wave_01_evidence_hygiene_04_implement_review_attestation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2["wave-01-evidence-hygiene/05-author-tests-mark-reviewed-f2"]
    task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2_gr_0["01-tests-build"]:::guardrail
    task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2 fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2["wave-01-evidence-hygiene/06-implement-mark-reviewed-f2"]
    task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2_gr_0["01-mark-reviewed-f2-tests-pass"]:::guardrail
  end
  style task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2 fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_evidence_hygiene_07_author_tests_plan_hash["wave-01-evidence-hygiene/07-author-tests-plan-hash"]
    task_wave_01_evidence_hygiene_07_author_tests_plan_hash_gr_0["01-tests-build"]:::guardrail
    task_wave_01_evidence_hygiene_07_author_tests_plan_hash_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_01_evidence_hygiene_07_author_tests_plan_hash fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_evidence_hygiene_08_implement_plan_hash["wave-01-evidence-hygiene/08-implement-plan-hash"]
    task_wave_01_evidence_hygiene_08_implement_plan_hash_gr_0["01-plan-hash-tests-pass"]:::guardrail
    task_wave_01_evidence_hygiene_08_implement_plan_hash_gr_1["02-plan-hash-registered"]:::guardrail
  end
  style task_wave_01_evidence_hygiene_08_implement_plan_hash fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_evidence_hygiene_09_update_guardrails_review_skill["wave-01-evidence-hygiene/09-update-guardrails-review-skill"]
    task_wave_01_evidence_hygiene_09_update_guardrails_review_skill_gr_0["01-review-skill-updated"]:::guardrail
  end
  style task_wave_01_evidence_hygiene_09_update_guardrails_review_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-wave1-union-clean"]:::guardrail
    plan_guardrails_1["02-wave1-solution-builds"]:::guardrail
    plan_guardrails_2["03-wave1-evidence-tests-pass"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_01_evidence_hygiene_01_ssot_review_marker_delta
  plan_preflights --> task_wave_01_evidence_hygiene_02_amend_open_k
  plan_preflights --> task_wave_01_evidence_hygiene_03_author_tests_review_attestation
  plan_preflights --> task_wave_01_evidence_hygiene_07_author_tests_plan_hash
  task_wave_01_evidence_hygiene_03_author_tests_review_attestation --> task_wave_01_evidence_hygiene_04_implement_review_attestation
  task_wave_01_evidence_hygiene_04_implement_review_attestation --> task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2
  task_wave_01_evidence_hygiene_05_author_tests_mark_reviewed_f2 --> task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2
  task_wave_01_evidence_hygiene_06_implement_mark_reviewed_f2 --> task_wave_01_evidence_hygiene_09_update_guardrails_review_skill
  task_wave_01_evidence_hygiene_07_author_tests_plan_hash --> task_wave_01_evidence_hygiene_08_implement_plan_hash
  task_wave_01_evidence_hygiene_08_implement_plan_hash --> task_wave_01_evidence_hygiene_09_update_guardrails_review_skill
  task_wave_01_evidence_hygiene_01_ssot_review_marker_delta --> plan_guardrails
  task_wave_01_evidence_hygiene_02_amend_open_k --> plan_guardrails
  task_wave_01_evidence_hygiene_09_update_guardrails_review_skill --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
