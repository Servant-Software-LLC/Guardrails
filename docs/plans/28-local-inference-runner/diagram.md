<!-- guardrails:graph v1 source-sha256=70eceae195ecd6278a292b041e7f497b49fe2278071b38fc159712f47ac73430 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
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
  subgraph task_03_author_tests_json_extractor["03-author-tests-json-extractor"]
    task_03_author_tests_json_extractor_gr_0["01-build-passes"]:::guardrail
    task_03_author_tests_json_extractor_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_03_author_tests_json_extractor fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_shared_json_extractor["04-implement-shared-json-extractor"]
    task_04_implement_shared_json_extractor_gr_0["01-build-passes"]:::guardrail
    task_04_implement_shared_json_extractor_gr_1["02-extractor-tests-pass"]:::guardrail
  end
  style task_04_implement_shared_json_extractor fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_build_fake_openai_server["05-build-fake-openai-server"]
    task_05_build_fake_openai_server_gr_0["01-build-passes"]:::guardrail
    task_05_build_fake_openai_server_gr_1["02-fake-server-self-test-passes"]:::guardrail
  end
  style task_05_build_fake_openai_server fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_author_tests_tool_containment["06-author-tests-tool-containment"]
    task_06_author_tests_tool_containment_gr_0["01-build-passes"]:::guardrail
    task_06_author_tests_tool_containment_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_06_author_tests_tool_containment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_implement_tool_containment["07-implement-tool-containment"]
    task_07_implement_tool_containment_gr_0["01-build-passes"]:::guardrail
    task_07_implement_tool_containment_gr_1["02-containment-tests-pass"]:::guardrail
  end
  style task_07_implement_tool_containment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_author_tests_openai_runner["08-author-tests-openai-runner"]
    task_08_author_tests_openai_runner_gr_0["01-build-passes"]:::guardrail
    task_08_author_tests_openai_runner_gr_1["02-transport-tests-fail-on-stubs"]:::guardrail
    task_08_author_tests_openai_runner_gr_2["03-tool-loop-tests-fail-on-stubs"]:::guardrail
    task_08_author_tests_openai_runner_gr_3["04-verdict-tests-fail-on-stubs"]:::guardrail
  end
  style task_08_author_tests_openai_runner fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_implement_runner_transport["09-implement-runner-transport"]
    task_09_implement_runner_transport_gr_0["01-build-passes"]:::guardrail
    task_09_implement_runner_transport_gr_1["02-transport-tests-pass"]:::guardrail
  end
  style task_09_implement_runner_transport fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_implement_runner_tool_loop["10-implement-runner-tool-loop"]
    task_10_implement_runner_tool_loop_gr_0["01-build-passes"]:::guardrail
    task_10_implement_runner_tool_loop_gr_1["02-tool-loop-tests-pass"]:::guardrail
  end
  style task_10_implement_runner_tool_loop fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_implement_runner_verdict_roles["11-implement-runner-verdict-roles"]
    task_11_implement_runner_verdict_roles_gr_0["01-build-passes"]:::guardrail
    task_11_implement_runner_verdict_roles_gr_1["02-verdict-tests-pass"]:::guardrail
    task_11_implement_runner_verdict_roles_gr_2["03-runner-neighbours-still-pass"]:::guardrail
  end
  style task_11_implement_runner_verdict_roles fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_author_tests_kind_aware_harness["12-author-tests-kind-aware-harness"]
    task_12_author_tests_kind_aware_harness_gr_0["01-build-passes"]:::guardrail
    task_12_author_tests_kind_aware_harness_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_12_author_tests_kind_aware_harness fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_implement_kind_aware_harness["13-implement-kind-aware-harness"]
    task_13_implement_kind_aware_harness_gr_0["01-build-passes"]:::guardrail
    task_13_implement_kind_aware_harness_gr_1["02-kind-aware-tests-pass"]:::guardrail
    task_13_implement_kind_aware_harness_gr_2["03-composer-neighbours-still-pass"]:::guardrail
  end
  style task_13_implement_kind_aware_harness fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_author_tests_block_diagnostics["14-author-tests-block-diagnostics"]
    task_14_author_tests_block_diagnostics_gr_0["01-build-passes"]:::guardrail
    task_14_author_tests_block_diagnostics_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_14_author_tests_block_diagnostics fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_implement_block_diagnostics["15-implement-block-diagnostics"]
    task_15_implement_block_diagnostics_gr_0["01-build-passes"]:::guardrail
    task_15_implement_block_diagnostics_gr_1["02-diagnostics-tests-pass"]:::guardrail
    task_15_implement_block_diagnostics_gr_2["03-validator-neighbours-still-pass"]:::guardrail
  end
  style task_15_implement_block_diagnostics fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_16_author_tests_reachability_gate["16-author-tests-reachability-gate"]
    task_16_author_tests_reachability_gate_gr_0["01-build-passes"]:::guardrail
    task_16_author_tests_reachability_gate_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_16_author_tests_reachability_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_17_implement_reachability_gate["17-implement-reachability-gate"]
    task_17_implement_reachability_gate_gr_0["01-build-passes"]:::guardrail
    task_17_implement_reachability_gate_gr_1["02-reachability-tests-pass"]:::guardrail
    task_17_implement_reachability_gate_gr_2["04-loader-neighbours-still-pass"]:::guardrail
  end
  style task_17_implement_reachability_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_18_author_tests_endpoint_preflight["18-author-tests-endpoint-preflight"]
    task_18_author_tests_endpoint_preflight_gr_0["01-build-passes"]:::guardrail
    task_18_author_tests_endpoint_preflight_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_18_author_tests_endpoint_preflight fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_19_implement_endpoint_preflight["19-implement-endpoint-preflight"]
    task_19_implement_endpoint_preflight_gr_0["01-build-passes"]:::guardrail
    task_19_implement_endpoint_preflight_gr_1["02-preflight-tests-pass"]:::guardrail
  end
  style task_19_implement_endpoint_preflight fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_20_author_tests_providers_check["20-author-tests-providers-check"]
    task_20_author_tests_providers_check_gr_0["01-build-passes"]:::guardrail
    task_20_author_tests_providers_check_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_20_author_tests_providers_check fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_21_implement_providers_check["21-implement-providers-check"]
    task_21_implement_providers_check_gr_0["01-build-passes"]:::guardrail
    task_21_implement_providers_check_gr_1["02-providers-check-tests-pass"]:::guardrail
  end
  style task_21_implement_providers_check fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_22_author_tests_judge_spend["22-author-tests-judge-spend"]
    task_22_author_tests_judge_spend_gr_0["01-build-passes"]:::guardrail
    task_22_author_tests_judge_spend_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_22_author_tests_judge_spend fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_23_implement_judge_spend["23-implement-judge-spend"]
    task_23_implement_judge_spend_gr_0["01-build-passes"]:::guardrail
    task_23_implement_judge_spend_gr_1["02-judge-spend-tests-pass"]:::guardrail
    task_23_implement_judge_spend_gr_2["03-journal-neighbours-still-pass"]:::guardrail
  end
  style task_23_implement_judge_spend fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_24_record_openai_compat_in_ssot["24-record-openai-compat-in-ssot"]
    task_24_record_openai_compat_in_ssot_gr_0["01-ssot-records-the-runner"]:::guardrail
  end
  style task_24_record_openai_compat_in_ssot fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_25_mirror_canonical_block_in_schemas["25-mirror-canonical-block-in-schemas"]
    task_25_mirror_canonical_block_in_schemas_gr_0["01-canonical-block-mirrors-ssot"]:::guardrail
  end
  style task_25_mirror_canonical_block_in_schemas fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_26_record_in_domain_knowledge["26-record-in-domain-knowledge"]
    task_26_record_in_domain_knowledge_gr_0["01-domain-knowledge-records-runner"]:::guardrail
  end
  style task_26_record_in_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-suite-passes"]:::guardrail
    plan_guardrails_2["03-integration-suite-passes"]:::guardrail
    plan_guardrails_3["04-union-artifacts-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_role_seam
  plan_preflights --> task_03_author_tests_json_extractor
  plan_preflights --> task_05_build_fake_openai_server
  plan_preflights --> task_06_author_tests_tool_containment
  task_01_author_tests_role_seam --> task_02_assign_roles_at_seven_sites
  task_02_assign_roles_at_seven_sites --> task_04_implement_shared_json_extractor
  task_02_assign_roles_at_seven_sites --> task_08_author_tests_openai_runner
  task_03_author_tests_json_extractor --> task_04_implement_shared_json_extractor
  task_04_implement_shared_json_extractor --> task_08_author_tests_openai_runner
  task_05_build_fake_openai_server --> task_08_author_tests_openai_runner
  task_05_build_fake_openai_server --> task_18_author_tests_endpoint_preflight
  task_06_author_tests_tool_containment --> task_07_implement_tool_containment
  task_07_implement_tool_containment --> task_08_author_tests_openai_runner
  task_08_author_tests_openai_runner --> task_09_implement_runner_transport
  task_08_author_tests_openai_runner --> task_14_author_tests_block_diagnostics
  task_09_implement_runner_transport --> task_10_implement_runner_tool_loop
  task_10_implement_runner_tool_loop --> task_11_implement_runner_verdict_roles
  task_11_implement_runner_verdict_roles --> task_12_author_tests_kind_aware_harness
  task_11_implement_runner_verdict_roles --> task_18_author_tests_endpoint_preflight
  task_12_author_tests_kind_aware_harness --> task_13_implement_kind_aware_harness
  task_13_implement_kind_aware_harness --> task_22_author_tests_judge_spend
  task_13_implement_kind_aware_harness --> task_24_record_openai_compat_in_ssot
  task_14_author_tests_block_diagnostics --> task_15_implement_block_diagnostics
  task_15_implement_block_diagnostics --> task_16_author_tests_reachability_gate
  task_16_author_tests_reachability_gate --> task_17_implement_reachability_gate
  task_17_implement_reachability_gate --> task_24_record_openai_compat_in_ssot
  task_18_author_tests_endpoint_preflight --> task_19_implement_endpoint_preflight
  task_19_implement_endpoint_preflight --> task_20_author_tests_providers_check
  task_19_implement_endpoint_preflight --> task_24_record_openai_compat_in_ssot
  task_20_author_tests_providers_check --> task_21_implement_providers_check
  task_21_implement_providers_check --> task_24_record_openai_compat_in_ssot
  task_22_author_tests_judge_spend --> task_23_implement_judge_spend
  task_23_implement_judge_spend --> task_24_record_openai_compat_in_ssot
  task_24_record_openai_compat_in_ssot --> task_25_mirror_canonical_block_in_schemas
  task_24_record_openai_compat_in_ssot --> task_26_record_in_domain_knowledge
  task_25_mirror_canonical_block_in_schemas --> plan_guardrails
  task_26_record_in_domain_knowledge --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
