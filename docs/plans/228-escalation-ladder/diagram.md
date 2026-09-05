<!-- guardrails:graph v1 source-sha256=66cc5b25bae08794f71866c05851a9b07353804a7d73b79168f3b235ed81bdf6 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_escalation_ladder["01-author-tests-escalation-ladder"]
    task_01_author_tests_escalation_ladder_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_escalation_ladder_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_escalation_ladder fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_escalation_ladder["02-implement-escalation-ladder"]
    task_02_implement_escalation_ladder_gr_0["01-ladder-tests-pass"]:::guardrail
  end
  style task_02_implement_escalation_ladder fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_escalated_provenance["03-author-tests-escalated-provenance"]
    task_03_author_tests_escalated_provenance_gr_0["01-build-passes"]:::guardrail
    task_03_author_tests_escalated_provenance_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_03_author_tests_escalated_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_escalated_provenance["04-implement-escalated-provenance"]
    task_04_implement_escalated_provenance_gr_0["01-provenance-tests-pass"]:::guardrail
  end
  style task_04_implement_escalated_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_retry_loop_escalation["05-author-tests-retry-loop-escalation"]
    task_05_author_tests_retry_loop_escalation_gr_0["01-build-passes"]:::guardrail
    task_05_author_tests_retry_loop_escalation_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_05_author_tests_retry_loop_escalation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_implement_retry_loop_escalation["06-implement-retry-loop-escalation"]
    task_06_implement_retry_loop_escalation_gr_0["01-real-seam-tests-pass"]:::guardrail
  end
  style task_06_implement_retry_loop_escalation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_update_design_docs["07-update-design-docs"]
    task_07_update_design_docs_gr_0["01-docs-record-the-ladder"]:::guardrail
  end
  style task_07_update_design_docs fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-escalation-seam-union-verified"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_escalation_ladder
  task_01_author_tests_escalation_ladder --> task_02_implement_escalation_ladder
  task_01_author_tests_escalation_ladder --> task_03_author_tests_escalated_provenance
  task_02_implement_escalation_ladder --> task_05_author_tests_retry_loop_escalation
  task_03_author_tests_escalated_provenance --> task_04_implement_escalated_provenance
  task_04_implement_escalated_provenance --> task_05_author_tests_retry_loop_escalation
  task_05_author_tests_retry_loop_escalation --> task_06_implement_retry_loop_escalation
  task_06_implement_retry_loop_escalation --> task_07_update_design_docs
  task_07_update_design_docs --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
