<!-- guardrails:graph v1 source-sha256=1bcacbd27b3de1fa503a02edd11b518c6e4df59a4a3e4abb03381691be544414 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-provenance-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_stream_model_capture["01-author-tests-stream-model-capture"]
    task_01_author_tests_stream_model_capture_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_stream_model_capture_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_stream_model_capture fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_stream_model_capture["02-implement-stream-model-capture"]
    task_02_implement_stream_model_capture_gr_0["01-build-passes"]:::guardrail
    task_02_implement_stream_model_capture_gr_1["02-parser-reads-init-model"]:::guardrail
    task_02_implement_stream_model_capture_gr_2["03-capture-tests-pass"]:::guardrail
  end
  style task_02_implement_stream_model_capture fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_provenance_model["03-author-tests-provenance-model"]
    task_03_author_tests_provenance_model_gr_0["01-build-passes"]:::guardrail
    task_03_author_tests_provenance_model_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_03_author_tests_provenance_model fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_provenance_model["04-implement-provenance-model"]
    task_04_implement_provenance_model_gr_0["01-build-passes"]:::guardrail
    task_04_implement_provenance_model_gr_1["02-provenance-tests-pass"]:::guardrail
  end
  style task_04_implement_provenance_model fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_observer_model_resolved["05-author-tests-observer-model-resolved"]
    task_05_author_tests_observer_model_resolved_gr_0["01-build-passes"]:::guardrail
    task_05_author_tests_observer_model_resolved_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_05_author_tests_observer_model_resolved fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_implement_observer_render_forward["06-implement-observer-render-forward"]
    task_06_implement_observer_render_forward_gr_0["01-build-passes"]:::guardrail
    task_06_implement_observer_render_forward_gr_1["02-observer-forwarding-tests-pass"]:::guardrail
  end
  style task_06_implement_observer_render_forward fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_model_resolved_firing["07-author-tests-model-resolved-firing"]
    task_07_author_tests_model_resolved_firing_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_model_resolved_firing_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_07_author_tests_model_resolved_firing fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_wire_firing_and_log["08-wire-firing-and-log"]
    task_08_wire_firing_and_log_gr_0["01-build-passes"]:::guardrail
    task_08_wire_firing_and_log_gr_1["02-taskexecutor-fires-event"]:::guardrail
    task_08_wire_firing_and_log_gr_2["03-firing-integration-test-passes"]:::guardrail
  end
  style task_08_wire_firing_and_log fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_author_tests_report_models_used["09-author-tests-report-models-used"]
    task_09_author_tests_report_models_used_gr_0["01-build-passes"]:::guardrail
    task_09_author_tests_report_models_used_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_09_author_tests_report_models_used fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_implement_report_models_used["10-implement-report-models-used"]
    task_10_implement_report_models_used_gr_0["01-build-passes"]:::guardrail
    task_10_implement_report_models_used_gr_1["02-report-tests-pass"]:::guardrail
  end
  style task_10_implement_report_models_used fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_update_ssot_provenance["11-update-ssot-provenance"]
    task_11_update_ssot_provenance_gr_0["01-ssot-documents-provenance"]:::guardrail
  end
  style task_11_update_ssot_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_update_domain_knowledge_skill["12-update-domain-knowledge-skill"]
    task_12_update_domain_knowledge_skill_gr_0["01-domain-knowledge-updated"]:::guardrail
  end
  style task_12_update_domain_knowledge_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-touched-source-conflict-marker-free"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_stream_model_capture
  plan_preflights --> task_03_author_tests_provenance_model
  plan_preflights --> task_05_author_tests_observer_model_resolved
  task_01_author_tests_stream_model_capture --> task_02_implement_stream_model_capture
  task_02_implement_stream_model_capture --> task_04_implement_provenance_model
  task_02_implement_stream_model_capture --> task_07_author_tests_model_resolved_firing
  task_02_implement_stream_model_capture --> task_11_update_ssot_provenance
  task_03_author_tests_provenance_model --> task_04_implement_provenance_model
  task_03_author_tests_provenance_model --> task_09_author_tests_report_models_used
  task_04_implement_provenance_model --> task_10_implement_report_models_used
  task_04_implement_provenance_model --> task_11_update_ssot_provenance
  task_05_author_tests_observer_model_resolved --> task_06_implement_observer_render_forward
  task_05_author_tests_observer_model_resolved --> task_07_author_tests_model_resolved_firing
  task_07_author_tests_model_resolved_firing --> task_08_wire_firing_and_log
  task_08_wire_firing_and_log --> task_11_update_ssot_provenance
  task_09_author_tests_report_models_used --> task_10_implement_report_models_used
  task_11_update_ssot_provenance --> task_12_update_domain_knowledge_skill
  task_06_implement_observer_render_forward --> plan_guardrails
  task_10_implement_report_models_used --> plan_guardrails
  task_12_update_domain_knowledge_skill --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
