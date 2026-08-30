<!-- guardrails:graph v1 source-sha256=ba8361a9d863a30c3c882cd0154bbf8464864355b78c51bd04b72bbc215aae44 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_00_land_the_required_role_seam["00-land-the-required-role-seam"]
    task_00_land_the_required_role_seam_gr_0["01-build-passes"]:::guardrail
    task_00_land_the_required_role_seam_gr_1["02-role-is-required-not-defaulted"]:::guardrail
    task_00_land_the_required_role_seam_gr_2["03-every-fixture-still-sets-role"]:::guardrail
    task_00_land_the_required_role_seam_gr_3["04-src-sites-are-the-uniform-action-stub"]:::guardrail
  end
  style task_00_land_the_required_role_seam fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_01_author_tests_role_seam["01-author-tests-role-seam"]
    task_01_author_tests_role_seam_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_role_seam_gr_1["02-seven-sites-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_role_seam fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_assign_roles_at_seven_sites["02-assign-roles-at-seven-sites"]
    task_02_assign_roles_at_seven_sites_gr_0["01-build-passes"]:::guardrail
    task_02_assign_roles_at_seven_sites_gr_1["02-role-seam-tests-pass"]:::guardrail
  end
  style task_02_assign_roles_at_seven_sites fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_prove_role_reaches_real_runner["03-prove-role-reaches-real-runner"]
    task_03_prove_role_reaches_real_runner_gr_0["01-build-passes"]:::guardrail
    task_03_prove_role_reaches_real_runner_gr_1["02-role-reaches-the-real-runner"]:::guardrail
  end
  style task_03_prove_role_reaches_real_runner fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_author_tests_json_extractor["04-author-tests-json-extractor"]
    task_04_author_tests_json_extractor_gr_0["01-build-passes"]:::guardrail
    task_04_author_tests_json_extractor_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_04_author_tests_json_extractor fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_implement_shared_json_extractor["05-implement-shared-json-extractor"]
    task_05_implement_shared_json_extractor_gr_0["01-build-passes"]:::guardrail
    task_05_implement_shared_json_extractor_gr_1["02-extractor-tests-pass"]:::guardrail
  end
  style task_05_implement_shared_json_extractor fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_build_fake_openai_server["06-build-fake-openai-server"]
    task_06_build_fake_openai_server_gr_0["01-build-passes"]:::guardrail
    task_06_build_fake_openai_server_gr_1["02-fake-server-self-test-census"]:::guardrail
  end
  style task_06_build_fake_openai_server fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_tool_containment["07-author-tests-tool-containment"]
    task_07_author_tests_tool_containment_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_tool_containment_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_07_author_tests_tool_containment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_implement_tool_containment["08-implement-tool-containment"]
    task_08_implement_tool_containment_gr_0["01-build-passes"]:::guardrail
    task_08_implement_tool_containment_gr_1["02-containment-tests-pass"]:::guardrail
  end
  style task_08_implement_tool_containment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_add_openai_block_config_surface["09-add-openai-block-config-surface"]
    task_09_add_openai_block_config_surface_gr_0["01-build-passes"]:::guardrail
    task_09_add_openai_block_config_surface_gr_1["02-config-shape-tests-pass"]:::guardrail
    task_09_add_openai_block_config_surface_gr_2["03-runner-config-neighbours-still-pass"]:::guardrail
  end
  style task_09_add_openai_block_config_surface fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_author_tests_runner_transport["10-author-tests-runner-transport"]
    task_10_author_tests_runner_transport_gr_0["01-build-passes"]:::guardrail
    task_10_author_tests_runner_transport_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_10_author_tests_runner_transport fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_implement_runner_transport["11-implement-runner-transport"]
    task_11_implement_runner_transport_gr_0["01-build-passes"]:::guardrail
    task_11_implement_runner_transport_gr_1["02-transport-tests-pass"]:::guardrail
  end
  style task_11_implement_runner_transport fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_author_tests_runner_tool_loop["12-author-tests-runner-tool-loop"]
    task_12_author_tests_runner_tool_loop_gr_0["01-build-passes"]:::guardrail
    task_12_author_tests_runner_tool_loop_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_12_author_tests_runner_tool_loop fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_implement_runner_tool_loop["13-implement-runner-tool-loop"]
    task_13_implement_runner_tool_loop_gr_0["01-build-passes"]:::guardrail
    task_13_implement_runner_tool_loop_gr_1["02-tool-loop-tests-pass"]:::guardrail
  end
  style task_13_implement_runner_tool_loop fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_author_tests_runner_verdict["14-author-tests-runner-verdict"]
    task_14_author_tests_runner_verdict_gr_0["01-build-passes"]:::guardrail
    task_14_author_tests_runner_verdict_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_14_author_tests_runner_verdict fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_implement_runner_verdict_roles["15-implement-runner-verdict-roles"]
    task_15_implement_runner_verdict_roles_gr_0["01-build-passes"]:::guardrail
    task_15_implement_runner_verdict_roles_gr_1["02-verdict-tests-pass"]:::guardrail
    task_15_implement_runner_verdict_roles_gr_2["03-runner-neighbours-still-pass"]:::guardrail
  end
  style task_15_implement_runner_verdict_roles fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_16_author_tests_kind_aware_harness["16-author-tests-kind-aware-harness"]
    task_16_author_tests_kind_aware_harness_gr_0["01-build-passes"]:::guardrail
    task_16_author_tests_kind_aware_harness_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_16_author_tests_kind_aware_harness fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_17_implement_kind_aware_harness["17-implement-kind-aware-harness"]
    task_17_implement_kind_aware_harness_gr_0["01-build-passes"]:::guardrail
    task_17_implement_kind_aware_harness_gr_1["02-kind-aware-tests-pass"]:::guardrail
    task_17_implement_kind_aware_harness_gr_2["03-composer-neighbours-still-pass"]:::guardrail
  end
  style task_17_implement_kind_aware_harness fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_18_author_tests_block_diagnostics["18-author-tests-block-diagnostics"]
    task_18_author_tests_block_diagnostics_gr_0["01-build-passes"]:::guardrail
    task_18_author_tests_block_diagnostics_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_18_author_tests_block_diagnostics fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_19_implement_block_diagnostics["19-implement-block-diagnostics"]
    task_19_implement_block_diagnostics_gr_0["01-build-passes"]:::guardrail
    task_19_implement_block_diagnostics_gr_1["02-diagnostics-tests-pass"]:::guardrail
    task_19_implement_block_diagnostics_gr_2["03-validator-neighbours-still-pass"]:::guardrail
  end
  style task_19_implement_block_diagnostics fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_20_author_tests_reachability_gate["20-author-tests-reachability-gate"]
    task_20_author_tests_reachability_gate_gr_0["01-build-passes"]:::guardrail
    task_20_author_tests_reachability_gate_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_20_author_tests_reachability_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_21_implement_reachability_gate["21-implement-reachability-gate"]
    task_21_implement_reachability_gate_gr_0["01-build-passes"]:::guardrail
    task_21_implement_reachability_gate_gr_1["02-reachability-tests-pass"]:::guardrail
    task_21_implement_reachability_gate_gr_2["03-loader-neighbours-still-pass"]:::guardrail
  end
  style task_21_implement_reachability_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_22_author_tests_endpoint_preflight["22-author-tests-endpoint-preflight"]
    task_22_author_tests_endpoint_preflight_gr_0["01-build-passes"]:::guardrail
    task_22_author_tests_endpoint_preflight_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_22_author_tests_endpoint_preflight fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_23_implement_endpoint_preflight["23-implement-endpoint-preflight"]
    task_23_implement_endpoint_preflight_gr_0["01-build-passes"]:::guardrail
    task_23_implement_endpoint_preflight_gr_1["02-preflight-tests-pass"]:::guardrail
    task_23_implement_endpoint_preflight_gr_2["03-providers-init-pin-rebaselined"]:::guardrail
  end
  style task_23_implement_endpoint_preflight fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_24_author_tests_providers_check["24-author-tests-providers-check"]
    task_24_author_tests_providers_check_gr_0["01-build-passes"]:::guardrail
    task_24_author_tests_providers_check_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_24_author_tests_providers_check fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_25_implement_providers_check["25-implement-providers-check"]
    task_25_implement_providers_check_gr_0["01-build-passes"]:::guardrail
    task_25_implement_providers_check_gr_1["02-providers-check-tests-pass"]:::guardrail
  end
  style task_25_implement_providers_check fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_26_author_tests_judge_spend["26-author-tests-judge-spend"]
    task_26_author_tests_judge_spend_gr_0["01-build-passes"]:::guardrail
    task_26_author_tests_judge_spend_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_26_author_tests_judge_spend fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_27_implement_judge_spend["27-implement-judge-spend"]
    task_27_implement_judge_spend_gr_0["01-build-passes"]:::guardrail
    task_27_implement_judge_spend_gr_1["02-judge-spend-tests-pass"]:::guardrail
    task_27_implement_judge_spend_gr_2["03-journal-neighbours-still-pass"]:::guardrail
  end
  style task_27_implement_judge_spend fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_28_record_openai_compat_in_ssot["28-record-openai-compat-in-ssot"]
    task_28_record_openai_compat_in_ssot_gr_0["01-canonical-block-carries-the-keys"]:::guardrail
    task_28_record_openai_compat_in_ssot_gr_1["02-schema-drift-test-passes"]:::guardrail
    task_28_record_openai_compat_in_ssot_gr_2["03-ssot-records-the-contract"]:::guardrail
  end
  style task_28_record_openai_compat_in_ssot fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_29_mirror_canonical_block_in_schemas["29-mirror-canonical-block-in-schemas"]
    task_29_mirror_canonical_block_in_schemas_gr_0["01-schema-drift-test-passes"]:::guardrail
  end
  style task_29_mirror_canonical_block_in_schemas fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_30_record_in_domain_knowledge["30-record-in-domain-knowledge"]
    task_30_record_in_domain_knowledge_gr_0["01-domain-knowledge-records-runner"]:::guardrail
  end
  style task_30_record_in_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-suite-passes"]:::guardrail
    plan_guardrails_2["03-integration-suite-passes"]:::guardrail
    plan_guardrails_3["04-union-artifacts-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_00_land_the_required_role_seam
  plan_preflights --> task_04_author_tests_json_extractor
  plan_preflights --> task_06_build_fake_openai_server
  plan_preflights --> task_07_author_tests_tool_containment
  plan_preflights --> task_09_add_openai_block_config_surface
  task_00_land_the_required_role_seam --> task_01_author_tests_role_seam
  task_01_author_tests_role_seam --> task_02_assign_roles_at_seven_sites
  task_02_assign_roles_at_seven_sites --> task_03_prove_role_reaches_real_runner
  task_02_assign_roles_at_seven_sites --> task_05_implement_shared_json_extractor
  task_02_assign_roles_at_seven_sites --> task_10_author_tests_runner_transport
  task_04_author_tests_json_extractor --> task_05_implement_shared_json_extractor
  task_05_implement_shared_json_extractor --> task_14_author_tests_runner_verdict
  task_06_build_fake_openai_server --> task_10_author_tests_runner_transport
  task_06_build_fake_openai_server --> task_22_author_tests_endpoint_preflight
  task_07_author_tests_tool_containment --> task_08_implement_tool_containment
  task_08_implement_tool_containment --> task_12_author_tests_runner_tool_loop
  task_09_add_openai_block_config_surface --> task_10_author_tests_runner_transport
  task_09_add_openai_block_config_surface --> task_18_author_tests_block_diagnostics
  task_10_author_tests_runner_transport --> task_11_implement_runner_transport
  task_11_implement_runner_transport --> task_12_author_tests_runner_tool_loop
  task_12_author_tests_runner_tool_loop --> task_13_implement_runner_tool_loop
  task_13_implement_runner_tool_loop --> task_14_author_tests_runner_verdict
  task_14_author_tests_runner_verdict --> task_15_implement_runner_verdict_roles
  task_15_implement_runner_verdict_roles --> task_16_author_tests_kind_aware_harness
  task_15_implement_runner_verdict_roles --> task_22_author_tests_endpoint_preflight
  task_16_author_tests_kind_aware_harness --> task_17_implement_kind_aware_harness
  task_17_implement_kind_aware_harness --> task_26_author_tests_judge_spend
  task_17_implement_kind_aware_harness --> task_28_record_openai_compat_in_ssot
  task_18_author_tests_block_diagnostics --> task_19_implement_block_diagnostics
  task_19_implement_block_diagnostics --> task_20_author_tests_reachability_gate
  task_20_author_tests_reachability_gate --> task_21_implement_reachability_gate
  task_21_implement_reachability_gate --> task_28_record_openai_compat_in_ssot
  task_22_author_tests_endpoint_preflight --> task_23_implement_endpoint_preflight
  task_23_implement_endpoint_preflight --> task_24_author_tests_providers_check
  task_23_implement_endpoint_preflight --> task_28_record_openai_compat_in_ssot
  task_24_author_tests_providers_check --> task_25_implement_providers_check
  task_25_implement_providers_check --> task_28_record_openai_compat_in_ssot
  task_26_author_tests_judge_spend --> task_27_implement_judge_spend
  task_27_implement_judge_spend --> task_28_record_openai_compat_in_ssot
  task_28_record_openai_compat_in_ssot --> task_29_mirror_canonical_block_in_schemas
  task_28_record_openai_compat_in_ssot --> task_30_record_in_domain_knowledge
  task_03_prove_role_reaches_real_runner --> plan_guardrails
  task_29_mirror_canonical_block_in_schemas --> plan_guardrails
  task_30_record_in_domain_knowledge --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
