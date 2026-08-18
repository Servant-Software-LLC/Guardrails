<!-- guardrails:graph v1 source-sha256=17dfce2ac8e81b9c1bb5069800f2b5b600dc4a0d0e9760e86a018bda1a157149 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave-01-artifacts-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema["wave-02-attempt-launch-wiring/01-author-tests-journal-tiering-schema"]
    task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema_gr_0["01-build-passes"]:::guardrail
    task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema["wave-02-attempt-launch-wiring/02-implement-journal-tiering-schema"]
    task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema_gr_0["01-journal-schema-tests-pass"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_02_implement_journal_tiering_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification["wave-02-attempt-launch-wiring/03-author-tests-unavailability-classification"]
    task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification_gr_0["01-build-passes"]:::guardrail
    task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification["wave-02-attempt-launch-wiring/04-implement-unavailability-classification"]
    task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification_gr_0["01-no-new-failure-kind-member"]:::guardrail
    task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification_gr_1["02-unavailability-tests-pass"]:::guardrail
    task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification_gr_2["03-answer-recorded-in-state"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_04_implement_unavailability_classification fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_05_build_conformance_harness["wave-02-attempt-launch-wiring/05-build-conformance-harness"]
    task_wave_02_attempt_launch_wiring_05_build_conformance_harness_gr_0["01-harness-shape"]:::guardrail
    task_wave_02_attempt_launch_wiring_05_build_conformance_harness_gr_1["02-build-passes"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_05_build_conformance_harness fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance["wave-02-attempt-launch-wiring/06-author-tests-stage2-conformance"]
    task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance_gr_0["01-covers-required-behaviors"]:::guardrail
    task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance_gr_1["02-build-passes"]:::guardrail
    task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance_gr_2["03-tests-fail-on-current-code"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_06_author_tests_stage2_conformance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch["wave-02-attempt-launch-wiring/07-wire-resolution-into-attempt-launch"]
    task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch_gr_0["01-resolver-called-at-attempt-launch"]:::guardrail
    task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch_gr_1["02-two-level-precedence-coverage-survives"]:::guardrail
    task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch_gr_2["03-conformance-wiring-tests-pass"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_07_wire_resolution_into_attempt_launch fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human["wave-02-attempt-launch-wiring/08-settle-no-route-as-needs-human"]
    task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human_gr_0["01-no-route-settled-not-faked"]:::guardrail
    task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human_gr_1["02-no-route-and-wiring-tests-pass"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_08_settle_no_route_as_needs_human fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings["wave-02-attempt-launch-wiring/09-disclose-resolved-route-and-warnings"]
    task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings_gr_0["01-ceiling-datum-read-not-rederived"]:::guardrail
    task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings_gr_1["02-full-conformance-suite-passes"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_09_disclose_resolved_route_and_warnings fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend["wave-02-attempt-launch-wiring/10-author-tests-per-tier-spend"]
    task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend_gr_0["01-build-passes"]:::guardrail
    task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend_gr_2["03-covers-invariant7-suppression"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_10_author_tests_per_tier_spend fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend["wave-02-attempt-launch-wiring/11-implement-per-tier-spend"]
    task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend_gr_0["01-cli-suppression-guarded"]:::guardrail
    task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend_gr_1["02-per-tier-spend-tests-pass"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_11_implement_per_tier_spend fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens["wave-02-attempt-launch-wiring/12-author-tests-attempt-usage-tokens"]
    task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens_gr_0["01-build-passes"]:::guardrail
    task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens_gr_2["03-covers-cache-token-total"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_12_author_tests_attempt_usage_tokens fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens["wave-02-attempt-launch-wiring/13-implement-attempt-usage-tokens"]
    task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens_gr_0["01-both-hops-landed"]:::guardrail
    task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens_gr_1["02-usage-tokens-tests-pass"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_13_implement_attempt_usage_tokens fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas["wave-02-attempt-launch-wiring/14-land-ssot-schema-deltas"]
    task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas_gr_0["01-ssot-deltas-landed"]:::guardrail
  end
  style task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-wave-union-builds"]:::guardrail
    plan_guardrails_1["02-stage2-conformance-green"]:::guardrail
    plan_guardrails_2["03-wave2-unit-suites-green"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_02_attempt_launch_wiring_01_author_tests_journal_tiering_schema
  plan_preflights --> task_wave_02_attempt_launch_wiring_03_author_tests_unavailability_classification
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
  task_wave_02_attempt_launch_wiring_14_land_ssot_schema_deltas --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
