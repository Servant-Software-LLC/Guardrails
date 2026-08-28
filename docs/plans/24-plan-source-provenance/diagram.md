<!-- guardrails:graph v1 source-sha256=0269f58e6eb718493b561283ec21e015b20cf91d74a866034c2a31ee8f990065 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_plan_source_record["01-author-tests-plan-source-record"]
    task_01_author_tests_plan_source_record_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_plan_source_record_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_plan_source_record fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_plan_source_record["02-implement-plan-source-record"]
    task_02_implement_plan_source_record_gr_0["01-build-passes"]:::guardrail
    task_02_implement_plan_source_record_gr_1["02-plan-source-record-tests-pass"]:::guardrail
  end
  style task_02_implement_plan_source_record fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_declared_count_gate["03-author-tests-declared-count-gate"]
    task_03_author_tests_declared_count_gate_gr_0["01-build-passes"]:::guardrail
    task_03_author_tests_declared_count_gate_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_03_author_tests_declared_count_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_declared_count_gate["04-implement-declared-count-gate"]
    task_04_implement_declared_count_gate_gr_0["01-build-passes"]:::guardrail
    task_04_implement_declared_count_gate_gr_1["02-declared-count-gate-tests-pass"]:::guardrail
  end
  style task_04_implement_declared_count_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_wire_recorder_into_breakdown["05-wire-recorder-into-breakdown"]
    task_05_wire_recorder_into_breakdown_gr_0["01-wiring-test-drives-the-real-seam"]:::guardrail
    task_05_wire_recorder_into_breakdown_gr_1["02-breakdown-command-wires-the-gate"]:::guardrail
    task_05_wire_recorder_into_breakdown_gr_2["03-build-passes"]:::guardrail
    task_05_wire_recorder_into_breakdown_gr_3["04-wiring-tests-pass"]:::guardrail
  end
  style task_05_wire_recorder_into_breakdown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_update_ssot_and_domain_knowledge["06-update-ssot-and-domain-knowledge"]
    task_06_update_ssot_and_domain_knowledge_gr_0["01-ssot-records-the-artifact"]:::guardrail
    task_06_update_ssot_and_domain_knowledge_gr_1["02-domain-knowledge-records-the-artifact"]:::guardrail
  end
  style task_06_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-suite-passes"]:::guardrail
    plan_guardrails_2["03-union-artifacts-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_plan_source_record
  plan_preflights --> task_03_author_tests_declared_count_gate
  task_01_author_tests_plan_source_record --> task_02_implement_plan_source_record
  task_02_implement_plan_source_record --> task_05_wire_recorder_into_breakdown
  task_03_author_tests_declared_count_gate --> task_04_implement_declared_count_gate
  task_04_implement_declared_count_gate --> task_05_wire_recorder_into_breakdown
  task_05_wire_recorder_into_breakdown --> task_06_update_ssot_and_domain_knowledge
  task_06_update_ssot_and_domain_knowledge --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
