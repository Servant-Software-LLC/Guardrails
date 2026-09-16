<!-- guardrails:graph v1 source-sha256=4840417eedb6148fb5375437a10965db63dbc1b61be3d9828460a6dec1be1269 body-sha256=885a12cb89dc88beca565621c80ecf4299ef99c4a0692b8259761ab54adb859f -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_wiring_proof["01-author-tests-wiring-proof"]
    task_01_author_tests_wiring_proof_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_wiring_proof_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_01_author_tests_wiring_proof fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_author_tests_missing_resource_facts["02-author-tests-missing-resource-facts"]
    task_02_author_tests_missing_resource_facts_gr_0["01-build-passes"]:::guardrail
    task_02_author_tests_missing_resource_facts_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_02_author_tests_missing_resource_facts_gr_2["03-tokens-and-fixture"]:::guardrail
  end
  style task_02_author_tests_missing_resource_facts fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_implement_missing_resource_facts["03-implement-missing-resource-facts"]
    task_03_implement_missing_resource_facts_gr_0["01-tests-pass"]:::guardrail
    task_03_implement_missing_resource_facts_gr_1["02-forward-census"]:::guardrail
  end
  style task_03_implement_missing_resource_facts fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_author_tests_supply_certification["04-author-tests-supply-certification"]
    task_04_author_tests_supply_certification_gr_0["01-build-passes"]:::guardrail
    task_04_author_tests_supply_certification_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_04_author_tests_supply_certification_gr_2["03-tests-do-not-pin-deleted-members"]:::guardrail
  end
  style task_04_author_tests_supply_certification fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_implement_supply_certification["05-implement-supply-certification"]
    task_05_implement_supply_certification_gr_0["01-tests-pass"]:::guardrail
    task_05_implement_supply_certification_gr_1["02-forward-census"]:::guardrail
    task_05_implement_supply_certification_gr_2["03-deleted-members-are-gone"]:::guardrail
    task_05_implement_supply_certification_gr_3["04-certify-is-pure"]:::guardrail
    task_05_implement_supply_certification_gr_4["05-judge-uses-the-shared-rule"]:::guardrail
  end
  style task_05_implement_supply_certification fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_author_tests_delivery_interlock["06-author-tests-delivery-interlock"]
    task_06_author_tests_delivery_interlock_gr_0["01-build-passes"]:::guardrail
    task_06_author_tests_delivery_interlock_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_06_author_tests_delivery_interlock fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_implement_delivery_interlock["07-implement-delivery-interlock"]
    task_07_implement_delivery_interlock_gr_0["01-tests-pass"]:::guardrail
    task_07_implement_delivery_interlock_gr_1["02-forward-census"]:::guardrail
    task_07_implement_delivery_interlock_gr_2["03-one-shared-predicate"]:::guardrail
    task_07_implement_delivery_interlock_gr_3["04-existing-interlock-rows-still-green"]:::guardrail
  end
  style task_07_implement_delivery_interlock fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_author_tests_resource_supply_brief["08-author-tests-resource-supply-brief"]
    task_08_author_tests_resource_supply_brief_gr_0["01-build-passes"]:::guardrail
    task_08_author_tests_resource_supply_brief_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_08_author_tests_resource_supply_brief fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_implement_resource_supply_brief["09-implement-resource-supply-brief"]
    task_09_implement_resource_supply_brief_gr_0["01-tests-pass"]:::guardrail
    task_09_implement_resource_supply_brief_gr_1["02-forward-census"]:::guardrail
    task_09_implement_resource_supply_brief_gr_2["03-real-seam-tests-pass"]:::guardrail
    task_09_implement_resource_supply_brief_gr_3["04-generic-brief-unchanged"]:::guardrail
  end
  style task_09_implement_resource_supply_brief fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_author_tests_commit_paths["10-author-tests-commit-paths"]
    task_10_author_tests_commit_paths_gr_0["01-build-passes"]:::guardrail
    task_10_author_tests_commit_paths_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_10_author_tests_commit_paths_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_10_author_tests_commit_paths fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_implement_commit_paths["11-implement-commit-paths"]
    task_11_implement_commit_paths_gr_0["01-build-passes"]:::guardrail
    task_11_implement_commit_paths_gr_1["02-commit-paths-tests-pass"]:::guardrail
    task_11_implement_commit_paths_gr_2["03-forward-census"]:::guardrail
    task_11_implement_commit_paths_gr_3["04-drain-delegates-to-commit-paths"]:::guardrail
  end
  style task_11_implement_commit_paths fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_author_tests_observer_by_field["12-author-tests-observer-by-field"]
    task_12_author_tests_observer_by_field_gr_0["01-build-passes"]:::guardrail
    task_12_author_tests_observer_by_field_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_12_author_tests_observer_by_field_gr_2["03-guards-compare-parameter-lists"]:::guardrail
  end
  style task_12_author_tests_observer_by_field fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_implement_observer_by_field["13-implement-observer-by-field"]
    task_13_implement_observer_by_field_gr_0["01-build-passes"]:::guardrail
    task_13_implement_observer_by_field_gr_1["02-observer-by-tests-pass"]:::guardrail
    task_13_implement_observer_by_field_gr_2["03-forward-census"]:::guardrail
    task_13_implement_observer_by_field_gr_3["04-member-replaced-not-overloaded"]:::guardrail
    task_13_implement_observer_by_field_gr_4["05-journal-doc-comments-current"]:::guardrail
    task_13_implement_observer_by_field_gr_5["06-drain-call-site-names-the-supplier"]:::guardrail
  end
  style task_13_implement_observer_by_field fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_implement_autoresolve_wiring["14-implement-autoresolve-wiring"]
    task_14_implement_autoresolve_wiring_gr_0["01-wiring-proof-tests-pass"]:::guardrail
    task_14_implement_autoresolve_wiring_gr_1["02-certify-is-on-the-real-path"]:::guardrail
  end
  style task_14_implement_autoresolve_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_author_tests_halt_text_and_interlock_wording["15-author-tests-halt-text-and-interlock-wording"]
    task_15_author_tests_halt_text_and_interlock_wording_gr_0["01-build-passes"]:::guardrail
    task_15_author_tests_halt_text_and_interlock_wording_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_15_author_tests_halt_text_and_interlock_wording_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_15_author_tests_halt_text_and_interlock_wording fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_16_implement_halt_text_and_interlock_wording["16-implement-halt-text-and-interlock-wording"]
    task_16_implement_halt_text_and_interlock_wording_gr_0["01-tests-pass"]:::guardrail
    task_16_implement_halt_text_and_interlock_wording_gr_1["02-forward-census"]:::guardrail
    task_16_implement_halt_text_and_interlock_wording_gr_2["03-halt-text-uses-the-shared-predicate"]:::guardrail
  end
  style task_16_implement_halt_text_and_interlock_wording fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_17_author_tests_terminal_gate_names_supply["17-author-tests-terminal-gate-names-supply"]
    task_17_author_tests_terminal_gate_names_supply_gr_0["01-build-passes"]:::guardrail
    task_17_author_tests_terminal_gate_names_supply_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_17_author_tests_terminal_gate_names_supply_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_17_author_tests_terminal_gate_names_supply fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_18_implement_terminal_gate_names_supply["18-implement-terminal-gate-names-supply"]
    task_18_implement_terminal_gate_names_supply_gr_0["01-tests-pass"]:::guardrail
    task_18_implement_terminal_gate_names_supply_gr_1["02-forward-census"]:::guardrail
    task_18_implement_terminal_gate_names_supply_gr_2["03-appends-the-shared-reader"]:::guardrail
  end
  style task_18_implement_terminal_gate_names_supply fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_19_update_ssot_and_design_docs["19-update-ssot-and-design-docs"]
    task_19_update_ssot_and_design_docs_gr_0["01-ssot-and-designs-record-the-autoresolve"]:::guardrail
  end
  style task_19_update_ssot_and_design_docs fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_20_update_domain_knowledge_skill["20-update-domain-knowledge-skill"]
    task_20_update_domain_knowledge_skill_gr_0["01-skill-records-the-autoresolve"]:::guardrail
  end
  style task_20_update_domain_knowledge_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-union-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_wiring_proof
  plan_preflights --> task_02_author_tests_missing_resource_facts
  plan_preflights --> task_06_author_tests_delivery_interlock
  plan_preflights --> task_10_author_tests_commit_paths
  plan_preflights --> task_12_author_tests_observer_by_field
  plan_preflights --> task_17_author_tests_terminal_gate_names_supply
  task_01_author_tests_wiring_proof --> task_14_implement_autoresolve_wiring
  task_02_author_tests_missing_resource_facts --> task_03_implement_missing_resource_facts
  task_03_implement_missing_resource_facts --> task_04_author_tests_supply_certification
  task_03_implement_missing_resource_facts --> task_14_implement_autoresolve_wiring
  task_03_implement_missing_resource_facts --> task_15_author_tests_halt_text_and_interlock_wording
  task_04_author_tests_supply_certification --> task_05_implement_supply_certification
  task_05_implement_supply_certification --> task_08_author_tests_resource_supply_brief
  task_05_implement_supply_certification --> task_14_implement_autoresolve_wiring
  task_06_author_tests_delivery_interlock --> task_07_implement_delivery_interlock
  task_07_implement_delivery_interlock --> task_08_author_tests_resource_supply_brief
  task_07_implement_delivery_interlock --> task_14_implement_autoresolve_wiring
  task_07_implement_delivery_interlock --> task_15_author_tests_halt_text_and_interlock_wording
  task_08_author_tests_resource_supply_brief --> task_09_implement_resource_supply_brief
  task_09_implement_resource_supply_brief --> task_14_implement_autoresolve_wiring
  task_10_author_tests_commit_paths --> task_11_implement_commit_paths
  task_11_implement_commit_paths --> task_14_implement_autoresolve_wiring
  task_12_author_tests_observer_by_field --> task_13_implement_observer_by_field
  task_13_implement_observer_by_field --> task_14_implement_autoresolve_wiring
  task_14_implement_autoresolve_wiring --> task_18_implement_terminal_gate_names_supply
  task_14_implement_autoresolve_wiring --> task_19_update_ssot_and_design_docs
  task_14_implement_autoresolve_wiring --> task_20_update_domain_knowledge_skill
  task_15_author_tests_halt_text_and_interlock_wording --> task_16_implement_halt_text_and_interlock_wording
  task_17_author_tests_terminal_gate_names_supply --> task_18_implement_terminal_gate_names_supply
  task_16_implement_halt_text_and_interlock_wording --> plan_guardrails
  task_18_implement_terminal_gate_names_supply --> plan_guardrails
  task_19_update_ssot_and_design_docs --> plan_guardrails
  task_20_update_domain_knowledge_skill --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
