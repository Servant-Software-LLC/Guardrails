<!-- guardrails:graph v1 source-sha256=5da19723e88208196f7d5a8bb572fc1a558a99c51116a08c303d6fc43c878a19 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_update_ssot_schema_contract["01-update-ssot-schema-contract"]
    task_01_update_ssot_schema_contract_gr_0["01-ssot-has-new-sections"]:::guardrail
    task_01_update_ssot_schema_contract_gr_1["02-ssot-allocates-fresh-gr-codes"]:::guardrail
  end
  style task_01_update_ssot_schema_contract fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_author_tests_worktree_provider_seam["02-author-tests-worktree-provider-seam"]
    task_02_author_tests_worktree_provider_seam_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_02_author_tests_worktree_provider_seam_gr_1["02-seam-scenarios-present"]:::guardrail
  end
  style task_02_author_tests_worktree_provider_seam fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_implement_worktree_provider_seam_and_channel_envelope["03-implement-worktree-provider-seam-and-channel-envelope"]
    task_03_implement_worktree_provider_seam_and_channel_envelope_gr_0["01-core-builds"]:::guardrail
    task_03_implement_worktree_provider_seam_and_channel_envelope_gr_1["02-fake-implements-provider"]:::guardrail
    task_03_implement_worktree_provider_seam_and_channel_envelope_gr_2["03-seam-tests-pass"]:::guardrail
    task_03_implement_worktree_provider_seam_and_channel_envelope_gr_3["04-tests-untouched"]:::guardrail
  end
  style task_03_implement_worktree_provider_seam_and_channel_envelope fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_deserialize_scheduler_begin_triad_teardown["04-deserialize-scheduler-begin-triad-teardown"]
    task_04_deserialize_scheduler_begin_triad_teardown_gr_0["01-restore-seam-removed-from-executor"]:::guardrail
    task_04_deserialize_scheduler_begin_triad_teardown_gr_1["02-core-builds"]:::guardrail
    task_04_deserialize_scheduler_begin_triad_teardown_gr_2["03-seam-suite-still-green"]:::guardrail
    task_04_deserialize_scheduler_begin_triad_teardown_gr_3["04-exclusive-gate-removed-from-scheduler"]:::guardrail
  end
  style task_04_deserialize_scheduler_begin_triad_teardown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_git_worktree_lifecycle["05-author-tests-git-worktree-lifecycle"]
    task_05_author_tests_git_worktree_lifecycle_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_05_author_tests_git_worktree_lifecycle_gr_1["02-lifecycle-scenarios-present"]:::guardrail
  end
  style task_05_author_tests_git_worktree_lifecycle fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_implement_git_worktree_provider_and_reuse_topology["06-implement-git-worktree-provider-and-reuse-topology"]
    task_06_implement_git_worktree_provider_and_reuse_topology_gr_0["01-core-builds"]:::guardrail
    task_06_implement_git_worktree_provider_and_reuse_topology_gr_1["02-provider-implements-interface"]:::guardrail
    task_06_implement_git_worktree_provider_and_reuse_topology_gr_2["03-lifecycle-tests-pass"]:::guardrail
    task_06_implement_git_worktree_provider_and_reuse_topology_gr_3["04-tests-untouched"]:::guardrail
  end
  style task_06_implement_git_worktree_provider_and_reuse_topology fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_validation_gates_and_triad_removal["07-author-tests-validation-gates-and-triad-removal"]
    task_07_author_tests_validation_gates_and_triad_removal_gr_0["01-tests-build"]:::guardrail
    task_07_author_tests_validation_gates_and_triad_removal_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_07_author_tests_validation_gates_and_triad_removal fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_implement_validation_gates_and_triad_teardown["08-implement-validation-gates-and-triad-teardown"]
    task_08_implement_validation_gates_and_triad_teardown_gr_0["01-core-builds"]:::guardrail
    task_08_implement_validation_gates_and_triad_teardown_gr_1["02-triad-torn-down"]:::guardrail
    task_08_implement_validation_gates_and_triad_teardown_gr_2["03-gate-tests-pass"]:::guardrail
    task_08_implement_validation_gates_and_triad_teardown_gr_3["04-tests-untouched"]:::guardrail
  end
  style task_08_implement_validation_gates_and_triad_teardown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_author_tests_logs_elevation_and_runconfig["09-author-tests-logs-elevation-and-runconfig"]
    task_09_author_tests_logs_elevation_and_runconfig_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_09_author_tests_logs_elevation_and_runconfig_gr_1["02-config-scenarios-present"]:::guardrail
  end
  style task_09_author_tests_logs_elevation_and_runconfig fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_implement_logs_elevation_and_runconfig["10-implement-logs-elevation-and-runconfig"]
    task_10_implement_logs_elevation_and_runconfig_gr_0["01-core-builds"]:::guardrail
    task_10_implement_logs_elevation_and_runconfig_gr_1["02-logs-runconfig-tests-pass"]:::guardrail
    task_10_implement_logs_elevation_and_runconfig_gr_2["03-tests-untouched"]:::guardrail
  end
  style task_10_implement_logs_elevation_and_runconfig fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_author_tests_writescope_matcher_truth_table_and_fuzz["11-author-tests-writescope-matcher-truth-table-and-fuzz"]
    task_11_author_tests_writescope_matcher_truth_table_and_fuzz_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_11_author_tests_writescope_matcher_truth_table_and_fuzz_gr_1["02-truth-table-rows-present"]:::guardrail
  end
  style task_11_author_tests_writescope_matcher_truth_table_and_fuzz fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_implement_writescope_matcher["12-implement-writescope-matcher"]
    task_12_implement_writescope_matcher_gr_0["01-core-builds"]:::guardrail
    task_12_implement_writescope_matcher_gr_1["02-matcher-file-exists"]:::guardrail
    task_12_implement_writescope_matcher_gr_2["03-matcher-tests-pass"]:::guardrail
    task_12_implement_writescope_matcher_gr_3["04-tests-untouched"]:::guardrail
  end
  style task_12_implement_writescope_matcher fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_author_tests_writescope_check["13-author-tests-writescope-check"]
    task_13_author_tests_writescope_check_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_13_author_tests_writescope_check_gr_1["02-check-scenarios-present"]:::guardrail
  end
  style task_13_author_tests_writescope_check fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_implement_writescope_check_and_scoped_revert["14-implement-writescope-check-and-scoped-revert"]
    task_14_implement_writescope_check_and_scoped_revert_gr_0["01-core-builds"]:::guardrail
    task_14_implement_writescope_check_and_scoped_revert_gr_1["02-check-tests-pass"]:::guardrail
    task_14_implement_writescope_check_and_scoped_revert_gr_2["03-tests-untouched"]:::guardrail
  end
  style task_14_implement_writescope_check_and_scoped_revert fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_author_tests_reverifier_seam["15-author-tests-reverifier-seam"]
    task_15_author_tests_reverifier_seam_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_15_author_tests_reverifier_seam_gr_1["02-reverifier-scenarios-present"]:::guardrail
  end
  style task_15_author_tests_reverifier_seam fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_16_implement_reverifier_seam["16-implement-reverifier-seam"]
    task_16_implement_reverifier_seam_gr_0["01-core-builds"]:::guardrail
    task_16_implement_reverifier_seam_gr_1["02-reverifier-tests-pass"]:::guardrail
    task_16_implement_reverifier_seam_gr_2["03-tests-untouched"]:::guardrail
  end
  style task_16_implement_reverifier_seam fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_17_author_tests_merge_lock_and_ff_and_union_settle["17-author-tests-merge-lock-and-ff-and-union-settle"]
    task_17_author_tests_merge_lock_and_ff_and_union_settle_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_17_author_tests_merge_lock_and_ff_and_union_settle_gr_1["02-settle-scenarios-present"]:::guardrail
  end
  style task_17_author_tests_merge_lock_and_ff_and_union_settle fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_18_implement_merge_lock_and_ff_and_union_settle["18-implement-merge-lock-and-ff-and-union-settle"]
    task_18_implement_merge_lock_and_ff_and_union_settle_gr_0["01-core-builds"]:::guardrail
    task_18_implement_merge_lock_and_ff_and_union_settle_gr_1["02-settle-tests-pass"]:::guardrail
    task_18_implement_merge_lock_and_ff_and_union_settle_gr_2["03-tests-untouched"]:::guardrail
  end
  style task_18_implement_merge_lock_and_ff_and_union_settle fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_19_author_tests_resume_and_reset_retry["19-author-tests-resume-and-reset-retry"]
    task_19_author_tests_resume_and_reset_retry_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_19_author_tests_resume_and_reset_retry_gr_1["02-resume-scenarios-present"]:::guardrail
  end
  style task_19_author_tests_resume_and_reset_retry fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_20_implement_resume_reconciliation_and_reset_retry["20-implement-resume-reconciliation-and-reset-retry"]
    task_20_implement_resume_reconciliation_and_reset_retry_gr_0["01-core-builds"]:::guardrail
    task_20_implement_resume_reconciliation_and_reset_retry_gr_1["02-resume-tests-pass"]:::guardrail
    task_20_implement_resume_reconciliation_and_reset_retry_gr_2["03-tests-untouched"]:::guardrail
  end
  style task_20_implement_resume_reconciliation_and_reset_retry fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_21_author_tests_merge_on_success["21-author-tests-merge-on-success"]
    task_21_author_tests_merge_on_success_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_21_author_tests_merge_on_success_gr_1["02-merge-on-success-scenarios-present"]:::guardrail
  end
  style task_21_author_tests_merge_on_success fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_22_implement_merge_on_success["22-implement-merge-on-success"]
    task_22_implement_merge_on_success_gr_0["01-cli-builds"]:::guardrail
    task_22_implement_merge_on_success_gr_1["02-merge-on-success-tests-pass"]:::guardrail
    task_22_implement_merge_on_success_gr_2["03-tests-untouched"]:::guardrail
  end
  style task_22_implement_merge_on_success fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_23_author_tests_guardrail_scope_field["23-author-tests-guardrail-scope-field"]
    task_23_author_tests_guardrail_scope_field_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_23_author_tests_guardrail_scope_field_gr_1["02-scope-scenarios-present"]:::guardrail
  end
  style task_23_author_tests_guardrail_scope_field fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_24_implement_guardrail_scope_field["24-implement-guardrail-scope-field"]
    task_24_implement_guardrail_scope_field_gr_0["01-core-builds"]:::guardrail
    task_24_implement_guardrail_scope_field_gr_1["02-scope-tests-pass"]:::guardrail
    task_24_implement_guardrail_scope_field_gr_2["03-tests-untouched"]:::guardrail
  end
  style task_24_implement_guardrail_scope_field fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_25_author_tests_ai_merge_worker["25-author-tests-ai-merge-worker"]
    task_25_author_tests_ai_merge_worker_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_25_author_tests_ai_merge_worker_gr_1["02-ai-merge-scenarios-present"]:::guardrail
  end
  style task_25_author_tests_ai_merge_worker fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_26_implement_ai_merge_worker["26-implement-ai-merge-worker"]
    task_26_implement_ai_merge_worker_gr_0["01-core-builds"]:::guardrail
    task_26_implement_ai_merge_worker_gr_1["02-ai-merge-tests-pass"]:::guardrail
    task_26_implement_ai_merge_worker_gr_2["03-tests-untouched"]:::guardrail
    task_26_implement_ai_merge_worker_gr_3["04-deterministic-gates-present"]:::guardrail
  end
  style task_26_implement_ai_merge_worker fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_27_update_plan_breakdown_skill["27-update-plan-breakdown-skill"]
    task_27_update_plan_breakdown_skill_gr_0["01-skill-emits-new-mechanisms"]:::guardrail
    task_27_update_plan_breakdown_skill_gr_1["02-worked-example-uses-writescope-not-triad"]:::guardrail
  end
  style task_27_update_plan_breakdown_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_28_update_guardrails_review_skill["28-update-guardrails-review-skill"]
    task_28_update_guardrails_review_skill_gr_0["01-review-skill-has-scope-probes"]:::guardrail
  end
  style task_28_update_guardrails_review_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_29_update_domain_knowledge_skill["29-update-domain-knowledge-skill"]
    task_29_update_domain_knowledge_skill_gr_0["01-domain-knowledge-updated"]:::guardrail
  end
  style task_29_update_domain_knowledge_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_30_build_solution_gate["30-build-solution-gate"]
    task_30_build_solution_gate_gr_0["01-solution-builds"]:::guardrail
  end
  style task_30_build_solution_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_32_author_tests_needs_human_triage["32-author-tests-needs-human-triage"]
    task_32_author_tests_needs_human_triage_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_32_author_tests_needs_human_triage_gr_1["02-triage-scenarios-present"]:::guardrail
  end
  style task_32_author_tests_needs_human_triage fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_33_implement_needs_human_triage["33-implement-needs-human-triage"]
    task_33_implement_needs_human_triage_gr_0["01-core-builds"]:::guardrail
    task_33_implement_needs_human_triage_gr_1["02-triage-class-exists"]:::guardrail
    task_33_implement_needs_human_triage_gr_2["03-triage-tests-pass"]:::guardrail
    task_33_implement_needs_human_triage_gr_3["04-tests-untouched"]:::guardrail
  end
  style task_33_implement_needs_human_triage fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-full-suite"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_update_ssot_schema_contract
  task_01_update_ssot_schema_contract --> task_02_author_tests_worktree_provider_seam
  task_02_author_tests_worktree_provider_seam --> task_03_implement_worktree_provider_seam_and_channel_envelope
  task_03_implement_worktree_provider_seam_and_channel_envelope --> task_04_deserialize_scheduler_begin_triad_teardown
  task_04_deserialize_scheduler_begin_triad_teardown --> task_05_author_tests_git_worktree_lifecycle
  task_04_deserialize_scheduler_begin_triad_teardown --> task_07_author_tests_validation_gates_and_triad_removal
  task_04_deserialize_scheduler_begin_triad_teardown --> task_09_author_tests_logs_elevation_and_runconfig
  task_05_author_tests_git_worktree_lifecycle --> task_06_implement_git_worktree_provider_and_reuse_topology
  task_06_implement_git_worktree_provider_and_reuse_topology --> task_08_implement_validation_gates_and_triad_teardown
  task_06_implement_git_worktree_provider_and_reuse_topology --> task_10_implement_logs_elevation_and_runconfig
  task_07_author_tests_validation_gates_and_triad_removal --> task_08_implement_validation_gates_and_triad_teardown
  task_08_implement_validation_gates_and_triad_teardown --> task_11_author_tests_writescope_matcher_truth_table_and_fuzz
  task_09_author_tests_logs_elevation_and_runconfig --> task_10_implement_logs_elevation_and_runconfig
  task_10_implement_logs_elevation_and_runconfig --> task_15_author_tests_reverifier_seam
  task_10_implement_logs_elevation_and_runconfig --> task_32_author_tests_needs_human_triage
  task_11_author_tests_writescope_matcher_truth_table_and_fuzz --> task_12_implement_writescope_matcher
  task_12_implement_writescope_matcher --> task_13_author_tests_writescope_check
  task_13_author_tests_writescope_check --> task_14_implement_writescope_check_and_scoped_revert
  task_14_implement_writescope_check_and_scoped_revert --> task_15_author_tests_reverifier_seam
  task_15_author_tests_reverifier_seam --> task_16_implement_reverifier_seam
  task_16_implement_reverifier_seam --> task_17_author_tests_merge_lock_and_ff_and_union_settle
  task_16_implement_reverifier_seam --> task_23_author_tests_guardrail_scope_field
  task_17_author_tests_merge_lock_and_ff_and_union_settle --> task_18_implement_merge_lock_and_ff_and_union_settle
  task_18_implement_merge_lock_and_ff_and_union_settle --> task_19_author_tests_resume_and_reset_retry
  task_18_implement_merge_lock_and_ff_and_union_settle --> task_21_author_tests_merge_on_success
  task_19_author_tests_resume_and_reset_retry --> task_20_implement_resume_reconciliation_and_reset_retry
  task_20_implement_resume_reconciliation_and_reset_retry --> task_22_implement_merge_on_success
  task_21_author_tests_merge_on_success --> task_22_implement_merge_on_success
  task_22_implement_merge_on_success --> task_25_author_tests_ai_merge_worker
  task_23_author_tests_guardrail_scope_field --> task_24_implement_guardrail_scope_field
  task_24_implement_guardrail_scope_field --> task_25_author_tests_ai_merge_worker
  task_25_author_tests_ai_merge_worker --> task_26_implement_ai_merge_worker
  task_26_implement_ai_merge_worker --> task_27_update_plan_breakdown_skill
  task_26_implement_ai_merge_worker --> task_28_update_guardrails_review_skill
  task_26_implement_ai_merge_worker --> task_29_update_domain_knowledge_skill
  task_26_implement_ai_merge_worker --> task_30_build_solution_gate
  task_26_implement_ai_merge_worker --> task_33_implement_needs_human_triage
  task_32_author_tests_needs_human_triage --> task_33_implement_needs_human_triage
  task_33_implement_needs_human_triage --> task_30_build_solution_gate
  task_27_update_plan_breakdown_skill --> plan_guardrails
  task_28_update_guardrails_review_skill --> plan_guardrails
  task_29_update_domain_knowledge_skill --> plan_guardrails
  task_30_build_solution_gate --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
