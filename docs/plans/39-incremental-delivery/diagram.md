<!-- guardrails:graph v1 source-sha256=ba43a5095f6d1c6641e895767c59be77142787a3a5b5b7d93b9713c8a391f176 body-sha256=3b02ac80815f68fe617b6f8702e69b1994d8450bacf315812224bc443ee193c1 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_wave_delivers_flag["01-author-tests-wave-delivers-flag"]
    task_01_author_tests_wave_delivers_flag_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_wave_delivers_flag_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_wave_delivers_flag fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_wave_delivers_flag["02-implement-wave-delivers-flag"]
    task_02_implement_wave_delivers_flag_gr_0["01-tests-pass"]:::guardrail
    task_02_implement_wave_delivers_flag_gr_1["02-forward-census"]:::guardrail
  end
  style task_02_implement_wave_delivers_flag fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_delivery_diagnostics["03-author-tests-delivery-diagnostics"]
    task_03_author_tests_delivery_diagnostics_gr_0["01-build-passes"]:::guardrail
    task_03_author_tests_delivery_diagnostics_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_03_author_tests_delivery_diagnostics_gr_2["03-codes-and-marker"]:::guardrail
  end
  style task_03_author_tests_delivery_diagnostics fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_delivery_diagnostics["04-implement-delivery-diagnostics"]
    task_04_implement_delivery_diagnostics_gr_0["01-tests-pass"]:::guardrail
    task_04_implement_delivery_diagnostics_gr_1["02-forward-census"]:::guardrail
  end
  style task_04_implement_delivery_diagnostics fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_wave_scoped_interlock["05-author-tests-wave-scoped-interlock"]
    task_05_author_tests_wave_scoped_interlock_gr_0["01-build-passes"]:::guardrail
    task_05_author_tests_wave_scoped_interlock_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_05_author_tests_wave_scoped_interlock fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_implement_wave_scoped_interlock["06-implement-wave-scoped-interlock"]
    task_06_implement_wave_scoped_interlock_gr_0["01-tests-pass"]:::guardrail
    task_06_implement_wave_scoped_interlock_gr_1["02-forward-census"]:::guardrail
  end
  style task_06_implement_wave_scoped_interlock fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_deliver_at_wave_barrier["07-author-tests-deliver-at-wave-barrier"]
    task_07_author_tests_deliver_at_wave_barrier_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_deliver_at_wave_barrier_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_07_author_tests_deliver_at_wave_barrier_gr_2["03-drives-the-real-provider"]:::guardrail
  end
  style task_07_author_tests_deliver_at_wave_barrier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_implement_deliver_at_wave_barrier["08-implement-deliver-at-wave-barrier"]
    task_08_implement_deliver_at_wave_barrier_gr_0["01-tests-pass"]:::guardrail
    task_08_implement_deliver_at_wave_barrier_gr_1["02-forward-census"]:::guardrail
  end
  style task_08_implement_deliver_at_wave_barrier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_author_tests_wave_delivered_journal["09-author-tests-wave-delivered-journal"]
    task_09_author_tests_wave_delivered_journal_gr_0["01-build-passes"]:::guardrail
    task_09_author_tests_wave_delivered_journal_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_09_author_tests_wave_delivered_journal fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_implement_wave_delivered_journal["10-implement-wave-delivered-journal"]
    task_10_implement_wave_delivered_journal_gr_0["01-tests-pass"]:::guardrail
    task_10_implement_wave_delivered_journal_gr_1["02-forward-census"]:::guardrail
  end
  style task_10_implement_wave_delivered_journal fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_author_tests_wave_delivered_event["11-author-tests-wave-delivered-event"]
    task_11_author_tests_wave_delivered_event_gr_0["01-build-passes"]:::guardrail
    task_11_author_tests_wave_delivered_event_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_11_author_tests_wave_delivered_event_gr_2["03-cli-forwarding-tests-fail-on-stubs"]:::guardrail
  end
  style task_11_author_tests_wave_delivered_event fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_implement_wave_delivered_event_core["12-implement-wave-delivered-event-core"]
    task_12_implement_wave_delivered_event_core_gr_0["01-tests-pass"]:::guardrail
    task_12_implement_wave_delivered_event_core_gr_1["02-forward-census"]:::guardrail
  end
  style task_12_implement_wave_delivered_event_core fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_implement_wave_delivered_event_cli["13-implement-wave-delivered-event-cli"]
    task_13_implement_wave_delivered_event_cli_gr_0["01-tests-pass"]:::guardrail
    task_13_implement_wave_delivered_event_cli_gr_1["02-forward-census"]:::guardrail
  end
  style task_13_implement_wave_delivered_event_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_author_tests_post_delivery_refresh["14-author-tests-post-delivery-refresh"]
    task_14_author_tests_post_delivery_refresh_gr_0["01-build-passes"]:::guardrail
    task_14_author_tests_post_delivery_refresh_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_14_author_tests_post_delivery_refresh_gr_2["03-drives-real-provider"]:::guardrail
  end
  style task_14_author_tests_post_delivery_refresh fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_implement_post_delivery_refresh["15-implement-post-delivery-refresh"]
    task_15_implement_post_delivery_refresh_gr_0["01-tests-pass"]:::guardrail
    task_15_implement_post_delivery_refresh_gr_1["02-forward-census"]:::guardrail
  end
  style task_15_implement_post_delivery_refresh fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_16_author_tests_branchmoved_halt["16-author-tests-branchmoved-halt"]
    task_16_author_tests_branchmoved_halt_gr_0["01-build-passes"]:::guardrail
    task_16_author_tests_branchmoved_halt_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_16_author_tests_branchmoved_halt_gr_2["03-drives-real-provider"]:::guardrail
  end
  style task_16_author_tests_branchmoved_halt fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_17_implement_branchmoved_halt["17-implement-branchmoved-halt"]
    task_17_implement_branchmoved_halt_gr_0["01-tests-pass"]:::guardrail
    task_17_implement_branchmoved_halt_gr_1["02-forward-census"]:::guardrail
  end
  style task_17_implement_branchmoved_halt fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_18_author_tests_partial_delivery_report["18-author-tests-partial-delivery-report"]
    task_18_author_tests_partial_delivery_report_gr_0["01-build-passes"]:::guardrail
    task_18_author_tests_partial_delivery_report_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_18_author_tests_partial_delivery_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_19_implement_partial_delivery_report["19-implement-partial-delivery-report"]
    task_19_implement_partial_delivery_report_gr_0["01-tests-pass"]:::guardrail
    task_19_implement_partial_delivery_report_gr_1["02-forward-census"]:::guardrail
  end
  style task_19_implement_partial_delivery_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_20_update_ssot_wave_delivery["20-update-ssot-wave-delivery"]
    task_20_update_ssot_wave_delivery_gr_0["01-ssot-records-the-contracts"]:::guardrail
  end
  style task_20_update_ssot_wave_delivery fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_21_update_plan_breakdown_skill["21-update-plan-breakdown-skill"]
    task_21_update_plan_breakdown_skill_gr_0["01-skill-teaches-wave-delivery"]:::guardrail
  end
  style task_21_update_plan_breakdown_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_24_author_tests_refresh_provenance["24-author-tests-refresh-provenance"]
    task_24_author_tests_refresh_provenance_gr_0["01-build-passes"]:::guardrail
    task_24_author_tests_refresh_provenance_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_24_author_tests_refresh_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_25_implement_refresh_provenance["25-implement-refresh-provenance"]
    task_25_implement_refresh_provenance_gr_0["01-tests-pass"]:::guardrail
    task_25_implement_refresh_provenance_gr_1["02-forward-census"]:::guardrail
  end
  style task_25_implement_refresh_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_26_update_domain_knowledge_skill["26-update-domain-knowledge-skill"]
    task_26_update_domain_knowledge_skill_gr_0["01-skill-documents-wave-delivery"]:::guardrail
  end
  style task_26_update_domain_knowledge_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_27_update_readme_wave_delivery["27-update-readme-wave-delivery"]
    task_27_update_readme_wave_delivery_gr_0["01-readme-documents-wave-delivery"]:::guardrail
  end
  style task_27_update_readme_wave_delivery fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_28_author_tests_wave_delivery_wiring["28-author-tests-wave-delivery-wiring"]
    task_28_author_tests_wave_delivery_wiring_gr_0["01-build-passes"]:::guardrail
    task_28_author_tests_wave_delivery_wiring_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_28_author_tests_wave_delivery_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_29_implement_wave_delivery_wiring["29-implement-wave-delivery-wiring"]
    task_29_implement_wave_delivery_wiring_gr_0["01-tests-pass"]:::guardrail
    task_29_implement_wave_delivery_wiring_gr_1["02-forward-census"]:::guardrail
  end
  style task_29_implement_wave_delivery_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_30_author_tests_trial_delivery_primitive["30-author-tests-trial-delivery-primitive"]
    task_30_author_tests_trial_delivery_primitive_gr_0["01-build-passes"]:::guardrail
    task_30_author_tests_trial_delivery_primitive_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_30_author_tests_trial_delivery_primitive_gr_2["03-drives-the-real-provider"]:::guardrail
  end
  style task_30_author_tests_trial_delivery_primitive fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_31_implement_trial_delivery_primitive["31-implement-trial-delivery-primitive"]
    task_31_implement_trial_delivery_primitive_gr_0["01-tests-pass"]:::guardrail
    task_31_implement_trial_delivery_primitive_gr_1["02-forward-census"]:::guardrail
  end
  style task_31_implement_trial_delivery_primitive fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-union-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_wave_delivers_flag
  plan_preflights --> task_30_author_tests_trial_delivery_primitive
  task_01_author_tests_wave_delivers_flag --> task_02_implement_wave_delivers_flag
  task_02_implement_wave_delivers_flag --> task_03_author_tests_delivery_diagnostics
  task_02_implement_wave_delivers_flag --> task_05_author_tests_wave_scoped_interlock
  task_02_implement_wave_delivers_flag --> task_07_author_tests_deliver_at_wave_barrier
  task_02_implement_wave_delivers_flag --> task_09_author_tests_wave_delivered_journal
  task_02_implement_wave_delivers_flag --> task_21_update_plan_breakdown_skill
  task_03_author_tests_delivery_diagnostics --> task_04_implement_delivery_diagnostics
  task_04_implement_delivery_diagnostics --> task_20_update_ssot_wave_delivery
  task_04_implement_delivery_diagnostics --> task_21_update_plan_breakdown_skill
  task_05_author_tests_wave_scoped_interlock --> task_06_implement_wave_scoped_interlock
  task_06_implement_wave_scoped_interlock --> task_07_author_tests_deliver_at_wave_barrier
  task_06_implement_wave_scoped_interlock --> task_20_update_ssot_wave_delivery
  task_07_author_tests_deliver_at_wave_barrier --> task_08_implement_deliver_at_wave_barrier
  task_08_implement_deliver_at_wave_barrier --> task_28_author_tests_wave_delivery_wiring
  task_09_author_tests_wave_delivered_journal --> task_10_implement_wave_delivered_journal
  task_09_author_tests_wave_delivered_journal --> task_11_author_tests_wave_delivered_event
  task_10_implement_wave_delivered_journal --> task_12_implement_wave_delivered_event_core
  task_10_implement_wave_delivered_journal --> task_20_update_ssot_wave_delivery
  task_10_implement_wave_delivered_journal --> task_24_author_tests_refresh_provenance
  task_10_implement_wave_delivered_journal --> task_28_author_tests_wave_delivery_wiring
  task_11_author_tests_wave_delivered_event --> task_12_implement_wave_delivered_event_core
  task_11_author_tests_wave_delivered_event --> task_13_implement_wave_delivered_event_cli
  task_11_author_tests_wave_delivered_event --> task_28_author_tests_wave_delivery_wiring
  task_12_implement_wave_delivered_event_core --> task_13_implement_wave_delivered_event_cli
  task_13_implement_wave_delivered_event_cli --> task_20_update_ssot_wave_delivery
  task_14_author_tests_post_delivery_refresh --> task_15_implement_post_delivery_refresh
  task_15_implement_post_delivery_refresh --> task_17_implement_branchmoved_halt
  task_15_implement_post_delivery_refresh --> task_20_update_ssot_wave_delivery
  task_16_author_tests_branchmoved_halt --> task_17_implement_branchmoved_halt
  task_16_author_tests_branchmoved_halt --> task_18_author_tests_partial_delivery_report
  task_17_implement_branchmoved_halt --> task_20_update_ssot_wave_delivery
  task_18_author_tests_partial_delivery_report --> task_19_implement_partial_delivery_report
  task_19_implement_partial_delivery_report --> task_20_update_ssot_wave_delivery
  task_20_update_ssot_wave_delivery --> task_26_update_domain_knowledge_skill
  task_20_update_ssot_wave_delivery --> task_27_update_readme_wave_delivery
  task_24_author_tests_refresh_provenance --> task_25_implement_refresh_provenance
  task_25_implement_refresh_provenance --> task_14_author_tests_post_delivery_refresh
  task_25_implement_refresh_provenance --> task_18_author_tests_partial_delivery_report
  task_25_implement_refresh_provenance --> task_20_update_ssot_wave_delivery
  task_28_author_tests_wave_delivery_wiring --> task_29_implement_wave_delivery_wiring
  task_29_implement_wave_delivery_wiring --> task_14_author_tests_post_delivery_refresh
  task_29_implement_wave_delivery_wiring --> task_16_author_tests_branchmoved_halt
  task_29_implement_wave_delivery_wiring --> task_18_author_tests_partial_delivery_report
  task_30_author_tests_trial_delivery_primitive --> task_31_implement_trial_delivery_primitive
  task_31_implement_trial_delivery_primitive --> task_07_author_tests_deliver_at_wave_barrier
  task_21_update_plan_breakdown_skill --> plan_guardrails
  task_26_update_domain_knowledge_skill --> plan_guardrails
  task_27_update_readme_wave_delivery --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
