<!-- guardrails:graph v1 source-sha256=7d7f243a824dfdd8fcecbaf85a5de85f5aa80f810b863bd920ea45ace7d68c63 body-sha256=433af13e8f45b00231ead0151b2ce49d87baad474e5f25ddd565bfb440cd3bc6 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_staging_tree["01-author-tests-staging-tree"]
    task_01_author_tests_staging_tree_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_staging_tree_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_staging_tree fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_staging_tree["02-implement-staging-tree"]
    task_02_implement_staging_tree_gr_0["01-tests-pass"]:::guardrail
  end
  style task_02_implement_staging_tree fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_drain["03-author-tests-drain"]
    task_03_author_tests_drain_gr_0["01-build-passes"]:::guardrail
    task_03_author_tests_drain_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_03_author_tests_drain fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_drain["04-implement-drain"]
    task_04_implement_drain_gr_0["01-tests-pass"]:::guardrail
  end
  style task_04_implement_drain fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_provenance["05-author-tests-provenance"]
    task_05_author_tests_provenance_gr_0["01-build-passes"]:::guardrail
    task_05_author_tests_provenance_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_05_author_tests_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_implement_provenance["06-implement-provenance"]
    task_06_implement_provenance_gr_0["01-tests-pass"]:::guardrail
  end
  style task_06_implement_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_observer_event["07-author-tests-observer-event"]
    task_07_author_tests_observer_event_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_observer_event_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_07_author_tests_observer_event fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_implement_observer_core["08-implement-observer-core"]
    task_08_implement_observer_core_gr_0["01-tests-pass"]:::guardrail
  end
  style task_08_implement_observer_core fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_implement_observer_cli["09-implement-observer-cli"]
    task_09_implement_observer_cli_gr_0["01-tests-pass"]:::guardrail
  end
  style task_09_implement_observer_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_author_tests_supply_command["10-author-tests-supply-command"]
    task_10_author_tests_supply_command_gr_0["01-build-passes"]:::guardrail
    task_10_author_tests_supply_command_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_10_author_tests_supply_command fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_implement_supply_command["11-implement-supply-command"]
    task_11_implement_supply_command_gr_0["01-tests-pass"]:::guardrail
  end
  style task_11_implement_supply_command fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_author_tests_caller_scope["12-author-tests-caller-scope"]
    task_12_author_tests_caller_scope_gr_0["01-build-passes"]:::guardrail
    task_12_author_tests_caller_scope_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_12_author_tests_caller_scope fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_implement_caller_scope["13-implement-caller-scope"]
    task_13_implement_caller_scope_gr_0["01-tests-pass"]:::guardrail
  end
  style task_13_implement_caller_scope fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_author_tests_boundary_wiring["14-author-tests-boundary-wiring"]
    task_14_author_tests_boundary_wiring_gr_0["01-build-passes"]:::guardrail
    task_14_author_tests_boundary_wiring_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_14_author_tests_boundary_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_wire_drain_at_task_boundary["15-wire-drain-at-task-boundary"]
    task_15_wire_drain_at_task_boundary_gr_0["01-tests-pass"]:::guardrail
  end
  style task_15_wire_drain_at_task_boundary fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_16_wire_drain_at_run_start["16-wire-drain-at-run-start"]
    task_16_wire_drain_at_run_start_gr_0["01-tests-pass"]:::guardrail
  end
  style task_16_wire_drain_at_run_start fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_17_author_tests_resume_shorthand["17-author-tests-resume-shorthand"]
    task_17_author_tests_resume_shorthand_gr_0["01-build-passes"]:::guardrail
    task_17_author_tests_resume_shorthand_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_17_author_tests_resume_shorthand fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_18_implement_resume_shorthand["18-implement-resume-shorthand"]
    task_18_implement_resume_shorthand_gr_0["01-tests-pass"]:::guardrail
  end
  style task_18_implement_resume_shorthand fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_19_author_tests_overwatcher_autoresolve["19-author-tests-overwatcher-autoresolve"]
    task_19_author_tests_overwatcher_autoresolve_gr_0["01-build-passes"]:::guardrail
    task_19_author_tests_overwatcher_autoresolve_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_19_author_tests_overwatcher_autoresolve fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_20_implement_overwatcher_autoresolve["20-implement-overwatcher-autoresolve"]
    task_20_implement_overwatcher_autoresolve_gr_0["01-tests-pass"]:::guardrail
  end
  style task_20_implement_overwatcher_autoresolve fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_21_update_ssot_supply_contracts["21-update-ssot-supply-contracts"]
    task_21_update_ssot_supply_contracts_gr_0["01-ssot-records-the-contracts"]:::guardrail
  end
  style task_21_update_ssot_supply_contracts fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_22_update_readme_supply["22-update-readme-supply"]
    task_22_update_readme_supply_gr_0["01-readme-documents-supply"]:::guardrail
  end
  style task_22_update_readme_supply fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_23_update_domain_knowledge_skill["23-update-domain-knowledge-skill"]
    task_23_update_domain_knowledge_skill_gr_0["01-skill-documents-supply"]:::guardrail
  end
  style task_23_update_domain_knowledge_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-supplied-tree-union-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_staging_tree
  task_01_author_tests_staging_tree --> task_02_implement_staging_tree
  task_02_implement_staging_tree --> task_03_author_tests_drain
  task_02_implement_staging_tree --> task_05_author_tests_provenance
  task_02_implement_staging_tree --> task_07_author_tests_observer_event
  task_02_implement_staging_tree --> task_10_author_tests_supply_command
  task_02_implement_staging_tree --> task_12_author_tests_caller_scope
  task_03_author_tests_drain --> task_04_implement_drain
  task_04_implement_drain --> task_14_author_tests_boundary_wiring
  task_05_author_tests_provenance --> task_06_implement_provenance
  task_06_implement_provenance --> task_14_author_tests_boundary_wiring
  task_06_implement_provenance --> task_19_author_tests_overwatcher_autoresolve
  task_06_implement_provenance --> task_21_update_ssot_supply_contracts
  task_07_author_tests_observer_event --> task_08_implement_observer_core
  task_07_author_tests_observer_event --> task_09_implement_observer_cli
  task_08_implement_observer_core --> task_09_implement_observer_cli
  task_09_implement_observer_cli --> task_14_author_tests_boundary_wiring
  task_09_implement_observer_cli --> task_21_update_ssot_supply_contracts
  task_10_author_tests_supply_command --> task_11_implement_supply_command
  task_11_implement_supply_command --> task_17_author_tests_resume_shorthand
  task_11_implement_supply_command --> task_22_update_readme_supply
  task_11_implement_supply_command --> task_23_update_domain_knowledge_skill
  task_12_author_tests_caller_scope --> task_13_implement_caller_scope
  task_13_implement_caller_scope --> task_19_author_tests_overwatcher_autoresolve
  task_13_implement_caller_scope --> task_23_update_domain_knowledge_skill
  task_14_author_tests_boundary_wiring --> task_15_wire_drain_at_task_boundary
  task_14_author_tests_boundary_wiring --> task_16_wire_drain_at_run_start
  task_16_wire_drain_at_run_start --> task_17_author_tests_resume_shorthand
  task_16_wire_drain_at_run_start --> task_21_update_ssot_supply_contracts
  task_17_author_tests_resume_shorthand --> task_18_implement_resume_shorthand
  task_19_author_tests_overwatcher_autoresolve --> task_20_implement_overwatcher_autoresolve
  task_15_wire_drain_at_task_boundary --> plan_guardrails
  task_18_implement_resume_shorthand --> plan_guardrails
  task_20_implement_overwatcher_autoresolve --> plan_guardrails
  task_21_update_ssot_supply_contracts --> plan_guardrails
  task_22_update_readme_supply --> plan_guardrails
  task_23_update_domain_knowledge_skill --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
