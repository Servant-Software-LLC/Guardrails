<!-- guardrails:graph v1 source-sha256=c87e385ab1de51d3f57fdef390d27d61a5985eb6f2be5f9abef891b38bb40f26 -->

```mermaid
flowchart TD
  task_01_update_ssot_schema_contract["01-update-ssot-schema-contract"]:::task
  gr_01_update_ssot_schema_contract_0["01-ssot-has-new-sections"]:::guardrail
  task_01_update_ssot_schema_contract --> gr_01_update_ssot_schema_contract_0
  gr_01_update_ssot_schema_contract_0 --> done_01_update_ssot_schema_contract
  gr_01_update_ssot_schema_contract_1["02-ssot-allocates-fresh-gr-codes"]:::guardrail
  task_01_update_ssot_schema_contract --> gr_01_update_ssot_schema_contract_1
  gr_01_update_ssot_schema_contract_1 --> done_01_update_ssot_schema_contract
  done_01_update_ssot_schema_contract["01-update-ssot-schema-contract ✓ Finished"]:::done
  task_02_author_tests_worktree_provider_seam["02-author-tests-worktree-provider-seam"]:::task
  gr_02_author_tests_worktree_provider_seam_0["01-tests-fail-on-current-code"]:::guardrail
  task_02_author_tests_worktree_provider_seam --> gr_02_author_tests_worktree_provider_seam_0
  gr_02_author_tests_worktree_provider_seam_0 --> done_02_author_tests_worktree_provider_seam
  gr_02_author_tests_worktree_provider_seam_1["02-seam-scenarios-present"]:::guardrail
  task_02_author_tests_worktree_provider_seam --> gr_02_author_tests_worktree_provider_seam_1
  gr_02_author_tests_worktree_provider_seam_1 --> done_02_author_tests_worktree_provider_seam
  done_02_author_tests_worktree_provider_seam["02-author-tests-worktree-provider-seam ✓ Finished"]:::done
  task_03_implement_worktree_provider_seam_and_channel_envelope["03-implement-worktree-provider-seam-and-channel-envelope"]:::task
  gr_03_implement_worktree_provider_seam_and_channel_envelope_0["01-core-builds"]:::guardrail
  task_03_implement_worktree_provider_seam_and_channel_envelope --> gr_03_implement_worktree_provider_seam_and_channel_envelope_0
  gr_03_implement_worktree_provider_seam_and_channel_envelope_0 --> done_03_implement_worktree_provider_seam_and_channel_envelope
  gr_03_implement_worktree_provider_seam_and_channel_envelope_1["02-fake-implements-provider"]:::guardrail
  task_03_implement_worktree_provider_seam_and_channel_envelope --> gr_03_implement_worktree_provider_seam_and_channel_envelope_1
  gr_03_implement_worktree_provider_seam_and_channel_envelope_1 --> done_03_implement_worktree_provider_seam_and_channel_envelope
  gr_03_implement_worktree_provider_seam_and_channel_envelope_2["03-seam-tests-pass"]:::guardrail
  task_03_implement_worktree_provider_seam_and_channel_envelope --> gr_03_implement_worktree_provider_seam_and_channel_envelope_2
  gr_03_implement_worktree_provider_seam_and_channel_envelope_2 --> done_03_implement_worktree_provider_seam_and_channel_envelope
  gr_03_implement_worktree_provider_seam_and_channel_envelope_3["04-tests-untouched"]:::guardrail
  task_03_implement_worktree_provider_seam_and_channel_envelope --> gr_03_implement_worktree_provider_seam_and_channel_envelope_3
  gr_03_implement_worktree_provider_seam_and_channel_envelope_3 --> done_03_implement_worktree_provider_seam_and_channel_envelope
  done_03_implement_worktree_provider_seam_and_channel_envelope["03-implement-worktree-provider-seam-and-channel-envelope ✓ Finished"]:::done
  task_04_deserialize_scheduler_begin_triad_teardown["04-deserialize-scheduler-begin-triad-teardown"]:::task
  gr_04_deserialize_scheduler_begin_triad_teardown_0["01-restore-seam-removed-from-executor"]:::guardrail
  task_04_deserialize_scheduler_begin_triad_teardown --> gr_04_deserialize_scheduler_begin_triad_teardown_0
  gr_04_deserialize_scheduler_begin_triad_teardown_0 --> done_04_deserialize_scheduler_begin_triad_teardown
  gr_04_deserialize_scheduler_begin_triad_teardown_1["02-core-builds"]:::guardrail
  task_04_deserialize_scheduler_begin_triad_teardown --> gr_04_deserialize_scheduler_begin_triad_teardown_1
  gr_04_deserialize_scheduler_begin_triad_teardown_1 --> done_04_deserialize_scheduler_begin_triad_teardown
  gr_04_deserialize_scheduler_begin_triad_teardown_2["03-seam-suite-still-green"]:::guardrail
  task_04_deserialize_scheduler_begin_triad_teardown --> gr_04_deserialize_scheduler_begin_triad_teardown_2
  gr_04_deserialize_scheduler_begin_triad_teardown_2 --> done_04_deserialize_scheduler_begin_triad_teardown
  gr_04_deserialize_scheduler_begin_triad_teardown_3["04-exclusive-gate-removed-from-scheduler"]:::guardrail
  task_04_deserialize_scheduler_begin_triad_teardown --> gr_04_deserialize_scheduler_begin_triad_teardown_3
  gr_04_deserialize_scheduler_begin_triad_teardown_3 --> done_04_deserialize_scheduler_begin_triad_teardown
  done_04_deserialize_scheduler_begin_triad_teardown["04-deserialize-scheduler-begin-triad-teardown ✓ Finished"]:::done
  task_05_author_tests_git_worktree_lifecycle["05-author-tests-git-worktree-lifecycle"]:::task
  gr_05_author_tests_git_worktree_lifecycle_0["01-tests-fail-on-current-code"]:::guardrail
  task_05_author_tests_git_worktree_lifecycle --> gr_05_author_tests_git_worktree_lifecycle_0
  gr_05_author_tests_git_worktree_lifecycle_0 --> done_05_author_tests_git_worktree_lifecycle
  gr_05_author_tests_git_worktree_lifecycle_1["02-lifecycle-scenarios-present"]:::guardrail
  task_05_author_tests_git_worktree_lifecycle --> gr_05_author_tests_git_worktree_lifecycle_1
  gr_05_author_tests_git_worktree_lifecycle_1 --> done_05_author_tests_git_worktree_lifecycle
  done_05_author_tests_git_worktree_lifecycle["05-author-tests-git-worktree-lifecycle ✓ Finished"]:::done
  task_06_implement_git_worktree_provider_and_reuse_topology["06-implement-git-worktree-provider-and-reuse-topology"]:::task
  gr_06_implement_git_worktree_provider_and_reuse_topology_0["01-core-builds"]:::guardrail
  task_06_implement_git_worktree_provider_and_reuse_topology --> gr_06_implement_git_worktree_provider_and_reuse_topology_0
  gr_06_implement_git_worktree_provider_and_reuse_topology_0 --> done_06_implement_git_worktree_provider_and_reuse_topology
  gr_06_implement_git_worktree_provider_and_reuse_topology_1["02-provider-implements-interface"]:::guardrail
  task_06_implement_git_worktree_provider_and_reuse_topology --> gr_06_implement_git_worktree_provider_and_reuse_topology_1
  gr_06_implement_git_worktree_provider_and_reuse_topology_1 --> done_06_implement_git_worktree_provider_and_reuse_topology
  gr_06_implement_git_worktree_provider_and_reuse_topology_2["03-lifecycle-tests-pass"]:::guardrail
  task_06_implement_git_worktree_provider_and_reuse_topology --> gr_06_implement_git_worktree_provider_and_reuse_topology_2
  gr_06_implement_git_worktree_provider_and_reuse_topology_2 --> done_06_implement_git_worktree_provider_and_reuse_topology
  gr_06_implement_git_worktree_provider_and_reuse_topology_3["04-tests-untouched"]:::guardrail
  task_06_implement_git_worktree_provider_and_reuse_topology --> gr_06_implement_git_worktree_provider_and_reuse_topology_3
  gr_06_implement_git_worktree_provider_and_reuse_topology_3 --> done_06_implement_git_worktree_provider_and_reuse_topology
  done_06_implement_git_worktree_provider_and_reuse_topology["06-implement-git-worktree-provider-and-reuse-topology ✓ Finished"]:::done
  task_07_author_tests_validation_gates_and_triad_removal["07-author-tests-validation-gates-and-triad-removal"]:::task
  gr_07_author_tests_validation_gates_and_triad_removal_0["01-tests-build"]:::guardrail
  task_07_author_tests_validation_gates_and_triad_removal --> gr_07_author_tests_validation_gates_and_triad_removal_0
  gr_07_author_tests_validation_gates_and_triad_removal_0 --> done_07_author_tests_validation_gates_and_triad_removal
  gr_07_author_tests_validation_gates_and_triad_removal_1["02-tests-fail-on-current-code"]:::guardrail
  task_07_author_tests_validation_gates_and_triad_removal --> gr_07_author_tests_validation_gates_and_triad_removal_1
  gr_07_author_tests_validation_gates_and_triad_removal_1 --> done_07_author_tests_validation_gates_and_triad_removal
  done_07_author_tests_validation_gates_and_triad_removal["07-author-tests-validation-gates-and-triad-removal ✓ Finished"]:::done
  task_08_implement_validation_gates_and_triad_teardown["08-implement-validation-gates-and-triad-teardown"]:::task
  gr_08_implement_validation_gates_and_triad_teardown_0["01-core-builds"]:::guardrail
  task_08_implement_validation_gates_and_triad_teardown --> gr_08_implement_validation_gates_and_triad_teardown_0
  gr_08_implement_validation_gates_and_triad_teardown_0 --> done_08_implement_validation_gates_and_triad_teardown
  gr_08_implement_validation_gates_and_triad_teardown_1["02-triad-torn-down"]:::guardrail
  task_08_implement_validation_gates_and_triad_teardown --> gr_08_implement_validation_gates_and_triad_teardown_1
  gr_08_implement_validation_gates_and_triad_teardown_1 --> done_08_implement_validation_gates_and_triad_teardown
  gr_08_implement_validation_gates_and_triad_teardown_2["03-gate-tests-pass"]:::guardrail
  task_08_implement_validation_gates_and_triad_teardown --> gr_08_implement_validation_gates_and_triad_teardown_2
  gr_08_implement_validation_gates_and_triad_teardown_2 --> done_08_implement_validation_gates_and_triad_teardown
  gr_08_implement_validation_gates_and_triad_teardown_3["04-tests-untouched"]:::guardrail
  task_08_implement_validation_gates_and_triad_teardown --> gr_08_implement_validation_gates_and_triad_teardown_3
  gr_08_implement_validation_gates_and_triad_teardown_3 --> done_08_implement_validation_gates_and_triad_teardown
  done_08_implement_validation_gates_and_triad_teardown["08-implement-validation-gates-and-triad-teardown ✓ Finished"]:::done
  task_09_author_tests_logs_elevation_and_runconfig["09-author-tests-logs-elevation-and-runconfig"]:::task
  gr_09_author_tests_logs_elevation_and_runconfig_0["01-tests-fail-on-current-code"]:::guardrail
  task_09_author_tests_logs_elevation_and_runconfig --> gr_09_author_tests_logs_elevation_and_runconfig_0
  gr_09_author_tests_logs_elevation_and_runconfig_0 --> done_09_author_tests_logs_elevation_and_runconfig
  gr_09_author_tests_logs_elevation_and_runconfig_1["02-config-scenarios-present"]:::guardrail
  task_09_author_tests_logs_elevation_and_runconfig --> gr_09_author_tests_logs_elevation_and_runconfig_1
  gr_09_author_tests_logs_elevation_and_runconfig_1 --> done_09_author_tests_logs_elevation_and_runconfig
  done_09_author_tests_logs_elevation_and_runconfig["09-author-tests-logs-elevation-and-runconfig ✓ Finished"]:::done
  task_10_implement_logs_elevation_and_runconfig["10-implement-logs-elevation-and-runconfig"]:::task
  gr_10_implement_logs_elevation_and_runconfig_0["01-core-builds"]:::guardrail
  task_10_implement_logs_elevation_and_runconfig --> gr_10_implement_logs_elevation_and_runconfig_0
  gr_10_implement_logs_elevation_and_runconfig_0 --> done_10_implement_logs_elevation_and_runconfig
  gr_10_implement_logs_elevation_and_runconfig_1["02-logs-runconfig-tests-pass"]:::guardrail
  task_10_implement_logs_elevation_and_runconfig --> gr_10_implement_logs_elevation_and_runconfig_1
  gr_10_implement_logs_elevation_and_runconfig_1 --> done_10_implement_logs_elevation_and_runconfig
  gr_10_implement_logs_elevation_and_runconfig_2["03-tests-untouched"]:::guardrail
  task_10_implement_logs_elevation_and_runconfig --> gr_10_implement_logs_elevation_and_runconfig_2
  gr_10_implement_logs_elevation_and_runconfig_2 --> done_10_implement_logs_elevation_and_runconfig
  done_10_implement_logs_elevation_and_runconfig["10-implement-logs-elevation-and-runconfig ✓ Finished"]:::done
  task_11_author_tests_writescope_matcher_truth_table_and_fuzz["11-author-tests-writescope-matcher-truth-table-and-fuzz"]:::task
  gr_11_author_tests_writescope_matcher_truth_table_and_fuzz_0["01-tests-fail-on-current-code"]:::guardrail
  task_11_author_tests_writescope_matcher_truth_table_and_fuzz --> gr_11_author_tests_writescope_matcher_truth_table_and_fuzz_0
  gr_11_author_tests_writescope_matcher_truth_table_and_fuzz_0 --> done_11_author_tests_writescope_matcher_truth_table_and_fuzz
  gr_11_author_tests_writescope_matcher_truth_table_and_fuzz_1["02-truth-table-rows-present"]:::guardrail
  task_11_author_tests_writescope_matcher_truth_table_and_fuzz --> gr_11_author_tests_writescope_matcher_truth_table_and_fuzz_1
  gr_11_author_tests_writescope_matcher_truth_table_and_fuzz_1 --> done_11_author_tests_writescope_matcher_truth_table_and_fuzz
  done_11_author_tests_writescope_matcher_truth_table_and_fuzz["11-author-tests-writescope-matcher-truth-table-and-fuzz ✓ Finished"]:::done
  task_12_implement_writescope_matcher["12-implement-writescope-matcher"]:::task
  gr_12_implement_writescope_matcher_0["01-core-builds"]:::guardrail
  task_12_implement_writescope_matcher --> gr_12_implement_writescope_matcher_0
  gr_12_implement_writescope_matcher_0 --> done_12_implement_writescope_matcher
  gr_12_implement_writescope_matcher_1["02-matcher-file-exists"]:::guardrail
  task_12_implement_writescope_matcher --> gr_12_implement_writescope_matcher_1
  gr_12_implement_writescope_matcher_1 --> done_12_implement_writescope_matcher
  gr_12_implement_writescope_matcher_2["03-matcher-tests-pass"]:::guardrail
  task_12_implement_writescope_matcher --> gr_12_implement_writescope_matcher_2
  gr_12_implement_writescope_matcher_2 --> done_12_implement_writescope_matcher
  gr_12_implement_writescope_matcher_3["04-tests-untouched"]:::guardrail
  task_12_implement_writescope_matcher --> gr_12_implement_writescope_matcher_3
  gr_12_implement_writescope_matcher_3 --> done_12_implement_writescope_matcher
  done_12_implement_writescope_matcher["12-implement-writescope-matcher ✓ Finished"]:::done
  task_13_author_tests_writescope_check["13-author-tests-writescope-check"]:::task
  gr_13_author_tests_writescope_check_0["01-tests-fail-on-current-code"]:::guardrail
  task_13_author_tests_writescope_check --> gr_13_author_tests_writescope_check_0
  gr_13_author_tests_writescope_check_0 --> done_13_author_tests_writescope_check
  gr_13_author_tests_writescope_check_1["02-check-scenarios-present"]:::guardrail
  task_13_author_tests_writescope_check --> gr_13_author_tests_writescope_check_1
  gr_13_author_tests_writescope_check_1 --> done_13_author_tests_writescope_check
  done_13_author_tests_writescope_check["13-author-tests-writescope-check ✓ Finished"]:::done
  task_14_implement_writescope_check_and_scoped_revert["14-implement-writescope-check-and-scoped-revert"]:::task
  gr_14_implement_writescope_check_and_scoped_revert_0["01-core-builds"]:::guardrail
  task_14_implement_writescope_check_and_scoped_revert --> gr_14_implement_writescope_check_and_scoped_revert_0
  gr_14_implement_writescope_check_and_scoped_revert_0 --> done_14_implement_writescope_check_and_scoped_revert
  gr_14_implement_writescope_check_and_scoped_revert_1["02-check-tests-pass"]:::guardrail
  task_14_implement_writescope_check_and_scoped_revert --> gr_14_implement_writescope_check_and_scoped_revert_1
  gr_14_implement_writescope_check_and_scoped_revert_1 --> done_14_implement_writescope_check_and_scoped_revert
  gr_14_implement_writescope_check_and_scoped_revert_2["03-tests-untouched"]:::guardrail
  task_14_implement_writescope_check_and_scoped_revert --> gr_14_implement_writescope_check_and_scoped_revert_2
  gr_14_implement_writescope_check_and_scoped_revert_2 --> done_14_implement_writescope_check_and_scoped_revert
  done_14_implement_writescope_check_and_scoped_revert["14-implement-writescope-check-and-scoped-revert ✓ Finished"]:::done
  task_15_author_tests_reverifier_seam["15-author-tests-reverifier-seam"]:::task
  gr_15_author_tests_reverifier_seam_0["01-tests-fail-on-current-code"]:::guardrail
  task_15_author_tests_reverifier_seam --> gr_15_author_tests_reverifier_seam_0
  gr_15_author_tests_reverifier_seam_0 --> done_15_author_tests_reverifier_seam
  gr_15_author_tests_reverifier_seam_1["02-reverifier-scenarios-present"]:::guardrail
  task_15_author_tests_reverifier_seam --> gr_15_author_tests_reverifier_seam_1
  gr_15_author_tests_reverifier_seam_1 --> done_15_author_tests_reverifier_seam
  done_15_author_tests_reverifier_seam["15-author-tests-reverifier-seam ✓ Finished"]:::done
  task_16_implement_reverifier_seam["16-implement-reverifier-seam"]:::task
  gr_16_implement_reverifier_seam_0["01-core-builds"]:::guardrail
  task_16_implement_reverifier_seam --> gr_16_implement_reverifier_seam_0
  gr_16_implement_reverifier_seam_0 --> done_16_implement_reverifier_seam
  gr_16_implement_reverifier_seam_1["02-reverifier-tests-pass"]:::guardrail
  task_16_implement_reverifier_seam --> gr_16_implement_reverifier_seam_1
  gr_16_implement_reverifier_seam_1 --> done_16_implement_reverifier_seam
  gr_16_implement_reverifier_seam_2["03-tests-untouched"]:::guardrail
  task_16_implement_reverifier_seam --> gr_16_implement_reverifier_seam_2
  gr_16_implement_reverifier_seam_2 --> done_16_implement_reverifier_seam
  done_16_implement_reverifier_seam["16-implement-reverifier-seam ✓ Finished"]:::done
  task_17_author_tests_merge_lock_and_ff_and_union_settle["17-author-tests-merge-lock-and-ff-and-union-settle"]:::task
  gr_17_author_tests_merge_lock_and_ff_and_union_settle_0["01-tests-fail-on-current-code"]:::guardrail
  task_17_author_tests_merge_lock_and_ff_and_union_settle --> gr_17_author_tests_merge_lock_and_ff_and_union_settle_0
  gr_17_author_tests_merge_lock_and_ff_and_union_settle_0 --> done_17_author_tests_merge_lock_and_ff_and_union_settle
  gr_17_author_tests_merge_lock_and_ff_and_union_settle_1["02-settle-scenarios-present"]:::guardrail
  task_17_author_tests_merge_lock_and_ff_and_union_settle --> gr_17_author_tests_merge_lock_and_ff_and_union_settle_1
  gr_17_author_tests_merge_lock_and_ff_and_union_settle_1 --> done_17_author_tests_merge_lock_and_ff_and_union_settle
  done_17_author_tests_merge_lock_and_ff_and_union_settle["17-author-tests-merge-lock-and-ff-and-union-settle ✓ Finished"]:::done
  task_18_implement_merge_lock_and_ff_and_union_settle["18-implement-merge-lock-and-ff-and-union-settle"]:::task
  gr_18_implement_merge_lock_and_ff_and_union_settle_0["01-core-builds"]:::guardrail
  task_18_implement_merge_lock_and_ff_and_union_settle --> gr_18_implement_merge_lock_and_ff_and_union_settle_0
  gr_18_implement_merge_lock_and_ff_and_union_settle_0 --> done_18_implement_merge_lock_and_ff_and_union_settle
  gr_18_implement_merge_lock_and_ff_and_union_settle_1["02-settle-tests-pass"]:::guardrail
  task_18_implement_merge_lock_and_ff_and_union_settle --> gr_18_implement_merge_lock_and_ff_and_union_settle_1
  gr_18_implement_merge_lock_and_ff_and_union_settle_1 --> done_18_implement_merge_lock_and_ff_and_union_settle
  gr_18_implement_merge_lock_and_ff_and_union_settle_2["03-tests-untouched"]:::guardrail
  task_18_implement_merge_lock_and_ff_and_union_settle --> gr_18_implement_merge_lock_and_ff_and_union_settle_2
  gr_18_implement_merge_lock_and_ff_and_union_settle_2 --> done_18_implement_merge_lock_and_ff_and_union_settle
  done_18_implement_merge_lock_and_ff_and_union_settle["18-implement-merge-lock-and-ff-and-union-settle ✓ Finished"]:::done
  task_19_author_tests_resume_and_reset_retry["19-author-tests-resume-and-reset-retry"]:::task
  gr_19_author_tests_resume_and_reset_retry_0["01-tests-fail-on-current-code"]:::guardrail
  task_19_author_tests_resume_and_reset_retry --> gr_19_author_tests_resume_and_reset_retry_0
  gr_19_author_tests_resume_and_reset_retry_0 --> done_19_author_tests_resume_and_reset_retry
  gr_19_author_tests_resume_and_reset_retry_1["02-resume-scenarios-present"]:::guardrail
  task_19_author_tests_resume_and_reset_retry --> gr_19_author_tests_resume_and_reset_retry_1
  gr_19_author_tests_resume_and_reset_retry_1 --> done_19_author_tests_resume_and_reset_retry
  done_19_author_tests_resume_and_reset_retry["19-author-tests-resume-and-reset-retry ✓ Finished"]:::done
  task_20_implement_resume_reconciliation_and_reset_retry["20-implement-resume-reconciliation-and-reset-retry"]:::task
  gr_20_implement_resume_reconciliation_and_reset_retry_0["01-core-builds"]:::guardrail
  task_20_implement_resume_reconciliation_and_reset_retry --> gr_20_implement_resume_reconciliation_and_reset_retry_0
  gr_20_implement_resume_reconciliation_and_reset_retry_0 --> done_20_implement_resume_reconciliation_and_reset_retry
  gr_20_implement_resume_reconciliation_and_reset_retry_1["02-resume-tests-pass"]:::guardrail
  task_20_implement_resume_reconciliation_and_reset_retry --> gr_20_implement_resume_reconciliation_and_reset_retry_1
  gr_20_implement_resume_reconciliation_and_reset_retry_1 --> done_20_implement_resume_reconciliation_and_reset_retry
  gr_20_implement_resume_reconciliation_and_reset_retry_2["03-tests-untouched"]:::guardrail
  task_20_implement_resume_reconciliation_and_reset_retry --> gr_20_implement_resume_reconciliation_and_reset_retry_2
  gr_20_implement_resume_reconciliation_and_reset_retry_2 --> done_20_implement_resume_reconciliation_and_reset_retry
  done_20_implement_resume_reconciliation_and_reset_retry["20-implement-resume-reconciliation-and-reset-retry ✓ Finished"]:::done
  task_21_author_tests_merge_on_success["21-author-tests-merge-on-success"]:::task
  gr_21_author_tests_merge_on_success_0["01-tests-fail-on-current-code"]:::guardrail
  task_21_author_tests_merge_on_success --> gr_21_author_tests_merge_on_success_0
  gr_21_author_tests_merge_on_success_0 --> done_21_author_tests_merge_on_success
  gr_21_author_tests_merge_on_success_1["02-merge-on-success-scenarios-present"]:::guardrail
  task_21_author_tests_merge_on_success --> gr_21_author_tests_merge_on_success_1
  gr_21_author_tests_merge_on_success_1 --> done_21_author_tests_merge_on_success
  done_21_author_tests_merge_on_success["21-author-tests-merge-on-success ✓ Finished"]:::done
  task_22_implement_merge_on_success["22-implement-merge-on-success"]:::task
  gr_22_implement_merge_on_success_0["01-cli-builds"]:::guardrail
  task_22_implement_merge_on_success --> gr_22_implement_merge_on_success_0
  gr_22_implement_merge_on_success_0 --> done_22_implement_merge_on_success
  gr_22_implement_merge_on_success_1["02-merge-on-success-tests-pass"]:::guardrail
  task_22_implement_merge_on_success --> gr_22_implement_merge_on_success_1
  gr_22_implement_merge_on_success_1 --> done_22_implement_merge_on_success
  gr_22_implement_merge_on_success_2["03-tests-untouched"]:::guardrail
  task_22_implement_merge_on_success --> gr_22_implement_merge_on_success_2
  gr_22_implement_merge_on_success_2 --> done_22_implement_merge_on_success
  done_22_implement_merge_on_success["22-implement-merge-on-success ✓ Finished"]:::done
  task_23_author_tests_guardrail_scope_field["23-author-tests-guardrail-scope-field"]:::task
  gr_23_author_tests_guardrail_scope_field_0["01-tests-fail-on-current-code"]:::guardrail
  task_23_author_tests_guardrail_scope_field --> gr_23_author_tests_guardrail_scope_field_0
  gr_23_author_tests_guardrail_scope_field_0 --> done_23_author_tests_guardrail_scope_field
  gr_23_author_tests_guardrail_scope_field_1["02-scope-scenarios-present"]:::guardrail
  task_23_author_tests_guardrail_scope_field --> gr_23_author_tests_guardrail_scope_field_1
  gr_23_author_tests_guardrail_scope_field_1 --> done_23_author_tests_guardrail_scope_field
  done_23_author_tests_guardrail_scope_field["23-author-tests-guardrail-scope-field ✓ Finished"]:::done
  task_24_implement_guardrail_scope_field["24-implement-guardrail-scope-field"]:::task
  gr_24_implement_guardrail_scope_field_0["01-core-builds"]:::guardrail
  task_24_implement_guardrail_scope_field --> gr_24_implement_guardrail_scope_field_0
  gr_24_implement_guardrail_scope_field_0 --> done_24_implement_guardrail_scope_field
  gr_24_implement_guardrail_scope_field_1["02-scope-tests-pass"]:::guardrail
  task_24_implement_guardrail_scope_field --> gr_24_implement_guardrail_scope_field_1
  gr_24_implement_guardrail_scope_field_1 --> done_24_implement_guardrail_scope_field
  gr_24_implement_guardrail_scope_field_2["03-tests-untouched"]:::guardrail
  task_24_implement_guardrail_scope_field --> gr_24_implement_guardrail_scope_field_2
  gr_24_implement_guardrail_scope_field_2 --> done_24_implement_guardrail_scope_field
  done_24_implement_guardrail_scope_field["24-implement-guardrail-scope-field ✓ Finished"]:::done
  task_25_author_tests_ai_merge_worker["25-author-tests-ai-merge-worker"]:::task
  gr_25_author_tests_ai_merge_worker_0["01-tests-fail-on-current-code"]:::guardrail
  task_25_author_tests_ai_merge_worker --> gr_25_author_tests_ai_merge_worker_0
  gr_25_author_tests_ai_merge_worker_0 --> done_25_author_tests_ai_merge_worker
  gr_25_author_tests_ai_merge_worker_1["02-ai-merge-scenarios-present"]:::guardrail
  task_25_author_tests_ai_merge_worker --> gr_25_author_tests_ai_merge_worker_1
  gr_25_author_tests_ai_merge_worker_1 --> done_25_author_tests_ai_merge_worker
  done_25_author_tests_ai_merge_worker["25-author-tests-ai-merge-worker ✓ Finished"]:::done
  task_26_implement_ai_merge_worker["26-implement-ai-merge-worker"]:::task
  gr_26_implement_ai_merge_worker_0["01-core-builds"]:::guardrail
  task_26_implement_ai_merge_worker --> gr_26_implement_ai_merge_worker_0
  gr_26_implement_ai_merge_worker_0 --> done_26_implement_ai_merge_worker
  gr_26_implement_ai_merge_worker_1["02-ai-merge-tests-pass"]:::guardrail
  task_26_implement_ai_merge_worker --> gr_26_implement_ai_merge_worker_1
  gr_26_implement_ai_merge_worker_1 --> done_26_implement_ai_merge_worker
  gr_26_implement_ai_merge_worker_2["03-tests-untouched"]:::guardrail
  task_26_implement_ai_merge_worker --> gr_26_implement_ai_merge_worker_2
  gr_26_implement_ai_merge_worker_2 --> done_26_implement_ai_merge_worker
  gr_26_implement_ai_merge_worker_3["04-deterministic-gates-present"]:::guardrail
  task_26_implement_ai_merge_worker --> gr_26_implement_ai_merge_worker_3
  gr_26_implement_ai_merge_worker_3 --> done_26_implement_ai_merge_worker
  done_26_implement_ai_merge_worker["26-implement-ai-merge-worker ✓ Finished"]:::done
  task_27_update_plan_breakdown_skill["27-update-plan-breakdown-skill"]:::task
  gr_27_update_plan_breakdown_skill_0["01-skill-emits-new-mechanisms"]:::guardrail
  task_27_update_plan_breakdown_skill --> gr_27_update_plan_breakdown_skill_0
  gr_27_update_plan_breakdown_skill_0 --> done_27_update_plan_breakdown_skill
  gr_27_update_plan_breakdown_skill_1["02-worked-example-uses-writescope-not-triad"]:::guardrail
  task_27_update_plan_breakdown_skill --> gr_27_update_plan_breakdown_skill_1
  gr_27_update_plan_breakdown_skill_1 --> done_27_update_plan_breakdown_skill
  done_27_update_plan_breakdown_skill["27-update-plan-breakdown-skill ✓ Finished"]:::done
  task_28_update_guardrails_review_skill["28-update-guardrails-review-skill"]:::task
  gr_28_update_guardrails_review_skill_0["01-review-skill-has-scope-probes"]:::guardrail
  task_28_update_guardrails_review_skill --> gr_28_update_guardrails_review_skill_0
  gr_28_update_guardrails_review_skill_0 --> done_28_update_guardrails_review_skill
  done_28_update_guardrails_review_skill["28-update-guardrails-review-skill ✓ Finished"]:::done
  task_29_update_domain_knowledge_skill["29-update-domain-knowledge-skill"]:::task
  gr_29_update_domain_knowledge_skill_0["01-domain-knowledge-updated"]:::guardrail
  task_29_update_domain_knowledge_skill --> gr_29_update_domain_knowledge_skill_0
  gr_29_update_domain_knowledge_skill_0 --> done_29_update_domain_knowledge_skill
  done_29_update_domain_knowledge_skill["29-update-domain-knowledge-skill ✓ Finished"]:::done
  task_30_build_solution_gate["30-build-solution-gate"]:::task
  gr_30_build_solution_gate_0["01-solution-builds"]:::guardrail
  task_30_build_solution_gate --> gr_30_build_solution_gate_0
  gr_30_build_solution_gate_0 --> done_30_build_solution_gate
  done_30_build_solution_gate["30-build-solution-gate ✓ Finished"]:::done
  task_31_full_suite_green_gate["31-full-suite-green-gate"]:::task
  gr_31_full_suite_green_gate_0["01-full-suite"]:::guardrail
  task_31_full_suite_green_gate --> gr_31_full_suite_green_gate_0
  gr_31_full_suite_green_gate_0 --> done_31_full_suite_green_gate
  done_31_full_suite_green_gate["31-full-suite-green-gate ✓ Finished"]:::done
  task_32_author_tests_needs_human_triage["32-author-tests-needs-human-triage"]:::task
  gr_32_author_tests_needs_human_triage_0["01-tests-fail-on-current-code"]:::guardrail
  task_32_author_tests_needs_human_triage --> gr_32_author_tests_needs_human_triage_0
  gr_32_author_tests_needs_human_triage_0 --> done_32_author_tests_needs_human_triage
  gr_32_author_tests_needs_human_triage_1["02-triage-scenarios-present"]:::guardrail
  task_32_author_tests_needs_human_triage --> gr_32_author_tests_needs_human_triage_1
  gr_32_author_tests_needs_human_triage_1 --> done_32_author_tests_needs_human_triage
  done_32_author_tests_needs_human_triage["32-author-tests-needs-human-triage ✓ Finished"]:::done
  task_33_implement_needs_human_triage["33-implement-needs-human-triage"]:::task
  gr_33_implement_needs_human_triage_0["01-core-builds"]:::guardrail
  task_33_implement_needs_human_triage --> gr_33_implement_needs_human_triage_0
  gr_33_implement_needs_human_triage_0 --> done_33_implement_needs_human_triage
  gr_33_implement_needs_human_triage_1["02-triage-class-exists"]:::guardrail
  task_33_implement_needs_human_triage --> gr_33_implement_needs_human_triage_1
  gr_33_implement_needs_human_triage_1 --> done_33_implement_needs_human_triage
  gr_33_implement_needs_human_triage_2["03-triage-tests-pass"]:::guardrail
  task_33_implement_needs_human_triage --> gr_33_implement_needs_human_triage_2
  gr_33_implement_needs_human_triage_2 --> done_33_implement_needs_human_triage
  gr_33_implement_needs_human_triage_3["04-tests-untouched"]:::guardrail
  task_33_implement_needs_human_triage --> gr_33_implement_needs_human_triage_3
  gr_33_implement_needs_human_triage_3 --> done_33_implement_needs_human_triage
  done_33_implement_needs_human_triage["33-implement-needs-human-triage ✓ Finished"]:::done
  done_01_update_ssot_schema_contract --> task_02_author_tests_worktree_provider_seam
  done_02_author_tests_worktree_provider_seam --> task_03_implement_worktree_provider_seam_and_channel_envelope
  done_03_implement_worktree_provider_seam_and_channel_envelope --> task_04_deserialize_scheduler_begin_triad_teardown
  done_04_deserialize_scheduler_begin_triad_teardown --> task_05_author_tests_git_worktree_lifecycle
  done_04_deserialize_scheduler_begin_triad_teardown --> task_07_author_tests_validation_gates_and_triad_removal
  done_04_deserialize_scheduler_begin_triad_teardown --> task_09_author_tests_logs_elevation_and_runconfig
  done_05_author_tests_git_worktree_lifecycle --> task_06_implement_git_worktree_provider_and_reuse_topology
  done_06_implement_git_worktree_provider_and_reuse_topology --> task_08_implement_validation_gates_and_triad_teardown
  done_06_implement_git_worktree_provider_and_reuse_topology --> task_10_implement_logs_elevation_and_runconfig
  done_07_author_tests_validation_gates_and_triad_removal --> task_08_implement_validation_gates_and_triad_teardown
  done_08_implement_validation_gates_and_triad_teardown --> task_11_author_tests_writescope_matcher_truth_table_and_fuzz
  done_09_author_tests_logs_elevation_and_runconfig --> task_10_implement_logs_elevation_and_runconfig
  done_10_implement_logs_elevation_and_runconfig --> task_15_author_tests_reverifier_seam
  done_10_implement_logs_elevation_and_runconfig --> task_32_author_tests_needs_human_triage
  done_11_author_tests_writescope_matcher_truth_table_and_fuzz --> task_12_implement_writescope_matcher
  done_12_implement_writescope_matcher --> task_13_author_tests_writescope_check
  done_13_author_tests_writescope_check --> task_14_implement_writescope_check_and_scoped_revert
  done_14_implement_writescope_check_and_scoped_revert --> task_15_author_tests_reverifier_seam
  done_15_author_tests_reverifier_seam --> task_16_implement_reverifier_seam
  done_16_implement_reverifier_seam --> task_17_author_tests_merge_lock_and_ff_and_union_settle
  done_16_implement_reverifier_seam --> task_23_author_tests_guardrail_scope_field
  done_17_author_tests_merge_lock_and_ff_and_union_settle --> task_18_implement_merge_lock_and_ff_and_union_settle
  done_18_implement_merge_lock_and_ff_and_union_settle --> task_19_author_tests_resume_and_reset_retry
  done_18_implement_merge_lock_and_ff_and_union_settle --> task_21_author_tests_merge_on_success
  done_19_author_tests_resume_and_reset_retry --> task_20_implement_resume_reconciliation_and_reset_retry
  done_20_implement_resume_reconciliation_and_reset_retry --> task_22_implement_merge_on_success
  done_21_author_tests_merge_on_success --> task_22_implement_merge_on_success
  done_22_implement_merge_on_success --> task_25_author_tests_ai_merge_worker
  done_23_author_tests_guardrail_scope_field --> task_24_implement_guardrail_scope_field
  done_24_implement_guardrail_scope_field --> task_25_author_tests_ai_merge_worker
  done_25_author_tests_ai_merge_worker --> task_26_implement_ai_merge_worker
  done_26_implement_ai_merge_worker --> task_27_update_plan_breakdown_skill
  done_26_implement_ai_merge_worker --> task_28_update_guardrails_review_skill
  done_26_implement_ai_merge_worker --> task_29_update_domain_knowledge_skill
  done_26_implement_ai_merge_worker --> task_30_build_solution_gate
  done_26_implement_ai_merge_worker --> task_33_implement_needs_human_triage
  done_27_update_plan_breakdown_skill --> task_31_full_suite_green_gate
  done_28_update_guardrails_review_skill --> task_31_full_suite_green_gate
  done_29_update_domain_knowledge_skill --> task_31_full_suite_green_gate
  done_30_build_solution_gate --> task_31_full_suite_green_gate
  done_32_author_tests_needs_human_triage --> task_33_implement_needs_human_triage
  done_33_implement_needs_human_triage --> task_30_build_solution_gate
  done_33_implement_needs_human_triage --> task_31_full_suite_green_gate
  classDef task fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
  classDef done fill:#d4edda,stroke:#2e7d32,color:#10341a;
```

_Structure only — retry, feedback, and needs-human edges are omitted._
