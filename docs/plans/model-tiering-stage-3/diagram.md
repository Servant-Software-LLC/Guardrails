<!-- guardrails:graph v1 source-sha256=57531827097e1f90345ad7d319c9731f7be55184e007a93a30d6cd35354f8c5c -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-toolchain-current"]:::preflight
    plan_preflights_1["02-baseline-core-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1_preflights["Wave 1 Entry Gate"]
  end
  style wave_1_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1["Wave 1 — config-net"]
    subgraph task_wave_01_config_net_01_allocate_diagnostic_codes["01-allocate-diagnostic-codes"]
      task_wave_01_config_net_01_allocate_diagnostic_codes_gr_0["01-build-passes"]:::guardrail
      task_wave_01_config_net_01_allocate_diagnostic_codes_gr_1["02-codes-allocated"]:::guardrail
    end
    style task_wave_01_config_net_01_allocate_diagnostic_codes fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_02_author_tests_registry_warnings["02-author-tests-registry-warnings"]
      task_wave_01_config_net_02_author_tests_registry_warnings_gr_0["01-build-passes"]:::guardrail
      task_wave_01_config_net_02_author_tests_registry_warnings_gr_1["02-tests-fail-on-current-code"]:::guardrail
    end
    style task_wave_01_config_net_02_author_tests_registry_warnings fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_03_implement_registry_warnings["03-implement-registry-warnings"]
      task_wave_01_config_net_03_implement_registry_warnings_gr_0["01-tests-pass"]:::guardrail
    end
    style task_wave_01_config_net_03_implement_registry_warnings fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_04_author_tests_pin_and_tier_coexist["04-author-tests-pin-and-tier-coexist"]
      task_wave_01_config_net_04_author_tests_pin_and_tier_coexist_gr_0["01-build-passes"]:::guardrail
      task_wave_01_config_net_04_author_tests_pin_and_tier_coexist_gr_1["02-tests-fail-on-current-code"]:::guardrail
    end
    style task_wave_01_config_net_04_author_tests_pin_and_tier_coexist fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_05_implement_pin_and_tier_coexist["05-implement-pin-and-tier-coexist"]
      task_wave_01_config_net_05_implement_pin_and_tier_coexist_gr_0["01-tests-pass"]:::guardrail
    end
    style task_wave_01_config_net_05_implement_pin_and_tier_coexist fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_config_net_06_update_ssot_and_domain_knowledge["06-update-ssot-and-domain-knowledge"]
      task_wave_01_config_net_06_update_ssot_and_domain_knowledge_gr_0["01-docs-record-the-codes"]:::guardrail
      task_wave_01_config_net_06_update_ssot_and_domain_knowledge_gr_1["02-schema-drift-tests-pass"]:::guardrail
    end
    style task_wave_01_config_net_06_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_1 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_1_guardrails["Wave 1 Exit Gate"]
    wave_1_guardrails_0["01-solution-builds"]:::guardrail
    wave_1_guardrails_1["02-core-tests-pass"]:::guardrail
    wave_1_guardrails_2["03-wave-deliverables-present"]:::guardrail
  end
  style wave_1_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2_preflights["Wave 2 Entry Gate"]
    wave_2_preflights_0["01-stage2-anchors-materialized"]:::preflight
  end
  style wave_2_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2["Wave 2 — capture-and-persist"]
    subgraph task_wave_02_capture_and_persist_01_author_tests_observed_model_capture["01-author-tests-observed-model-capture"]
      task_wave_02_capture_and_persist_01_author_tests_observed_model_capture_gr_0["01-tests-build"]:::guardrail
      task_wave_02_capture_and_persist_01_author_tests_observed_model_capture_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_02_capture_and_persist_01_author_tests_observed_model_capture fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_capture_and_persist_02_implement_observed_model_capture["02-implement-observed-model-capture"]
      task_wave_02_capture_and_persist_02_implement_observed_model_capture_gr_0["01-capture-tests-pass"]:::guardrail
    end
    style task_wave_02_capture_and_persist_02_implement_observed_model_capture fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_capture_and_persist_03_author_tests_provenance_model_persist["03-author-tests-provenance-model-persist"]
      task_wave_02_capture_and_persist_03_author_tests_provenance_model_persist_gr_0["01-tests-build"]:::guardrail
      task_wave_02_capture_and_persist_03_author_tests_provenance_model_persist_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_02_capture_and_persist_03_author_tests_provenance_model_persist fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_capture_and_persist_04_implement_provenance_model_persist["04-implement-provenance-model-persist"]
      task_wave_02_capture_and_persist_04_implement_provenance_model_persist_gr_0["01-provenance-tests-pass"]:::guardrail
    end
    style task_wave_02_capture_and_persist_04_implement_provenance_model_persist fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_capture_and_persist_05_update_ssot_and_domain_knowledge["05-update-ssot-and-domain-knowledge"]
      task_wave_02_capture_and_persist_05_update_ssot_and_domain_knowledge_gr_0["01-contract-delta-present"]:::guardrail
    end
    style task_wave_02_capture_and_persist_05_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_2 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_2_guardrails["Wave 2 Exit Gate"]
    wave_2_guardrails_0["01-solution-builds"]:::guardrail
    wave_2_guardrails_1["02-suites-pass"]:::guardrail
    wave_2_guardrails_2["03-wave-deliverables-present"]:::guardrail
  end
  style wave_2_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3_preflights["Wave 3 Entry Gate"]
    wave_3_preflights_0["01-wave2-surfaces-materialized"]:::preflight
  end
  style wave_3_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3["Wave 3 — operator-surfaces"]
    subgraph task_wave_03_operator_surfaces_01_stub_the_observer_seam["01-stub-the-observer-seam"]
      task_wave_03_operator_surfaces_01_stub_the_observer_seam_gr_0["01-stubs-declared-and-inert"]:::guardrail
    end
    style task_wave_03_operator_surfaces_01_stub_the_observer_seam fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_operator_surfaces_02_author_tests_disclosure["02-author-tests-disclosure"]
      task_wave_03_operator_surfaces_02_author_tests_disclosure_gr_0["01-tests-build"]:::guardrail
      task_wave_03_operator_surfaces_02_author_tests_disclosure_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_03_operator_surfaces_02_author_tests_disclosure fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_operator_surfaces_03_author_tests_rendering["03-author-tests-rendering"]
      task_wave_03_operator_surfaces_03_author_tests_rendering_gr_0["01-tests-build"]:::guardrail
      task_wave_03_operator_surfaces_03_author_tests_rendering_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_03_operator_surfaces_03_author_tests_rendering fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_operator_surfaces_04_author_tests_forwarding["04-author-tests-forwarding"]
      task_wave_03_operator_surfaces_04_author_tests_forwarding_gr_0["01-tests-build"]:::guardrail
      task_wave_03_operator_surfaces_04_author_tests_forwarding_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_03_operator_surfaces_04_author_tests_forwarding fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise["05-implement-route-log-and-observer-raise"]
      task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise_gr_0["01-disclosure-tests-pass"]:::guardrail
      task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise_gr_1["02-consumes-not-rederives"]:::guardrail
    end
    style task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console["06-render-attempt-model-in-live-and-console"]
      task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console_gr_0["01-rendering-tests-pass"]:::guardrail
      task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console_gr_1["02-live-renders-through-the-shared-summary"]:::guardrail
    end
    style task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators["07-forward-attempt-model-in-decorators"]
      task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators_gr_0["01-forwarding-tests-pass"]:::guardrail
    end
    style task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge["08-update-ssot-and-domain-knowledge"]
      task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge_gr_0["01-contract-delta-present"]:::guardrail
    end
    style task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_3 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_3_guardrails["Wave 3 Exit Gate"]
    wave_3_guardrails_0["01-solution-builds"]:::guardrail
    wave_3_guardrails_1["02-suites-pass"]:::guardrail
    wave_3_guardrails_2["03-wave-deliverables-present"]:::guardrail
  end
  style wave_3_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4_preflights["Wave 4 Entry Gate"]
    wave_4_preflights_0["01-wave3-surfaces-materialized"]:::preflight
  end
  style wave_4_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4["Wave 4 — report-and-cleanup"]
    subgraph task_wave_04_report_and_cleanup_01_author_tests_models_used_report["01-author-tests-models-used-report"]
      task_wave_04_report_and_cleanup_01_author_tests_models_used_report_gr_0["01-tests-build"]:::guardrail
      task_wave_04_report_and_cleanup_01_author_tests_models_used_report_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_04_report_and_cleanup_01_author_tests_models_used_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_report_and_cleanup_02_implement_models_used_report["02-implement-models-used-report"]
      task_wave_04_report_and_cleanup_02_implement_models_used_report_gr_0["01-models-used-tests-pass"]:::guardrail
    end
    style task_wave_04_report_and_cleanup_02_implement_models_used_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder["03-delete-superseded-plan-folder"]
      task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder_gr_0["01-folder-gone"]:::guardrail
    end
    style task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge["04-update-ssot-and-domain-knowledge"]
      task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge_gr_0["01-contract-delta-present"]:::guardrail
    end
    style task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_4 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_4_guardrails["Wave 4 Exit Gate"]
    wave_4_guardrails_0["01-solution-builds"]:::guardrail
    wave_4_guardrails_1["02-suites-pass"]:::guardrail
    wave_4_guardrails_2["03-wave-deliverables-present"]:::guardrail
  end
  style wave_4_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_5_preflights["Wave 5 Entry Gate"]
  end
  style wave_5_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_5["Wave 5 — review-net"]
    wave_5_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_5_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_5 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_5_guardrails["Wave 5 Exit Gate"]
  end
  style wave_5_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph plan_guardrails["Terminal Gate"]
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> wave_1_preflights
  wave_1_preflights --> task_wave_01_config_net_01_allocate_diagnostic_codes
  task_wave_01_config_net_01_allocate_diagnostic_codes --> task_wave_01_config_net_02_author_tests_registry_warnings
  task_wave_01_config_net_01_allocate_diagnostic_codes --> task_wave_01_config_net_04_author_tests_pin_and_tier_coexist
  task_wave_01_config_net_02_author_tests_registry_warnings --> task_wave_01_config_net_03_implement_registry_warnings
  task_wave_01_config_net_03_implement_registry_warnings --> task_wave_01_config_net_05_implement_pin_and_tier_coexist
  task_wave_01_config_net_04_author_tests_pin_and_tier_coexist --> task_wave_01_config_net_05_implement_pin_and_tier_coexist
  task_wave_01_config_net_05_implement_pin_and_tier_coexist --> task_wave_01_config_net_06_update_ssot_and_domain_knowledge
  task_wave_01_config_net_06_update_ssot_and_domain_knowledge --> wave_1_guardrails
  wave_2_preflights --> task_wave_02_capture_and_persist_01_author_tests_observed_model_capture
  task_wave_02_capture_and_persist_01_author_tests_observed_model_capture --> task_wave_02_capture_and_persist_02_implement_observed_model_capture
  task_wave_02_capture_and_persist_01_author_tests_observed_model_capture --> task_wave_02_capture_and_persist_03_author_tests_provenance_model_persist
  task_wave_02_capture_and_persist_02_implement_observed_model_capture --> task_wave_02_capture_and_persist_05_update_ssot_and_domain_knowledge
  task_wave_02_capture_and_persist_03_author_tests_provenance_model_persist --> task_wave_02_capture_and_persist_04_implement_provenance_model_persist
  task_wave_02_capture_and_persist_04_implement_provenance_model_persist --> task_wave_02_capture_and_persist_05_update_ssot_and_domain_knowledge
  task_wave_02_capture_and_persist_05_update_ssot_and_domain_knowledge --> wave_2_guardrails
  wave_3_preflights --> task_wave_03_operator_surfaces_01_stub_the_observer_seam
  task_wave_03_operator_surfaces_01_stub_the_observer_seam --> task_wave_03_operator_surfaces_02_author_tests_disclosure
  task_wave_03_operator_surfaces_01_stub_the_observer_seam --> task_wave_03_operator_surfaces_03_author_tests_rendering
  task_wave_03_operator_surfaces_01_stub_the_observer_seam --> task_wave_03_operator_surfaces_04_author_tests_forwarding
  task_wave_03_operator_surfaces_02_author_tests_disclosure --> task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise
  task_wave_03_operator_surfaces_03_author_tests_rendering --> task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console
  task_wave_03_operator_surfaces_04_author_tests_forwarding --> task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators
  task_wave_03_operator_surfaces_05_implement_route_log_and_observer_raise --> task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge
  task_wave_03_operator_surfaces_06_render_attempt_model_in_live_and_console --> task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge
  task_wave_03_operator_surfaces_07_forward_attempt_model_in_decorators --> task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge
  task_wave_03_operator_surfaces_08_update_ssot_and_domain_knowledge --> wave_3_guardrails
  wave_4_preflights --> task_wave_04_report_and_cleanup_01_author_tests_models_used_report
  wave_4_preflights --> task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder
  task_wave_04_report_and_cleanup_01_author_tests_models_used_report --> task_wave_04_report_and_cleanup_02_implement_models_used_report
  task_wave_04_report_and_cleanup_02_implement_models_used_report --> task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge
  task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder --> task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge
  task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge --> wave_4_guardrails
  wave_5_preflights --> wave_5_stub
  wave_5_stub --> wave_5_guardrails
  wave_1_guardrails -.->|"🔒 wave barrier"| wave_2_preflights
  wave_2_guardrails -.->|"🔒 wave barrier"| wave_3_preflights
  wave_3_guardrails -.->|"🔒 wave barrier"| wave_4_preflights
  wave_4_guardrails -.->|"🔒 wave barrier"| wave_5_preflights
  wave_5_guardrails --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
