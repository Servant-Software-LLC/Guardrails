<!-- guardrails:graph v1 source-sha256=4bbfaa6d4c2e487135aed0e2b49262efc374648562ad5bee201afea33e97757a -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-runevents-green"]:::preflight
    plan_preflights_1["02-baseline-integration-runevents-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_update_ssot_event_schema["01-update-ssot-event-schema"]
    task_01_update_ssot_event_schema_gr_0["01-ssot-contract-present"]:::guardrail
  end
  style task_01_update_ssot_event_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_author_tests_bracket_and_wire_copy["02-author-tests-bracket-and-wire-copy"]
    task_02_author_tests_bracket_and_wire_copy_gr_0["01-tests-build"]:::guardrail
    task_02_author_tests_bracket_and_wire_copy_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_02_author_tests_bracket_and_wire_copy fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_implement_bracket_and_wire_copy["03-implement-bracket-and-wire-copy"]
    task_03_implement_bracket_and_wire_copy_gr_0["01-bracket-tests-pass"]:::guardrail
    task_03_implement_bracket_and_wire_copy_gr_1["02-existing-runevents-unregressed"]:::guardrail
  end
  style task_03_implement_bracket_and_wire_copy fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_author_tests_webhook_policy["04-author-tests-webhook-policy"]
    task_04_author_tests_webhook_policy_gr_0["01-tests-build"]:::guardrail
    task_04_author_tests_webhook_policy_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_04_author_tests_webhook_policy fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_implement_webhook_policy["05-implement-webhook-policy"]
    task_05_implement_webhook_policy_gr_0["01-policy-tests-pass"]:::guardrail
  end
  style task_05_implement_webhook_policy fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_author_tests_webhook_dispatcher["06-author-tests-webhook-dispatcher"]
    task_06_author_tests_webhook_dispatcher_gr_0["01-tests-build"]:::guardrail
    task_06_author_tests_webhook_dispatcher_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_06_author_tests_webhook_dispatcher fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_implement_webhook_dispatcher["07-implement-webhook-dispatcher"]
    task_07_implement_webhook_dispatcher_gr_0["01-dispatcher-tests-pass"]:::guardrail
    task_07_implement_webhook_dispatcher_gr_1["02-policy-tests-unregressed"]:::guardrail
  end
  style task_07_implement_webhook_dispatcher fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_author_tests_cli_wiring_and_delivery["08-author-tests-cli-wiring-and-delivery"]
    task_08_author_tests_cli_wiring_and_delivery_gr_0["01-tests-build"]:::guardrail
    task_08_author_tests_cli_wiring_and_delivery_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_08_author_tests_cli_wiring_and_delivery fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_implement_cli_wiring["09-implement-cli-wiring"]
    task_09_implement_cli_wiring_gr_0["01-composition-root-constructs-the-sink"]:::guardrail
    task_09_implement_cli_wiring_gr_1["02-webhook-delivery-tests-pass"]:::guardrail
  end
  style task_09_implement_cli_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_update_domain_knowledge_skill["10-update-domain-knowledge-skill"]
    task_10_update_domain_knowledge_skill_gr_0["01-skill-documents-on-event"]:::guardrail
  end
  style task_10_update_domain_knowledge_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_record_ws_closure_in_design["11-record-ws-closure-in-design"]
    task_11_record_ws_closure_in_design_gr_0["01-design-records-ws-closure"]:::guardrail
  end
  style task_11_record_ws_closure_in_design fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-union-integrity"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_update_ssot_event_schema
  plan_preflights --> task_02_author_tests_bracket_and_wire_copy
  plan_preflights --> task_04_author_tests_webhook_policy
  plan_preflights --> task_10_update_domain_knowledge_skill
  plan_preflights --> task_11_record_ws_closure_in_design
  task_02_author_tests_bracket_and_wire_copy --> task_03_implement_bracket_and_wire_copy
  task_03_implement_bracket_and_wire_copy --> task_06_author_tests_webhook_dispatcher
  task_04_author_tests_webhook_policy --> task_05_implement_webhook_policy
  task_05_implement_webhook_policy --> task_06_author_tests_webhook_dispatcher
  task_06_author_tests_webhook_dispatcher --> task_07_implement_webhook_dispatcher
  task_07_implement_webhook_dispatcher --> task_08_author_tests_cli_wiring_and_delivery
  task_08_author_tests_cli_wiring_and_delivery --> task_09_implement_cli_wiring
  task_01_update_ssot_event_schema --> plan_guardrails
  task_09_implement_cli_wiring --> plan_guardrails
  task_10_update_domain_knowledge_skill --> plan_guardrails
  task_11_record_ws_closure_in_design --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
