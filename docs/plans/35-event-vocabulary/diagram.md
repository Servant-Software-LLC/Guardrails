<!-- guardrails:graph v1 source-sha256=91055a4a797d25f60aa5dfa28629406b9139a0043b13347a84465442373c1cdd -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-runevents-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_migrate_attemptfinished_payload["01-migrate-attemptfinished-payload"]
    task_01_migrate_attemptfinished_payload_gr_0["01-solution-builds"]:::guardrail
    task_01_migrate_attemptfinished_payload_gr_1["02-runevents-tests-pass"]:::guardrail
  end
  style task_01_migrate_attemptfinished_payload fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_author_tests_observer_forwarding["02-author-tests-observer-forwarding"]
    task_02_author_tests_observer_forwarding_gr_0["01-tests-build"]:::guardrail
    task_02_author_tests_observer_forwarding_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_02_author_tests_observer_forwarding fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_forward_runfinished_in_decorators["03-forward-runfinished-in-decorators"]
    task_03_forward_runfinished_in_decorators_gr_0["01-forwarding-tests-pass"]:::guardrail
  end
  style task_03_forward_runfinished_in_decorators fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_author_tests_event_vocabulary["04-author-tests-event-vocabulary"]
    task_04_author_tests_event_vocabulary_gr_0["01-tests-build"]:::guardrail
    task_04_author_tests_event_vocabulary_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_04_author_tests_event_vocabulary fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_implement_event_vocabulary["05-implement-event-vocabulary"]
    task_05_implement_event_vocabulary_gr_0["01-vocabulary-tests-pass"]:::guardrail
    task_05_implement_event_vocabulary_gr_1["02-existing-rows-unchanged"]:::guardrail
  end
  style task_05_implement_event_vocabulary fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_author_tests_record_roundtrip["06-author-tests-record-roundtrip"]
    task_06_author_tests_record_roundtrip_gr_0["01-tests-build"]:::guardrail
    task_06_author_tests_record_roundtrip_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_06_author_tests_record_roundtrip fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_implement_record_roundtrip["07-implement-record-roundtrip"]
    task_07_implement_record_roundtrip_gr_0["01-roundtrip-tests-pass"]:::guardrail
    task_07_implement_record_roundtrip_gr_1["02-existing-attach-tests-pass"]:::guardrail
  end
  style task_07_implement_record_roundtrip fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_author_tests_worktree_settle_event["08-author-tests-worktree-settle-event"]
    task_08_author_tests_worktree_settle_event_gr_0["01-tests-build"]:::guardrail
    task_08_author_tests_worktree_settle_event_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_08_author_tests_worktree_settle_event fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_fix_worktree_settle_event["09-fix-worktree-settle-event"]
    task_09_fix_worktree_settle_event_gr_0["01-worktree-settle-tests-pass"]:::guardrail
    task_09_fix_worktree_settle_event_gr_1["02-comment-states-the-worktree-scope"]:::guardrail
  end
  style task_09_fix_worktree_settle_event fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_author_tests_run_finished_exit_paths["10-author-tests-run-finished-exit-paths"]
    task_10_author_tests_run_finished_exit_paths_gr_0["01-tests-build"]:::guardrail
    task_10_author_tests_run_finished_exit_paths_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_10_author_tests_run_finished_exit_paths fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_raise_run_finished_in_runcommand["11-raise-run-finished-in-runcommand"]
    task_11_raise_run_finished_in_runcommand_gr_0["01-exit-path-tests-pass"]:::guardrail
  end
  style task_11_raise_run_finished_in_runcommand fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_author_tests_terminal_row_delivery["12-author-tests-terminal-row-delivery"]
    task_12_author_tests_terminal_row_delivery_gr_0["01-tests-build"]:::guardrail
    task_12_author_tests_terminal_row_delivery_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_12_author_tests_terminal_row_delivery fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_deliver_terminal_row_in_logserver["13-deliver-terminal-row-in-logserver"]
    task_13_deliver_terminal_row_in_logserver_gr_0["01-delivery-tests-pass"]:::guardrail
    task_13_deliver_terminal_row_in_logserver_gr_1["02-existing-endpoint-tests-pass"]:::guardrail
  end
  style task_13_deliver_terminal_row_in_logserver fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_document_the_streams_in_ssot["14-document-the-streams-in-ssot"]
    task_14_document_the_streams_in_ssot_gr_0["01-ssot-documents-the-streams"]:::guardrail
  end
  style task_14_document_the_streams_in_ssot fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_15_update_domain_knowledge_skill["15-update-domain-knowledge-skill"]
    task_15_update_domain_knowledge_skill_gr_0["01-skill-documents-the-streams"]:::guardrail
  end
  style task_15_update_domain_knowledge_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-union-integrity"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_migrate_attemptfinished_payload
  task_01_migrate_attemptfinished_payload --> task_02_author_tests_observer_forwarding
  task_01_migrate_attemptfinished_payload --> task_08_author_tests_worktree_settle_event
  task_02_author_tests_observer_forwarding --> task_03_forward_runfinished_in_decorators
  task_03_forward_runfinished_in_decorators --> task_04_author_tests_event_vocabulary
  task_03_forward_runfinished_in_decorators --> task_06_author_tests_record_roundtrip
  task_04_author_tests_event_vocabulary --> task_05_implement_event_vocabulary
  task_05_implement_event_vocabulary --> task_10_author_tests_run_finished_exit_paths
  task_05_implement_event_vocabulary --> task_12_author_tests_terminal_row_delivery
  task_05_implement_event_vocabulary --> task_14_document_the_streams_in_ssot
  task_05_implement_event_vocabulary --> task_15_update_domain_knowledge_skill
  task_06_author_tests_record_roundtrip --> task_07_implement_record_roundtrip
  task_07_implement_record_roundtrip --> task_14_document_the_streams_in_ssot
  task_07_implement_record_roundtrip --> task_15_update_domain_knowledge_skill
  task_08_author_tests_worktree_settle_event --> task_09_fix_worktree_settle_event
  task_10_author_tests_run_finished_exit_paths --> task_11_raise_run_finished_in_runcommand
  task_11_raise_run_finished_in_runcommand --> task_14_document_the_streams_in_ssot
  task_12_author_tests_terminal_row_delivery --> task_13_deliver_terminal_row_in_logserver
  task_13_deliver_terminal_row_in_logserver --> task_14_document_the_streams_in_ssot
  task_09_fix_worktree_settle_event --> plan_guardrails
  task_14_document_the_streams_in_ssot --> plan_guardrails
  task_15_update_domain_knowledge_skill --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
