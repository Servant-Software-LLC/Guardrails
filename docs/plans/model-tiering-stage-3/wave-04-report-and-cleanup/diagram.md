<!-- guardrails:graph v1 source-sha256=e6740edb2606ae7db31b09bacb00463d6fff410627fb254f6a1403c37c50eebd -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave3-surfaces-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_04_report_and_cleanup_01_author_tests_models_used_report["wave-04-report-and-cleanup/01-author-tests-models-used-report"]
    task_wave_04_report_and_cleanup_01_author_tests_models_used_report_gr_0["01-tests-build"]:::guardrail
    task_wave_04_report_and_cleanup_01_author_tests_models_used_report_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_04_report_and_cleanup_01_author_tests_models_used_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_report_and_cleanup_02_implement_models_used_report["wave-04-report-and-cleanup/02-implement-models-used-report"]
    task_wave_04_report_and_cleanup_02_implement_models_used_report_gr_0["01-models-used-tests-pass"]:::guardrail
  end
  style task_wave_04_report_and_cleanup_02_implement_models_used_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder["wave-04-report-and-cleanup/03-delete-superseded-plan-folder"]
    task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder_gr_0["01-folder-gone"]:::guardrail
  end
  style task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge["wave-04-report-and-cleanup/04-update-ssot-and-domain-knowledge"]
    task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge_gr_0["01-contract-delta-present"]:::guardrail
  end
  style task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-suites-pass"]:::guardrail
    plan_guardrails_2["03-wave-deliverables-present"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_04_report_and_cleanup_01_author_tests_models_used_report
  plan_preflights --> task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder
  task_wave_04_report_and_cleanup_01_author_tests_models_used_report --> task_wave_04_report_and_cleanup_02_implement_models_used_report
  task_wave_04_report_and_cleanup_02_implement_models_used_report --> task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge
  task_wave_04_report_and_cleanup_03_delete_superseded_plan_folder --> task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge
  task_wave_04_report_and_cleanup_04_update_ssot_and_domain_knowledge --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
