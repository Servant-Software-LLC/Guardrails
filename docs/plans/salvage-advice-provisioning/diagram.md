<!-- guardrails:graph v1 source-sha256=0d16a1ddbc4f2b505232b41fd58dc5cff181a3453ac33ebcbd9fae7794b95823 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1_preflights["Wave 1 Entry Gate"]
    wave_1_preflights_0["01-baseline-core-retrypolicy-tests-green"]:::preflight
    wave_1_preflights_1["02-baseline-integration-salvage-tests-green"]:::preflight
  end
  style wave_1_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1["Wave 1 — correct-the-record"]
    subgraph task_wave_01_correct_the_record_01_correct_retrypolicy_rationale["01-correct-retrypolicy-rationale"]
      task_wave_01_correct_the_record_01_correct_retrypolicy_rationale_gr_0["01-false-claims-removed"]:::guardrail
      task_wave_01_correct_the_record_01_correct_retrypolicy_rationale_gr_1["02-builds"]:::guardrail
    end
    style task_wave_01_correct_the_record_01_correct_retrypolicy_rationale fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_correct_the_record_02_fix_ssot_drift["02-fix-ssot-drift"]
      task_wave_01_correct_the_record_02_fix_ssot_drift_gr_0["01-ssot-names-granted-route"]:::guardrail
    end
    style task_wave_01_correct_the_record_02_fix_ssot_drift fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording["03-correct-plan-breakdown-allowlist-wording"]
      task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording_gr_0["01-floor-not-ceiling-wording"]:::guardrail
    end
    style task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording["04-correct-guardrails-review-allowlist-wording"]
      task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording_gr_0["01-floor-not-ceiling-wording"]:::guardrail
    end
    style task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording["05-correct-guardrails-domain-knowledge-allowlist-wording"]
      task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording_gr_0["01-floor-not-ceiling-wording"]:::guardrail
    end
    style task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_1 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_1_guardrails["Wave 1 Exit Gate"]
    wave_1_guardrails_0["01-corrections-union-intact"]:::guardrail
  end
  style wave_1_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2_preflights["Wave 2 Entry Gate"]
    wave_2_preflights_0["01-wave1-corrections-materialized"]:::preflight
  end
  style wave_2_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2["Wave 2 — see-the-failure"]
    subgraph task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection["01-author-tests-bash-refusal-detection"]
      task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection_gr_0["01-tests-build"]:::guardrail
      task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection_gr_1["02-tests-fail-on-current-code"]:::guardrail
    end
    style task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_see_the_failure_02_implement_bash_refusal_detection["02-implement-bash-refusal-detection"]
      task_wave_02_see_the_failure_02_implement_bash_refusal_detection_gr_0["01-refusal-tests-pass"]:::guardrail
    end
    style task_wave_02_see_the_failure_02_implement_bash_refusal_detection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_2 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_2_guardrails["Wave 2 Exit Gate"]
    wave_2_guardrails_0["01-scanner-tests-pass"]:::guardrail
  end
  style wave_2_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3_preflights["Wave 3 Entry Gate"]
    wave_3_preflights_0["01-wave2-scanner-materialized"]:::preflight
  end
  style wave_3_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3["Wave 3 — provision-what-we-prescribe"]
    subgraph task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection["01-author-tests-grant-injection"]
      task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection_gr_0["01-tests-build"]:::guardrail
      task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection_gr_2["03-no-write-verb-asserted"]:::guardrail
    end
    style task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_provision_what_we_prescribe_02_implement_grant_injection["02-implement-grant-injection"]
      task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_0["01-injection-tests-pass"]:::guardrail
      task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_1["02-no-write-verb-injected"]:::guardrail
      task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_2["03-golden-args-tests-pass"]:::guardrail
      task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_3["04-golden-coverage-preserved"]:::guardrail
      task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_4["05-ssot-contract-line-landed"]:::guardrail
    end
    style task_wave_03_provision_what_we_prescribe_02_implement_grant_injection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance["03-record-injected-grants-in-provenance"]
      task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance_gr_0["01-provenance-records-injection"]:::guardrail
      task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance_gr_1["02-core-builds"]:::guardrail
      task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance_gr_2["03-log-header-echoes-injected-grants"]:::guardrail
    end
    style task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_3 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_3_guardrails["Wave 3 Exit Gate"]
    wave_3_guardrails_0["01-injection-tests-pass"]:::guardrail
  end
  style wave_3_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4_preflights["Wave 4 Entry Gate"]
    wave_4_preflights_0["01-wave3-injection-materialized"]:::preflight
  end
  style wave_4_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4["Wave 4 — fix-the-advice"]
    subgraph task_wave_04_fix_the_advice_01_author_tests_salvage_advice["01-author-tests-salvage-advice"]
      task_wave_04_fix_the_advice_01_author_tests_salvage_advice_gr_0["01-tests-build"]:::guardrail
      task_wave_04_fix_the_advice_01_author_tests_salvage_advice_gr_1["02-tests-fail-on-current-code"]:::guardrail
    end
    style task_wave_04_fix_the_advice_01_author_tests_salvage_advice fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_fix_the_advice_02_implement_salvage_advice["02-implement-salvage-advice"]
      task_wave_04_fix_the_advice_02_implement_salvage_advice_gr_0["01-advice-tests-pass"]:::guardrail
      task_wave_04_fix_the_advice_02_implement_salvage_advice_gr_1["02-no-ungranted-command-emitted"]:::guardrail
    end
    style task_wave_04_fix_the_advice_02_implement_salvage_advice fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory["03-reconcile-promptcomposer-advisory"]
      task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory_gr_0["01-advisory-has-no-unusable-recipe"]:::guardrail
      task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory_gr_1["02-composer-tests-pass"]:::guardrail
    end
    style task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_fix_the_advice_04_reconcile_containment_hook_message["04-reconcile-containment-hook-message"]
      task_wave_04_fix_the_advice_04_reconcile_containment_hook_message_gr_0["01-hook-message-reconciled"]:::guardrail
      task_wave_04_fix_the_advice_04_reconcile_containment_hook_message_gr_1["02-hook-logic-unchanged"]:::guardrail
    end
    style task_wave_04_fix_the_advice_04_reconcile_containment_hook_message fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_4 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_4_guardrails["Wave 4 Exit Gate"]
    wave_4_guardrails_0["01-solution-builds"]:::guardrail
    wave_4_guardrails_1["02-all-tests-pass"]:::guardrail
    wave_4_guardrails_2["03-union-conflict-marker-free"]:::guardrail
  end
  style wave_4_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph plan_guardrails["Terminal Gate"]
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> wave_1_preflights
  wave_1_preflights --> task_wave_01_correct_the_record_01_correct_retrypolicy_rationale
  wave_1_preflights --> task_wave_01_correct_the_record_02_fix_ssot_drift
  wave_1_preflights --> task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording
  wave_1_preflights --> task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording
  wave_1_preflights --> task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording
  task_wave_01_correct_the_record_01_correct_retrypolicy_rationale --> wave_1_guardrails
  task_wave_01_correct_the_record_02_fix_ssot_drift --> wave_1_guardrails
  task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording --> wave_1_guardrails
  task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording --> wave_1_guardrails
  task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording --> wave_1_guardrails
  wave_2_preflights --> task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection
  task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection --> task_wave_02_see_the_failure_02_implement_bash_refusal_detection
  task_wave_02_see_the_failure_02_implement_bash_refusal_detection --> wave_2_guardrails
  wave_3_preflights --> task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection
  task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection --> task_wave_03_provision_what_we_prescribe_02_implement_grant_injection
  task_wave_03_provision_what_we_prescribe_02_implement_grant_injection --> task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance
  task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance --> wave_3_guardrails
  wave_4_preflights --> task_wave_04_fix_the_advice_01_author_tests_salvage_advice
  task_wave_04_fix_the_advice_01_author_tests_salvage_advice --> task_wave_04_fix_the_advice_02_implement_salvage_advice
  task_wave_04_fix_the_advice_02_implement_salvage_advice --> task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory
  task_wave_04_fix_the_advice_02_implement_salvage_advice --> task_wave_04_fix_the_advice_04_reconcile_containment_hook_message
  task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory --> wave_4_guardrails
  task_wave_04_fix_the_advice_04_reconcile_containment_hook_message --> wave_4_guardrails
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
