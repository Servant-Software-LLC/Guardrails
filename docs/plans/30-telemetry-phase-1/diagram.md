<!-- guardrails:graph v1 source-sha256=b5db13b8c6460ec88978cf1340c783676f80add6879661d07c471ee0b3f00524 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_bucket_classifier["01-author-tests-bucket-classifier"]
    task_01_author_tests_bucket_classifier_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_bucket_classifier_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_bucket_classifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_bucket_classifier["02-implement-bucket-classifier"]
    task_02_implement_bucket_classifier_gr_0["01-build-passes"]:::guardrail
    task_02_implement_bucket_classifier_gr_1["02-bucket-tests-pass"]:::guardrail
  end
  style task_02_implement_bucket_classifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_extend_the_journal_record_shape["03-extend-the-journal-record-shape"]
    task_03_extend_the_journal_record_shape_gr_0["01-build-passes"]:::guardrail
    task_03_extend_the_journal_record_shape_gr_1["02-journal-shape-census-passes"]:::guardrail
  end
  style task_03_extend_the_journal_record_shape fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_extend_the_transport_record_shape["04-extend-the-transport-record-shape"]
    task_04_extend_the_transport_record_shape_gr_0["01-build-passes"]:::guardrail
    task_04_extend_the_transport_record_shape_gr_1["02-transport-shape-census-passes"]:::guardrail
  end
  style task_04_extend_the_transport_record_shape fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04a_extend_the_corpus_row_shape["04a-extend-the-corpus-row-shape"]
    task_04a_extend_the_corpus_row_shape_gr_0["01-build-passes"]:::guardrail
    task_04a_extend_the_corpus_row_shape_gr_1["02-row-shape-census-passes"]:::guardrail
  end
  style task_04a_extend_the_corpus_row_shape fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_bucket_journaled["05-author-tests-bucket-journaled"]
    task_05_author_tests_bucket_journaled_gr_0["01-build-passes"]:::guardrail
    task_05_author_tests_bucket_journaled_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_05_author_tests_bucket_journaled fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_journal_the_bucket_serial["06-journal-the-bucket-serial"]
    task_06_journal_the_bucket_serial_gr_0["01-build-passes"]:::guardrail
    task_06_journal_the_bucket_serial_gr_1["02-bucket-journal-tests-pass"]:::guardrail
  end
  style task_06_journal_the_bucket_serial fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_model_digest_capture["07-author-tests-model-digest-capture"]
    task_07_author_tests_model_digest_capture_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_model_digest_capture_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_07_author_tests_model_digest_capture fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_capture_the_model_digest_from_the_wire["08-capture-the-model-digest-from-the-wire"]
    task_08_capture_the_model_digest_from_the_wire_gr_0["01-build-passes"]:::guardrail
    task_08_capture_the_model_digest_from_the_wire_gr_1["02-digest-capture-tests-pass"]:::guardrail
  end
  style task_08_capture_the_model_digest_from_the_wire fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_author_tests_digest_reaches_the_provenance["09-author-tests-digest-reaches-the-provenance"]
    task_09_author_tests_digest_reaches_the_provenance_gr_0["01-build-passes"]:::guardrail
    task_09_author_tests_digest_reaches_the_provenance_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_09_author_tests_digest_reaches_the_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_fold_the_digest_into_the_provenance["10-fold-the-digest-into-the-provenance"]
    task_10_fold_the_digest_into_the_provenance_gr_0["01-build-passes"]:::guardrail
    task_10_fold_the_digest_into_the_provenance_gr_1["02-digest-provenance-tests-pass"]:::guardrail
  end
  style task_10_fold_the_digest_into_the_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_author_tests_attempt_envelope["11-author-tests-attempt-envelope"]
    task_11_author_tests_attempt_envelope_gr_0["01-build-passes"]:::guardrail
    task_11_author_tests_attempt_envelope_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_11_author_tests_attempt_envelope fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_record_the_turn_count["12-record-the-turn-count"]
    task_12_record_the_turn_count_gr_0["01-build-passes"]:::guardrail
    task_12_record_the_turn_count_gr_1["02-attempt-turns-tests-pass"]:::guardrail
  end
  style task_12_record_the_turn_count fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12a_segment_the_attempt_durations["12a-segment-the-attempt-durations"]
    task_12a_segment_the_attempt_durations_gr_0["01-build-passes"]:::guardrail
    task_12a_segment_the_attempt_durations_gr_1["02-attempt-segments-tests-pass"]:::guardrail
  end
  style task_12a_segment_the_attempt_durations fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_author_tests_route_warmth["13-author-tests-route-warmth"]
    task_13_author_tests_route_warmth_gr_0["01-build-passes"]:::guardrail
    task_13_author_tests_route_warmth_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_13_author_tests_route_warmth fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_record_whether_the_route_was_warm["14-record-whether-the-route-was-warm"]
    task_14_record_whether_the_route_was_warm_gr_0["01-build-passes"]:::guardrail
    task_14_record_whether_the_route_was_warm_gr_1["02-route-warmth-tests-pass"]:::guardrail
  end
  style task_14_record_whether_the_route_was_warm fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_author_tests_worktree_settle_carries_phase1["15-author-tests-worktree-settle-carries-phase1"]
    task_15_author_tests_worktree_settle_carries_phase1_gr_0["01-build-passes"]:::guardrail
    task_15_author_tests_worktree_settle_carries_phase1_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_15_author_tests_worktree_settle_carries_phase1 fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_16_carry_phase1_facts_through_the_worktree_settle["16-carry-phase1-facts-through-the-worktree-settle"]
    task_16_carry_phase1_facts_through_the_worktree_settle_gr_0["01-build-passes"]:::guardrail
    task_16_carry_phase1_facts_through_the_worktree_settle_gr_1["02-worktree-settle-tests-pass"]:::guardrail
    task_16_carry_phase1_facts_through_the_worktree_settle_gr_2["03-both-settle-records-set-every-phase1-member"]:::guardrail
  end
  style task_16_carry_phase1_facts_through_the_worktree_settle fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_17_author_tests_run_environment["17-author-tests-run-environment"]
    task_17_author_tests_run_environment_gr_0["01-build-passes"]:::guardrail
    task_17_author_tests_run_environment_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_17_author_tests_run_environment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_18_record_the_run_environment["18-record-the-run-environment"]
    task_18_record_the_run_environment_gr_0["01-build-passes"]:::guardrail
    task_18_record_the_run_environment_gr_1["02-run-environment-tests-pass"]:::guardrail
  end
  style task_18_record_the_run_environment fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_19_author_tests_row_carries_phase1_facts["19-author-tests-row-carries-phase1-facts"]
    task_19_author_tests_row_carries_phase1_facts_gr_0["01-build-passes"]:::guardrail
    task_19_author_tests_row_carries_phase1_facts_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_19_author_tests_row_carries_phase1_facts fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_20_carry_phase1_facts_into_the_corpus_row["20-carry-phase1-facts-into-the-corpus-row"]
    task_20_carry_phase1_facts_into_the_corpus_row_gr_0["01-build-passes"]:::guardrail
    task_20_carry_phase1_facts_into_the_corpus_row_gr_1["02-phase1-row-tests-pass"]:::guardrail
  end
  style task_20_carry_phase1_facts_into_the_corpus_row fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_21_author_tests_report_and_era_boundary["21-author-tests-report-and-era-boundary"]
    task_21_author_tests_report_and_era_boundary_gr_0["01-build-passes"]:::guardrail
    task_21_author_tests_report_and_era_boundary_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_21_author_tests_report_and_era_boundary fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_22_render_the_bucket_digest_and_era_boundary["22-render-the-bucket-digest-and-era-boundary"]
    task_22_render_the_bucket_digest_and_era_boundary_gr_0["01-build-passes"]:::guardrail
    task_22_render_the_bucket_digest_and_era_boundary_gr_1["02-report-phase1-tests-pass"]:::guardrail
  end
  style task_22_render_the_bucket_digest_and_era_boundary fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_23_author_tests_attribution_census["23-author-tests-attribution-census"]
    task_23_author_tests_attribution_census_gr_0["01-build-passes"]:::guardrail
    task_23_author_tests_attribution_census_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_23_author_tests_attribution_census fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_24_implement_the_attribution_census["24-implement-the-attribution-census"]
    task_24_implement_the_attribution_census_gr_0["01-build-passes"]:::guardrail
    task_24_implement_the_attribution_census_gr_1["02-census-tests-pass"]:::guardrail
  end
  style task_24_implement_the_attribution_census fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_25_record_in_ssot_and_skills["25-record-in-ssot-and-skills"]
    task_25_record_in_ssot_and_skills_gr_0["01-ssot-carries-the-contract"]:::guardrail
    task_25_record_in_ssot_and_skills_gr_1["02-skill-carries-the-execution-semantics"]:::guardrail
  end
  style task_25_record_in_ssot_and_skills fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-suite-passes"]:::guardrail
    plan_guardrails_2["03-integration-suite-passes"]:::guardrail
    plan_guardrails_3["04-union-artifacts-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_bucket_classifier
  plan_preflights --> task_03_extend_the_journal_record_shape
  plan_preflights --> task_04a_extend_the_corpus_row_shape
  task_01_author_tests_bucket_classifier --> task_02_implement_bucket_classifier
  task_02_implement_bucket_classifier --> task_05_author_tests_bucket_journaled
  task_03_extend_the_journal_record_shape --> task_04_extend_the_transport_record_shape
  task_03_extend_the_journal_record_shape --> task_05_author_tests_bucket_journaled
  task_03_extend_the_journal_record_shape --> task_09_author_tests_digest_reaches_the_provenance
  task_03_extend_the_journal_record_shape --> task_11_author_tests_attempt_envelope
  task_03_extend_the_journal_record_shape --> task_13_author_tests_route_warmth
  task_03_extend_the_journal_record_shape --> task_15_author_tests_worktree_settle_carries_phase1
  task_03_extend_the_journal_record_shape --> task_17_author_tests_run_environment
  task_03_extend_the_journal_record_shape --> task_19_author_tests_row_carries_phase1_facts
  task_03_extend_the_journal_record_shape --> task_23_author_tests_attribution_census
  task_04_extend_the_transport_record_shape --> task_07_author_tests_model_digest_capture
  task_04_extend_the_transport_record_shape --> task_09_author_tests_digest_reaches_the_provenance
  task_04_extend_the_transport_record_shape --> task_11_author_tests_attempt_envelope
  task_04_extend_the_transport_record_shape --> task_15_author_tests_worktree_settle_carries_phase1
  task_04a_extend_the_corpus_row_shape --> task_19_author_tests_row_carries_phase1_facts
  task_04a_extend_the_corpus_row_shape --> task_21_author_tests_report_and_era_boundary
  task_05_author_tests_bucket_journaled --> task_06_journal_the_bucket_serial
  task_06_journal_the_bucket_serial --> task_12_record_the_turn_count
  task_06_journal_the_bucket_serial --> task_18_record_the_run_environment
  task_06_journal_the_bucket_serial --> task_25_record_in_ssot_and_skills
  task_07_author_tests_model_digest_capture --> task_08_capture_the_model_digest_from_the_wire
  task_08_capture_the_model_digest_from_the_wire --> task_10_fold_the_digest_into_the_provenance
  task_09_author_tests_digest_reaches_the_provenance --> task_10_fold_the_digest_into_the_provenance
  task_10_fold_the_digest_into_the_provenance --> task_12_record_the_turn_count
  task_10_fold_the_digest_into_the_provenance --> task_25_record_in_ssot_and_skills
  task_11_author_tests_attempt_envelope --> task_12_record_the_turn_count
  task_12_record_the_turn_count --> task_12a_segment_the_attempt_durations
  task_12_record_the_turn_count --> task_25_record_in_ssot_and_skills
  task_12a_segment_the_attempt_durations --> task_14_record_whether_the_route_was_warm
  task_12a_segment_the_attempt_durations --> task_16_carry_phase1_facts_through_the_worktree_settle
  task_12a_segment_the_attempt_durations --> task_25_record_in_ssot_and_skills
  task_13_author_tests_route_warmth --> task_14_record_whether_the_route_was_warm
  task_14_record_whether_the_route_was_warm --> task_16_carry_phase1_facts_through_the_worktree_settle
  task_14_record_whether_the_route_was_warm --> task_25_record_in_ssot_and_skills
  task_15_author_tests_worktree_settle_carries_phase1 --> task_16_carry_phase1_facts_through_the_worktree_settle
  task_16_carry_phase1_facts_through_the_worktree_settle --> task_18_record_the_run_environment
  task_16_carry_phase1_facts_through_the_worktree_settle --> task_25_record_in_ssot_and_skills
  task_17_author_tests_run_environment --> task_18_record_the_run_environment
  task_18_record_the_run_environment --> task_25_record_in_ssot_and_skills
  task_19_author_tests_row_carries_phase1_facts --> task_20_carry_phase1_facts_into_the_corpus_row
  task_20_carry_phase1_facts_into_the_corpus_row --> task_25_record_in_ssot_and_skills
  task_21_author_tests_report_and_era_boundary --> task_22_render_the_bucket_digest_and_era_boundary
  task_22_render_the_bucket_digest_and_era_boundary --> task_24_implement_the_attribution_census
  task_22_render_the_bucket_digest_and_era_boundary --> task_25_record_in_ssot_and_skills
  task_23_author_tests_attribution_census --> task_24_implement_the_attribution_census
  task_24_implement_the_attribution_census --> task_25_record_in_ssot_and_skills
  task_25_record_in_ssot_and_skills --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
