<!-- guardrails:graph v1 source-sha256=3e8174358e54b0ab4ccbeb95bd249b702b1e3809e9aaa863f42d9ad81ec03cfa -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_executed_hash["01-author-tests-executed-hash"]
    task_01_author_tests_executed_hash_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_executed_hash_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_executed_hash fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_rebaseline_plan_edit_hash_assertions["02-rebaseline-plan-edit-hash-assertions"]
    task_02_rebaseline_plan_edit_hash_assertions_gr_0["01-build-passes"]:::guardrail
    task_02_rebaseline_plan_edit_hash_assertions_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_02_rebaseline_plan_edit_hash_assertions_gr_2["03-file-shape-preserved"]:::guardrail
  end
  style task_02_rebaseline_plan_edit_hash_assertions fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_pin_the_definition_at_load["03-pin-the-definition-at-load"]
    task_03_pin_the_definition_at_load_gr_0["01-build-passes"]:::guardrail
    task_03_pin_the_definition_at_load_gr_1["02-captures-are-bodiless-autoproperties"]:::guardrail
    task_03_pin_the_definition_at_load_gr_2["03-captures-are-set-eagerly-at-load"]:::guardrail
    task_03_pin_the_definition_at_load_gr_3["04-shipped-hash-suites-still-pass"]:::guardrail
  end
  style task_03_pin_the_definition_at_load fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_stamp_the_pin_serial_and_revalidate["04-stamp-the-pin-serial-and-revalidate"]
    task_04_stamp_the_pin_serial_and_revalidate_gr_0["01-build-passes"]:::guardrail
    task_04_stamp_the_pin_serial_and_revalidate_gr_1["02-executed-hash-tests-pass"]:::guardrail
    task_04_stamp_the_pin_serial_and_revalidate_gr_2["03-no-disk-fallback-at-the-serial-sites"]:::guardrail
  end
  style task_04_stamp_the_pin_serial_and_revalidate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_stamp_the_pin_worktree["05-stamp-the-pin-worktree"]
    task_05_stamp_the_pin_worktree_gr_0["01-build-passes"]:::guardrail
    task_05_stamp_the_pin_worktree_gr_1["02-mid-run-edit-tests-pass"]:::guardrail
    task_05_stamp_the_pin_worktree_gr_2["03-writes-read-the-pin-reads-read-disk"]:::guardrail
    task_05_stamp_the_pin_worktree_gr_3["04-ignore-predicate-has-one-home"]:::guardrail
  end
  style task_05_stamp_the_pin_worktree fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_author_anchor_tests_hash_sites["06-author-anchor-tests-hash-sites"]
    task_06_author_anchor_tests_hash_sites_gr_0["01-build-passes"]:::guardrail
    task_06_author_anchor_tests_hash_sites_gr_1["02-anchor-tests-pass"]:::guardrail
    task_06_author_anchor_tests_hash_sites_gr_2["03-anchor-enumerates-the-set-not-a-count"]:::guardrail
  end
  style task_06_author_anchor_tests_hash_sites fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_mid_run_definition_edit["07-author-tests-mid-run-definition-edit"]
    task_07_author_tests_mid_run_definition_edit_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_mid_run_definition_edit_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_07_author_tests_mid_run_definition_edit fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_author_tests_wave_executed_hash["08-author-tests-wave-executed-hash"]
    task_08_author_tests_wave_executed_hash_gr_0["01-build-passes"]:::guardrail
    task_08_author_tests_wave_executed_hash_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_08_author_tests_wave_executed_hash fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_pin_the_wave_twin["09-pin-the-wave-twin"]
    task_09_pin_the_wave_twin_gr_0["01-build-passes"]:::guardrail
    task_09_pin_the_wave_twin_gr_1["02-wave-pin-tests-pass"]:::guardrail
    task_09_pin_the_wave_twin_gr_2["03-pinned-fold-lands-beside-the-disk-form"]:::guardrail
  end
  style task_09_pin_the_wave_twin fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_author_tests_divergence_record["10-author-tests-divergence-record"]
    task_10_author_tests_divergence_record_gr_0["01-build-passes"]:::guardrail
    task_10_author_tests_divergence_record_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_10_author_tests_divergence_record fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_author_tests_delivery_gate["11-author-tests-delivery-gate"]
    task_11_author_tests_delivery_gate_gr_0["01-build-passes"]:::guardrail
    task_11_author_tests_delivery_gate_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_11_author_tests_delivery_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_add_the_divergence_record["12-add-the-divergence-record"]
    task_12_add_the_divergence_record_gr_0["01-build-passes"]:::guardrail
    task_12_add_the_divergence_record_gr_1["02-record-is-additive"]:::guardrail
  end
  style task_12_add_the_divergence_record fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_add_the_divergence_gate["13-add-the-divergence-gate"]
    task_13_add_the_divergence_gate_gr_0["01-build-passes"]:::guardrail
    task_13_add_the_divergence_gate_gr_1["02-divergence-tests-pass"]:::guardrail
    task_13_add_the_divergence_gate_gr_2["03-gate-uses-the-one-shared-predicate"]:::guardrail
    task_13_add_the_divergence_gate_gr_3["04-one-delivery-term-no-second-path"]:::guardrail
  end
  style task_13_add_the_divergence_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_rebaseline_advisory_assertions["14-rebaseline-advisory-assertions"]
    task_14_rebaseline_advisory_assertions_gr_0["01-build-passes"]:::guardrail
    task_14_rebaseline_advisory_assertions_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_14_rebaseline_advisory_assertions_gr_2["03-file-shape-preserved"]:::guardrail
  end
  style task_14_rebaseline_advisory_assertions fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_render_the_divergence_halt["15-render-the-divergence-halt"]
    task_15_render_the_divergence_halt_gr_0["01-build-passes"]:::guardrail
    task_15_render_the_divergence_halt_gr_1["02-full-suites-pass"]:::guardrail
    task_15_render_the_divergence_halt_gr_2["03-accept-is-refused-for-divergence"]:::guardrail
  end
  style task_15_render_the_divergence_halt fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_16_record_in_ssot_and_skills["16-record-in-ssot-and-skills"]
    task_16_record_in_ssot_and_skills_gr_0["01-ssot-carries-the-contract"]:::guardrail
    task_16_record_in_ssot_and_skills_gr_1["02-skill-carries-the-execution-semantics"]:::guardrail
  end
  style task_16_record_in_ssot_and_skills fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_17_rebaseline_wave_resume_fixture["17-rebaseline-wave-resume-fixture"]
    task_17_rebaseline_wave_resume_fixture_gr_0["01-build-passes"]:::guardrail
    task_17_rebaseline_wave_resume_fixture_gr_1["02-run-two-loads-its-own-plan"]:::guardrail
    task_17_rebaseline_wave_resume_fixture_gr_2["03-wave-resume-tests-still-pass"]:::guardrail
  end
  style task_17_rebaseline_wave_resume_fixture fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-suite-passes"]:::guardrail
    plan_guardrails_2["03-integration-suite-passes"]:::guardrail
    plan_guardrails_3["04-union-artifacts-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_executed_hash
  plan_preflights --> task_02_rebaseline_plan_edit_hash_assertions
  plan_preflights --> task_03_pin_the_definition_at_load
  plan_preflights --> task_17_rebaseline_wave_resume_fixture
  task_01_author_tests_executed_hash --> task_04_stamp_the_pin_serial_and_revalidate
  task_02_rebaseline_plan_edit_hash_assertions --> task_04_stamp_the_pin_serial_and_revalidate
  task_03_pin_the_definition_at_load --> task_04_stamp_the_pin_serial_and_revalidate
  task_03_pin_the_definition_at_load --> task_05_stamp_the_pin_worktree
  task_04_stamp_the_pin_serial_and_revalidate --> task_07_author_tests_mid_run_definition_edit
  task_05_stamp_the_pin_worktree --> task_06_author_anchor_tests_hash_sites
  task_05_stamp_the_pin_worktree --> task_08_author_tests_wave_executed_hash
  task_05_stamp_the_pin_worktree --> task_10_author_tests_divergence_record
  task_05_stamp_the_pin_worktree --> task_11_author_tests_delivery_gate
  task_06_author_anchor_tests_hash_sites --> task_09_pin_the_wave_twin
  task_07_author_tests_mid_run_definition_edit --> task_05_stamp_the_pin_worktree
  task_08_author_tests_wave_executed_hash --> task_09_pin_the_wave_twin
  task_09_pin_the_wave_twin --> task_13_add_the_divergence_gate
  task_10_author_tests_divergence_record --> task_12_add_the_divergence_record
  task_11_author_tests_delivery_gate --> task_13_add_the_divergence_gate
  task_12_add_the_divergence_record --> task_13_add_the_divergence_gate
  task_13_add_the_divergence_gate --> task_14_rebaseline_advisory_assertions
  task_14_rebaseline_advisory_assertions --> task_15_render_the_divergence_halt
  task_15_render_the_divergence_halt --> task_16_record_in_ssot_and_skills
  task_17_rebaseline_wave_resume_fixture --> task_13_add_the_divergence_gate
  task_16_record_in_ssot_and_skills --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
