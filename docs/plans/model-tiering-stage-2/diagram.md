<!-- guardrails:graph v1 source-sha256=227253a7ed38d8d971d678c4bc1d1de0e9b42aaba4099de96426ff1ce9dff5fa -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1_preflights["Wave 1 Entry Gate"]
  end
  style wave_1_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1["Wave 1 — resolver-core"]
    subgraph task_wave_01_resolver_core_01_author_tests_candidate_selection["01-author-tests-candidate-selection"]
      task_wave_01_resolver_core_01_author_tests_candidate_selection_gr_0["01-build-passes"]:::guardrail
      task_wave_01_resolver_core_01_author_tests_candidate_selection_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_01_resolver_core_01_author_tests_candidate_selection_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_01_resolver_core_01_author_tests_candidate_selection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_resolver_core_02_implement_candidate_selection["02-implement-candidate-selection"]
      task_wave_01_resolver_core_02_implement_candidate_selection_gr_0["01-selection-tests-pass"]:::guardrail
    end
    style task_wave_01_resolver_core_02_implement_candidate_selection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_resolver_core_03_author_tests_resolution_precedence["03-author-tests-resolution-precedence"]
      task_wave_01_resolver_core_03_author_tests_resolution_precedence_gr_0["01-build-passes"]:::guardrail
      task_wave_01_resolver_core_03_author_tests_resolution_precedence_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_01_resolver_core_03_author_tests_resolution_precedence_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_01_resolver_core_03_author_tests_resolution_precedence fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_resolver_core_04_implement_resolution_precedence["04-implement-resolution-precedence"]
      task_wave_01_resolver_core_04_implement_resolution_precedence_gr_0["01-precedence-tests-pass"]:::guardrail
    end
    style task_wave_01_resolver_core_04_implement_resolution_precedence fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_resolver_core_05_author_tests_tier_provenance["05-author-tests-tier-provenance"]
      task_wave_01_resolver_core_05_author_tests_tier_provenance_gr_0["01-build-passes"]:::guardrail
      task_wave_01_resolver_core_05_author_tests_tier_provenance_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_01_resolver_core_05_author_tests_tier_provenance_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_01_resolver_core_05_author_tests_tier_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_resolver_core_06_implement_tier_provenance["06-implement-tier-provenance"]
      task_wave_01_resolver_core_06_implement_tier_provenance_gr_0["01-provenance-tests-pass"]:::guardrail
    end
    style task_wave_01_resolver_core_06_implement_tier_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_1 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_1_guardrails["Wave 1 Exit Gate"]
    wave_1_guardrails_0["01-resolver-core-complete"]:::guardrail
    wave_1_guardrails_1["02-wave-union-builds"]:::guardrail
  end
  style wave_1_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2_preflights["Wave 2 Entry Gate"]
    wave_2_preflights_0["01-wave-01-artifacts-materialized"]:::preflight
  end
  style wave_2_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2["Wave 2 — attempt-launch-wiring"]
    subgraph task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema["01-author-tests-journal-tiering-schema"]
      task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema_gr_0["01-build-passes"]:::guardrail
      task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema["02-implement-journal-tiering-schema"]
      task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema_gr_0["01-journal-schema-tests-pass"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification["03-author-tests-unavailability-classification"]
      task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification_gr_0["01-build-passes"]:::guardrail
      task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification["04-implement-unavailability-classification"]
      task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification_gr_0["01-no-new-failure-kind-member"]:::guardrail
      task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification_gr_1["02-unavailability-tests-pass"]:::guardrail
      task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification_gr_2["03-answer-recorded-in-state"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_05_build_conformance_harness["05-build-conformance-harness"]
      task_wave_02_attempt_launch_wiring_05_build_conformance_harness_gr_0["01-harness-shape"]:::guardrail
      task_wave_02_attempt_launch_wiring_05_build_conformance_harness_gr_1["02-build-passes"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_05_build_conformance_harness fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance["06-author-tests-stage2-conformance"]
      task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance_gr_0["01-covers-required-behaviors"]:::guardrail
      task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance_gr_1["02-build-passes"]:::guardrail
      task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance_gr_2["03-tests-fail-on-current-code"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch["07-wire-resolution-into-attempt-launch"]
      task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch_gr_0["01-resolver-called-at-attempt-launch"]:::guardrail
      task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch_gr_1["02-two-level-precedence-coverage-survives"]:::guardrail
      task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch_gr_2["03-conformance-wiring-tests-pass"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human["08-settle-no-route-as-needs-human"]
      task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human_gr_0["01-no-route-settled-not-faked"]:::guardrail
      task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human_gr_1["02-no-route-and-wiring-tests-pass"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings["09-disclose-resolved-route-and-warnings"]
      task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings_gr_0["01-ceiling-datum-read-not-rederived"]:::guardrail
      task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings_gr_1["02-full-conformance-suite-passes"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend["10-author-tests-per-tier-spend"]
      task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend_gr_0["01-build-passes"]:::guardrail
      task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend_gr_2["03-covers-invariant7-suppression"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend["11-implement-per-tier-spend"]
      task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend_gr_0["01-cli-suppression-guarded"]:::guardrail
      task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend_gr_1["02-per-tier-spend-tests-pass"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens["12-author-tests-attempt-usage-tokens"]
      task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens_gr_0["01-build-passes"]:::guardrail
      task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens_gr_2["03-covers-cache-token-total"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens["13-implement-attempt-usage-tokens"]
      task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens_gr_0["01-both-hops-landed"]:::guardrail
      task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens_gr_1["02-usage-tokens-tests-pass"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas["14-land-ssot-schema-deltas"]
      task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas_gr_0["01-ssot-deltas-landed"]:::guardrail
    end
    style task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_2 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_2_guardrails["Wave 2 Exit Gate"]
    wave_2_guardrails_0["01-wave-union-builds"]:::guardrail
    wave_2_guardrails_1["02-stage2-conformance-green"]:::guardrail
    wave_2_guardrails_2["03-wave2-unit-suites-green"]:::guardrail
  end
  style wave_2_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-dor-section-6-contract-landed"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> wave_1_preflights
  wave_1_preflights --> task_wave_01_resolver_core_01_author_tests_candidate_selection
  wave_1_preflights --> task_wave_01_resolver_core_05_author_tests_tier_provenance
  task_wave_01_resolver_core_01_author_tests_candidate_selection --> task_wave_01_resolver_core_02_implement_candidate_selection
  task_wave_01_resolver_core_02_implement_candidate_selection --> task_wave_01_resolver_core_03_author_tests_resolution_precedence
  task_wave_01_resolver_core_03_author_tests_resolution_precedence --> task_wave_01_resolver_core_04_implement_resolution_precedence
  task_wave_01_resolver_core_05_author_tests_tier_provenance --> task_wave_01_resolver_core_06_implement_tier_provenance
  task_wave_01_resolver_core_04_implement_resolution_precedence --> wave_1_guardrails
  task_wave_01_resolver_core_06_implement_tier_provenance --> wave_1_guardrails
  wave_2_preflights --> task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema
  wave_2_preflights --> task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification
  task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema --> task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema
  task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema --> task_wave_02_attempt_launch_wiring_05_build_conformance_harness
  task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema --> task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend
  task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema --> task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens
  task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification --> task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification
  task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification --> task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas
  task_wave_02_attempt_launch_wiring_05_build_conformance_harness --> task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance
  task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance --> task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch
  task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch --> task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human
  task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human --> task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings
  task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings --> task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas
  task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend --> task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend
  task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend --> task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas
  task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens --> task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens
  task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens --> task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas
  task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas --> wave_2_guardrails
  wave_1_guardrails -.->|"🔒 wave barrier"| wave_2_preflights
  wave_2_guardrails --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
