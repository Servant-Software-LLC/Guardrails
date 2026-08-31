<!-- guardrails:graph v1 source-sha256=753c0b3d147be01051cdd4028e5ada051292fd49bc3a60efd6c90d75609a7b63 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_escalation_salvage["01-author-tests-escalation-salvage"]
    task_01_author_tests_escalation_salvage_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_escalation_salvage_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_01_author_tests_escalation_salvage_gr_2["03-no-new-api-named"]:::guardrail
  end
  style task_01_author_tests_escalation_salvage fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_preserve_on_escalation_path["02-preserve-on-escalation-path"]
    task_02_preserve_on_escalation_path_gr_0["01-build-passes"]:::guardrail
    task_02_preserve_on_escalation_path_gr_1["02-escalation-preserve-tests-pass"]:::guardrail
    task_02_preserve_on_escalation_path_gr_2["03-shipped-salvage-suites-unmoved"]:::guardrail
    task_02_preserve_on_escalation_path_gr_3["04-salvage-section-is-internal"]:::guardrail
  end
  style task_02_preserve_on_escalation_path fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_carry_salvage_forward_to_prompts["03-carry-salvage-forward-to-prompts"]
    task_03_carry_salvage_forward_to_prompts_gr_0["01-build-passes"]:::guardrail
    task_03_carry_salvage_forward_to_prompts_gr_1["02-forward-carry-tests-pass"]:::guardrail
    task_03_carry_salvage_forward_to_prompts_gr_2["03-salvage-text-has-one-owner"]:::guardrail
  end
  style task_03_carry_salvage_forward_to_prompts fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_author_tests_handoff_coverage["04-author-tests-handoff-coverage"]
    task_04_author_tests_handoff_coverage_gr_0["01-build-passes"]:::guardrail
    task_04_author_tests_handoff_coverage_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_04_author_tests_handoff_coverage_gr_2["03-pins-key-the-right-codes"]:::guardrail
  end
  style task_04_author_tests_handoff_coverage fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_implement_handoff_coverage_check["05-implement-handoff-coverage-check"]
    task_05_implement_handoff_coverage_check_gr_0["01-build-passes"]:::guardrail
    task_05_implement_handoff_coverage_check_gr_1["02-handoff-coverage-tests-pass"]:::guardrail
    task_05_implement_handoff_coverage_check_gr_2["03-no-second-glob-matcher"]:::guardrail
    task_05_implement_handoff_coverage_check_gr_3["04-codes-and-marker"]:::guardrail
  end
  style task_05_implement_handoff_coverage_check fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_stub_the_plan_edit_watch["06-stub-the-plan-edit-watch"]
    task_06_stub_the_plan_edit_watch_gr_0["01-build-passes"]:::guardrail
    task_06_stub_the_plan_edit_watch_gr_1["02-stubs-declared-and-inert"]:::guardrail
  end
  style task_06_stub_the_plan_edit_watch fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_plan_edit_watch["07-author-tests-plan-edit-watch"]
    task_07_author_tests_plan_edit_watch_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_plan_edit_watch_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_07_author_tests_plan_edit_watch fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_implement_the_plan_edit_watch["08-implement-the-plan-edit-watch"]
    task_08_implement_the_plan_edit_watch_gr_0["01-build-passes"]:::guardrail
    task_08_implement_the_plan_edit_watch_gr_1["02-watch-unit-tests-pass"]:::guardrail
  end
  style task_08_implement_the_plan_edit_watch fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_wire_the_plan_edit_watch["09-wire-the-plan-edit-watch"]
    task_09_wire_the_plan_edit_watch_gr_0["01-build-passes"]:::guardrail
    task_09_wire_the_plan_edit_watch_gr_1["02-plan-edit-run-tests-pass"]:::guardrail
    task_09_wire_the_plan_edit_watch_gr_2["03-watch-is-wired"]:::guardrail
  end
  style task_09_wire_the_plan_edit_watch fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_record_in_ssot_and_skills["10-record-in-ssot-and-skills"]
    task_10_record_in_ssot_and_skills_gr_0["01-contract-moved-with-the-code"]:::guardrail
  end
  style task_10_record_in_ssot_and_skills fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-suite-passes"]:::guardrail
    plan_guardrails_2["03-integration-suite-passes"]:::guardrail
    plan_guardrails_3["04-union-artifacts-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_escalation_salvage
  plan_preflights --> task_04_author_tests_handoff_coverage
  plan_preflights --> task_06_stub_the_plan_edit_watch
  task_01_author_tests_escalation_salvage --> task_02_preserve_on_escalation_path
  task_02_preserve_on_escalation_path --> task_03_carry_salvage_forward_to_prompts
  task_03_carry_salvage_forward_to_prompts --> task_09_wire_the_plan_edit_watch
  task_03_carry_salvage_forward_to_prompts --> task_10_record_in_ssot_and_skills
  task_04_author_tests_handoff_coverage --> task_05_implement_handoff_coverage_check
  task_05_implement_handoff_coverage_check --> task_10_record_in_ssot_and_skills
  task_06_stub_the_plan_edit_watch --> task_07_author_tests_plan_edit_watch
  task_07_author_tests_plan_edit_watch --> task_08_implement_the_plan_edit_watch
  task_08_implement_the_plan_edit_watch --> task_09_wire_the_plan_edit_watch
  task_09_wire_the_plan_edit_watch --> task_10_record_in_ssot_and_skills
  task_10_record_in_ssot_and_skills --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
