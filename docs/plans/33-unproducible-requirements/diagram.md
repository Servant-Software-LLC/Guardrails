<!-- guardrails:graph v1 source-sha256=db980f634fb63750e558c4a5672c682b1f0df0283a2edaea0aa4129e4ec5f6d6 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_lift_guardrail_clause_text["01-lift-guardrail-clause-text"]
    task_01_lift_guardrail_clause_text_gr_0["01-build-passes"]:::guardrail
    task_01_lift_guardrail_clause_text_gr_1["02-gr2057-tests-pass"]:::guardrail
    task_01_lift_guardrail_clause_text_gr_2["03-single-quote-rule-intact"]:::guardrail
  end
  style task_01_lift_guardrail_clause_text fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_add_git_tracked_file_probe["02-add-git-tracked-file-probe"]
    task_02_add_git_tracked_file_probe_gr_0["01-build-passes"]:::guardrail
    task_02_add_git_tracked_file_probe_gr_1["02-call-sites-intact"]:::guardrail
    task_02_add_git_tracked_file_probe_gr_2["03-probe-contract"]:::guardrail
  end
  style task_02_add_git_tracked_file_probe fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_producer_coverage["03-author-tests-producer-coverage"]
    task_03_author_tests_producer_coverage_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_03_author_tests_producer_coverage_gr_1["02-covers-key-behaviors"]:::guardrail
    task_03_author_tests_producer_coverage_gr_2["03-constructed-fixture-labelled"]:::guardrail
  end
  style task_03_author_tests_producer_coverage fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_gr2060["04-implement-gr2060"]
    task_04_implement_gr2060_gr_0["01-build-passes"]:::guardrail
    task_04_implement_gr2060_gr_1["02-tests-pass"]:::guardrail
    task_04_implement_gr2060_gr_2["03-validate-own-plan-folder"]:::guardrail
    task_04_implement_gr2060_gr_3["04-gr2070-not-allocated"]:::guardrail
  end
  style task_04_implement_gr2060 fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_jit_prefix_veto["05-author-tests-jit-prefix-veto"]
    task_05_author_tests_jit_prefix_veto_gr_0["01-tests-fail-on-current-code"]:::guardrail
    task_05_author_tests_jit_prefix_veto_gr_1["02-covers-key-behaviors"]:::guardrail
  end
  style task_05_author_tests_jit_prefix_veto fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_excuse_gr2060_at_jit_gate["06-excuse-gr2060-at-jit-gate"]
    task_06_excuse_gr2060_at_jit_gate_gr_0["01-build-passes"]:::guardrail
    task_06_excuse_gr2060_at_jit_gate_gr_1["02-tests-pass"]:::guardrail
    task_06_excuse_gr2060_at_jit_gate_gr_2["03-keyed-on-wave-prefix"]:::guardrail
  end
  style task_06_excuse_gr2060_at_jit_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_ssot_producer_coverage_section["07-ssot-producer-coverage-section"]
    task_07_ssot_producer_coverage_section_gr_0["01-ssot-section-4-8"]:::guardrail
  end
  style task_07_ssot_producer_coverage_section fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_advance_the_code_ladder["08-advance-the-code-ladder"]
    task_08_advance_the_code_ladder_gr_0["01-code-ladder-advanced"]:::guardrail
  end
  style task_08_advance_the_code_ladder fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_author_corpus_sweep["09-author-corpus-sweep"]
    task_09_author_corpus_sweep_gr_0["01-build-passes"]:::guardrail
    task_09_author_corpus_sweep_gr_1["02-sweep-population"]:::guardrail
    task_09_author_corpus_sweep_gr_2["03-tests-pass"]:::guardrail
  end
  style task_09_author_corpus_sweep fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_add_callee_parameter_list_step["10-add-callee-parameter-list-step"]
    task_10_add_callee_parameter_list_step_gr_0["01-callee-step-present"]:::guardrail
  end
  style task_10_add_callee_parameter_list_step fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_record_gr2060_in_knowledge_skill["11-record-gr2060-in-knowledge-skill"]
    task_11_record_gr2060_in_knowledge_skill_gr_0["01-knowledge-recorded"]:::guardrail
  end
  style task_11_record_gr2060_in_knowledge_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_update_doc_19_status["12-update-doc-19-status"]
    task_12_update_doc_19_status_gr_0["01-doc19-updated"]:::guardrail
  end
  style task_12_update_doc_19_status fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-suite-passes"]:::guardrail
    plan_guardrails_2["03-union-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_lift_guardrail_clause_text
  task_01_lift_guardrail_clause_text --> task_02_add_git_tracked_file_probe
  task_02_add_git_tracked_file_probe --> task_03_author_tests_producer_coverage
  task_03_author_tests_producer_coverage --> task_04_implement_gr2060
  task_04_implement_gr2060 --> task_05_author_tests_jit_prefix_veto
  task_04_implement_gr2060 --> task_10_add_callee_parameter_list_step
  task_05_author_tests_jit_prefix_veto --> task_06_excuse_gr2060_at_jit_gate
  task_06_excuse_gr2060_at_jit_gate --> task_07_ssot_producer_coverage_section
  task_07_ssot_producer_coverage_section --> task_08_advance_the_code_ladder
  task_08_advance_the_code_ladder --> task_09_author_corpus_sweep
  task_08_advance_the_code_ladder --> task_11_record_gr2060_in_knowledge_skill
  task_08_advance_the_code_ladder --> task_12_update_doc_19_status
  task_09_author_corpus_sweep --> plan_guardrails
  task_10_add_callee_parameter_list_step --> plan_guardrails
  task_11_record_gr2060_in_knowledge_skill --> plan_guardrails
  task_12_update_doc_19_status --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
