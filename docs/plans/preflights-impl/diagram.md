<!-- guardrails:graph v1 source-sha256=a8ef4ee8b05e88052a049939d5ed7a4db656575230e73d9df46b950426a1dfd8 -->

```mermaid
flowchart TD
  task_00_baseline_existing_tests_green["00-baseline-existing-tests-green"]:::task
  gr_00_baseline_existing_tests_green_0["Existing repo test suite passes on the current code (baseline-green root, &#35;181)"]:::guardrail
  task_00_baseline_existing_tests_green --> gr_00_baseline_existing_tests_green_0
  gr_00_baseline_existing_tests_green_0 --> done_00_baseline_existing_tests_green
  done_00_baseline_existing_tests_green["00-baseline-existing-tests-green ✓ Finished"]:::done
  task_01_author_tests_reverifier_wiring["01-author-tests-reverifier-wiring"]:::task
  gr_01_author_tests_reverifier_wiring_0["01-build-passes"]:::guardrail
  task_01_author_tests_reverifier_wiring --> gr_01_author_tests_reverifier_wiring_0
  gr_01_author_tests_reverifier_wiring_0 --> done_01_author_tests_reverifier_wiring
  gr_01_author_tests_reverifier_wiring_1["02-tests-fail-on-current-code"]:::guardrail
  task_01_author_tests_reverifier_wiring --> gr_01_author_tests_reverifier_wiring_1
  gr_01_author_tests_reverifier_wiring_1 --> done_01_author_tests_reverifier_wiring
  done_01_author_tests_reverifier_wiring["01-author-tests-reverifier-wiring ✓ Finished"]:::done
  task_02_implement_reverifier_wiring["02-implement-reverifier-wiring"]:::task
  gr_02_implement_reverifier_wiring_0["01-build-passes"]:::guardrail
  task_02_implement_reverifier_wiring --> gr_02_implement_reverifier_wiring_0
  gr_02_implement_reverifier_wiring_0 --> done_02_implement_reverifier_wiring
  gr_02_implement_reverifier_wiring_1["02-wiring-tests-pass"]:::guardrail
  task_02_implement_reverifier_wiring --> gr_02_implement_reverifier_wiring_1
  gr_02_implement_reverifier_wiring_1 --> done_02_implement_reverifier_wiring
  done_02_implement_reverifier_wiring["02-implement-reverifier-wiring ✓ Finished"]:::done
  task_03_author_tests_four_folder_loader["03-author-tests-four-folder-loader"]:::task
  gr_03_author_tests_four_folder_loader_0["01-build-passes"]:::guardrail
  task_03_author_tests_four_folder_loader --> gr_03_author_tests_four_folder_loader_0
  gr_03_author_tests_four_folder_loader_0 --> done_03_author_tests_four_folder_loader
  gr_03_author_tests_four_folder_loader_1["02-tests-fail-on-stubs"]:::guardrail
  task_03_author_tests_four_folder_loader --> gr_03_author_tests_four_folder_loader_1
  gr_03_author_tests_four_folder_loader_1 --> done_03_author_tests_four_folder_loader
  gr_03_author_tests_four_folder_loader_2["03-covers-key-behaviors"]:::guardrail
  task_03_author_tests_four_folder_loader --> gr_03_author_tests_four_folder_loader_2
  gr_03_author_tests_four_folder_loader_2 --> done_03_author_tests_four_folder_loader
  done_03_author_tests_four_folder_loader["03-author-tests-four-folder-loader ✓ Finished"]:::done
  task_04_implement_four_folder_loader["04-implement-four-folder-loader"]:::task
  gr_04_implement_four_folder_loader_0["01-build-passes"]:::guardrail
  task_04_implement_four_folder_loader --> gr_04_implement_four_folder_loader_0
  gr_04_implement_four_folder_loader_0 --> done_04_implement_four_folder_loader
  gr_04_implement_four_folder_loader_1["02-four-folder-tests-pass"]:::guardrail
  task_04_implement_four_folder_loader --> gr_04_implement_four_folder_loader_1
  gr_04_implement_four_folder_loader_1 --> done_04_implement_four_folder_loader
  gr_04_implement_four_folder_loader_2["03-ssot-updated"]:::guardrail
  task_04_implement_four_folder_loader --> gr_04_implement_four_folder_loader_2
  gr_04_implement_four_folder_loader_2 --> done_04_implement_four_folder_loader
  done_04_implement_four_folder_loader["04-implement-four-folder-loader ✓ Finished"]:::done
  task_05_outcomes_and_journal["05-outcomes-and-journal"]:::task
  gr_05_outcomes_and_journal_0["01-build-passes"]:::guardrail
  task_05_outcomes_and_journal --> gr_05_outcomes_and_journal_0
  gr_05_outcomes_and_journal_0 --> done_05_outcomes_and_journal
  gr_05_outcomes_and_journal_1["02-journal-round-trips"]:::guardrail
  task_05_outcomes_and_journal --> gr_05_outcomes_and_journal_1
  gr_05_outcomes_and_journal_1 --> done_05_outcomes_and_journal
  gr_05_outcomes_and_journal_2["03-covers-key-behaviors"]:::guardrail
  task_05_outcomes_and_journal --> gr_05_outcomes_and_journal_2
  gr_05_outcomes_and_journal_2 --> done_05_outcomes_and_journal
  done_05_outcomes_and_journal["05-outcomes-and-journal ✓ Finished"]:::done
  task_06_author_tests_pre_dag_phase["06-author-tests-pre-dag-phase"]:::task
  gr_06_author_tests_pre_dag_phase_0["01-build-passes"]:::guardrail
  task_06_author_tests_pre_dag_phase --> gr_06_author_tests_pre_dag_phase_0
  gr_06_author_tests_pre_dag_phase_0 --> done_06_author_tests_pre_dag_phase
  gr_06_author_tests_pre_dag_phase_1["02-tests-fail-on-current-code"]:::guardrail
  task_06_author_tests_pre_dag_phase --> gr_06_author_tests_pre_dag_phase_1
  gr_06_author_tests_pre_dag_phase_1 --> done_06_author_tests_pre_dag_phase
  gr_06_author_tests_pre_dag_phase_2["03-covers-key-behaviors"]:::guardrail
  task_06_author_tests_pre_dag_phase --> gr_06_author_tests_pre_dag_phase_2
  gr_06_author_tests_pre_dag_phase_2 --> done_06_author_tests_pre_dag_phase
  done_06_author_tests_pre_dag_phase["06-author-tests-pre-dag-phase ✓ Finished"]:::done
  task_07_implement_pre_dag_phase["07-implement-pre-dag-phase"]:::task
  gr_07_implement_pre_dag_phase_0["01-build-passes"]:::guardrail
  task_07_implement_pre_dag_phase --> gr_07_implement_pre_dag_phase_0
  gr_07_implement_pre_dag_phase_0 --> done_07_implement_pre_dag_phase
  gr_07_implement_pre_dag_phase_1["02-pre-dag-tests-pass"]:::guardrail
  task_07_implement_pre_dag_phase --> gr_07_implement_pre_dag_phase_1
  gr_07_implement_pre_dag_phase_1 --> done_07_implement_pre_dag_phase
  gr_07_implement_pre_dag_phase_2["03-ssot-resume-rule"]:::guardrail
  task_07_implement_pre_dag_phase --> gr_07_implement_pre_dag_phase_2
  gr_07_implement_pre_dag_phase_2 --> done_07_implement_pre_dag_phase
  done_07_implement_pre_dag_phase["07-implement-pre-dag-phase ✓ Finished"]:::done
  task_08_author_tests_terminal_phase["08-author-tests-terminal-phase"]:::task
  gr_08_author_tests_terminal_phase_0["01-build-passes"]:::guardrail
  task_08_author_tests_terminal_phase --> gr_08_author_tests_terminal_phase_0
  gr_08_author_tests_terminal_phase_0 --> done_08_author_tests_terminal_phase
  gr_08_author_tests_terminal_phase_1["02-tests-fail-on-current-code"]:::guardrail
  task_08_author_tests_terminal_phase --> gr_08_author_tests_terminal_phase_1
  gr_08_author_tests_terminal_phase_1 --> done_08_author_tests_terminal_phase
  gr_08_author_tests_terminal_phase_2["03-covers-key-behaviors"]:::guardrail
  task_08_author_tests_terminal_phase --> gr_08_author_tests_terminal_phase_2
  gr_08_author_tests_terminal_phase_2 --> done_08_author_tests_terminal_phase
  done_08_author_tests_terminal_phase["08-author-tests-terminal-phase ✓ Finished"]:::done
  task_09_implement_terminal_phase["09-implement-terminal-phase"]:::task
  gr_09_implement_terminal_phase_0["01-build-passes"]:::guardrail
  task_09_implement_terminal_phase --> gr_09_implement_terminal_phase_0
  gr_09_implement_terminal_phase_0 --> done_09_implement_terminal_phase
  gr_09_implement_terminal_phase_1["02-terminal-tests-pass"]:::guardrail
  task_09_implement_terminal_phase --> gr_09_implement_terminal_phase_1
  gr_09_implement_terminal_phase_1 --> done_09_implement_terminal_phase
  gr_09_implement_terminal_phase_2["03-ssot-revalidate"]:::guardrail
  task_09_implement_terminal_phase --> gr_09_implement_terminal_phase_2
  gr_09_implement_terminal_phase_2 --> done_09_implement_terminal_phase
  done_09_implement_terminal_phase["09-implement-terminal-phase ✓ Finished"]:::done
  task_10_author_tests_task_preflight_slot["10-author-tests-task-preflight-slot"]:::task
  gr_10_author_tests_task_preflight_slot_0["01-build-passes"]:::guardrail
  task_10_author_tests_task_preflight_slot --> gr_10_author_tests_task_preflight_slot_0
  gr_10_author_tests_task_preflight_slot_0 --> done_10_author_tests_task_preflight_slot
  gr_10_author_tests_task_preflight_slot_1["02-tests-fail-on-current-code"]:::guardrail
  task_10_author_tests_task_preflight_slot --> gr_10_author_tests_task_preflight_slot_1
  gr_10_author_tests_task_preflight_slot_1 --> done_10_author_tests_task_preflight_slot
  gr_10_author_tests_task_preflight_slot_2["03-covers-key-behaviors"]:::guardrail
  task_10_author_tests_task_preflight_slot --> gr_10_author_tests_task_preflight_slot_2
  gr_10_author_tests_task_preflight_slot_2 --> done_10_author_tests_task_preflight_slot
  done_10_author_tests_task_preflight_slot["10-author-tests-task-preflight-slot ✓ Finished"]:::done
  task_11_implement_task_preflight_slot["11-implement-task-preflight-slot"]:::task
  gr_11_implement_task_preflight_slot_0["01-build-passes"]:::guardrail
  task_11_implement_task_preflight_slot --> gr_11_implement_task_preflight_slot_0
  gr_11_implement_task_preflight_slot_0 --> done_11_implement_task_preflight_slot
  gr_11_implement_task_preflight_slot_1["02-task-preflight-tests-pass"]:::guardrail
  task_11_implement_task_preflight_slot --> gr_11_implement_task_preflight_slot_1
  gr_11_implement_task_preflight_slot_1 --> done_11_implement_task_preflight_slot
  done_11_implement_task_preflight_slot["11-implement-task-preflight-slot ✓ Finished"]:::done
  task_12_author_tests_diagram_renderer["12-author-tests-diagram-renderer"]:::task
  gr_12_author_tests_diagram_renderer_0["01-build-passes"]:::guardrail
  task_12_author_tests_diagram_renderer --> gr_12_author_tests_diagram_renderer_0
  gr_12_author_tests_diagram_renderer_0 --> done_12_author_tests_diagram_renderer
  gr_12_author_tests_diagram_renderer_1["02-tests-fail-on-current-code"]:::guardrail
  task_12_author_tests_diagram_renderer --> gr_12_author_tests_diagram_renderer_1
  gr_12_author_tests_diagram_renderer_1 --> done_12_author_tests_diagram_renderer
  gr_12_author_tests_diagram_renderer_2["03-covers-key-behaviors"]:::guardrail
  task_12_author_tests_diagram_renderer --> gr_12_author_tests_diagram_renderer_2
  gr_12_author_tests_diagram_renderer_2 --> done_12_author_tests_diagram_renderer
  done_12_author_tests_diagram_renderer["12-author-tests-diagram-renderer ✓ Finished"]:::done
  task_13_implement_diagram_renderer["13-implement-diagram-renderer"]:::task
  gr_13_implement_diagram_renderer_0["01-build-passes"]:::guardrail
  task_13_implement_diagram_renderer --> gr_13_implement_diagram_renderer_0
  gr_13_implement_diagram_renderer_0 --> done_13_implement_diagram_renderer
  gr_13_implement_diagram_renderer_1["02-renderer-tests-pass"]:::guardrail
  task_13_implement_diagram_renderer --> gr_13_implement_diagram_renderer_1
  gr_13_implement_diagram_renderer_1 --> done_13_implement_diagram_renderer
  gr_13_implement_diagram_renderer_2["03-ssot-renderer"]:::guardrail
  task_13_implement_diagram_renderer --> gr_13_implement_diagram_renderer_2
  gr_13_implement_diagram_renderer_2 --> done_13_implement_diagram_renderer
  done_13_implement_diagram_renderer["13-implement-diagram-renderer ✓ Finished"]:::done
  task_14_update_plan_breakdown_skill["14-update-plan-breakdown-skill"]:::task
  gr_14_update_plan_breakdown_skill_0["01-four-folders-documented"]:::guardrail
  task_14_update_plan_breakdown_skill --> gr_14_update_plan_breakdown_skill_0
  gr_14_update_plan_breakdown_skill_0 --> done_14_update_plan_breakdown_skill
  gr_14_update_plan_breakdown_skill_1["02-precondition-scope-removed"]:::guardrail
  task_14_update_plan_breakdown_skill --> gr_14_update_plan_breakdown_skill_1
  gr_14_update_plan_breakdown_skill_1 --> done_14_update_plan_breakdown_skill
  done_14_update_plan_breakdown_skill["14-update-plan-breakdown-skill ✓ Finished"]:::done
  task_15_update_guardrails_review_skill["15-update-guardrails-review-skill"]:::task
  gr_15_update_guardrails_review_skill_0["01-four-folder-probes-documented"]:::guardrail
  task_15_update_guardrails_review_skill --> gr_15_update_guardrails_review_skill_0
  gr_15_update_guardrails_review_skill_0 --> done_15_update_guardrails_review_skill
  done_15_update_guardrails_review_skill["15-update-guardrails-review-skill ✓ Finished"]:::done
  task_16_update_domain_knowledge_skill["16-update-domain-knowledge-skill"]:::task
  gr_16_update_domain_knowledge_skill_0["01-domain-facts-updated"]:::guardrail
  task_16_update_domain_knowledge_skill --> gr_16_update_domain_knowledge_skill_0
  gr_16_update_domain_knowledge_skill_0 --> done_16_update_domain_knowledge_skill
  done_16_update_domain_knowledge_skill["16-update-domain-knowledge-skill ✓ Finished"]:::done
  task_17_author_four_folder_example["17-author-four-folder-example"]:::task
  gr_17_author_four_folder_example_0["01-validate-clean"]:::guardrail
  task_17_author_four_folder_example --> gr_17_author_four_folder_example_0
  gr_17_author_four_folder_example_0 --> done_17_author_four_folder_example
  gr_17_author_four_folder_example_1["02-four-folders-present-legacy-absent"]:::guardrail
  task_17_author_four_folder_example --> gr_17_author_four_folder_example_1
  gr_17_author_four_folder_example_1 --> done_17_author_four_folder_example
  gr_17_author_four_folder_example_2["03-diagram-container-model"]:::guardrail
  task_17_author_four_folder_example --> gr_17_author_four_folder_example_2
  gr_17_author_four_folder_example_2 --> done_17_author_four_folder_example
  done_17_author_four_folder_example["17-author-four-folder-example ✓ Finished"]:::done
  task_18_terminal_integration_gate["18-terminal-integration-gate"]:::task
  gr_18_terminal_integration_gate_0["Union invariant on the shared multi-writer files (SSOT doc, Scheduler/TaskExecutor/Factory, DiagnosticCodes): non-empty and conflict-marker-free"]:::guardrail
  task_18_terminal_integration_gate --> gr_18_terminal_integration_gate_0
  gr_18_terminal_integration_gate_0 --> done_18_terminal_integration_gate
  gr_18_terminal_integration_gate_1["Full solution build on the merged HEAD - catches cross-project breaks after all tasks merge (LOCAL, terminal-only)"]:::guardrail
  task_18_terminal_integration_gate --> gr_18_terminal_integration_gate_1
  gr_18_terminal_integration_gate_1 --> done_18_terminal_integration_gate
  gr_18_terminal_integration_gate_2["Whole test suite green on the merged HEAD (LOCAL, terminal-only)"]:::guardrail
  task_18_terminal_integration_gate --> gr_18_terminal_integration_gate_2
  gr_18_terminal_integration_gate_2 --> done_18_terminal_integration_gate
  done_18_terminal_integration_gate["18-terminal-integration-gate ✓ Finished"]:::done
  done_00_baseline_existing_tests_green --> task_01_author_tests_reverifier_wiring
  done_00_baseline_existing_tests_green --> task_03_author_tests_four_folder_loader
  done_00_baseline_existing_tests_green --> task_05_outcomes_and_journal
  done_01_author_tests_reverifier_wiring --> task_02_implement_reverifier_wiring
  done_02_implement_reverifier_wiring --> task_07_implement_pre_dag_phase
  done_02_implement_reverifier_wiring --> task_09_implement_terminal_phase
  done_02_implement_reverifier_wiring --> task_11_implement_task_preflight_slot
  done_03_author_tests_four_folder_loader --> task_04_implement_four_folder_loader
  done_04_implement_four_folder_loader --> task_06_author_tests_pre_dag_phase
  done_04_implement_four_folder_loader --> task_08_author_tests_terminal_phase
  done_04_implement_four_folder_loader --> task_10_author_tests_task_preflight_slot
  done_04_implement_four_folder_loader --> task_12_author_tests_diagram_renderer
  done_04_implement_four_folder_loader --> task_14_update_plan_breakdown_skill
  done_04_implement_four_folder_loader --> task_15_update_guardrails_review_skill
  done_04_implement_four_folder_loader --> task_16_update_domain_knowledge_skill
  done_04_implement_four_folder_loader --> task_17_author_four_folder_example
  done_05_outcomes_and_journal --> task_06_author_tests_pre_dag_phase
  done_05_outcomes_and_journal --> task_08_author_tests_terminal_phase
  done_05_outcomes_and_journal --> task_10_author_tests_task_preflight_slot
  done_05_outcomes_and_journal --> task_16_update_domain_knowledge_skill
  done_06_author_tests_pre_dag_phase --> task_07_implement_pre_dag_phase
  done_07_implement_pre_dag_phase --> task_09_implement_terminal_phase
  done_08_author_tests_terminal_phase --> task_09_implement_terminal_phase
  done_09_implement_terminal_phase --> task_18_terminal_integration_gate
  done_10_author_tests_task_preflight_slot --> task_11_implement_task_preflight_slot
  done_11_implement_task_preflight_slot --> task_18_terminal_integration_gate
  done_12_author_tests_diagram_renderer --> task_13_implement_diagram_renderer
  done_13_implement_diagram_renderer --> task_17_author_four_folder_example
  done_14_update_plan_breakdown_skill --> task_18_terminal_integration_gate
  done_15_update_guardrails_review_skill --> task_18_terminal_integration_gate
  done_16_update_domain_knowledge_skill --> task_18_terminal_integration_gate
  done_17_author_four_folder_example --> task_18_terminal_integration_gate
  classDef task fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
  classDef done fill:#d4edda,stroke:#2e7d32,color:#10341a;
```

_Structure only — retry, feedback, and needs-human edges are omitted._
