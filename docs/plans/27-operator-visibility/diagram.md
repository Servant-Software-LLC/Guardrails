<!-- guardrails:graph v1 source-sha256=d0b27c43274ba5eae158a9aa96f112f009250d9cb104f522d11311d4e06cc8b7 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_serve_diagram["01-author-tests-serve-diagram"]
    task_01_author_tests_serve_diagram_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_serve_diagram_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_serve_diagram fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_serve_diagram_from_log_site["02-serve-diagram-from-log-site"]
    task_02_serve_diagram_from_log_site_gr_0["01-build-passes"]:::guardrail
    task_02_serve_diagram_from_log_site_gr_1["02-serve-diagram-tests-pass"]:::guardrail
    task_02_serve_diagram_from_log_site_gr_2["03-log-server-suite-still-passes"]:::guardrail
  end
  style task_02_serve_diagram_from_log_site fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_replace_meta_refresh["03-replace-meta-refresh"]
    task_03_replace_meta_refresh_gr_0["01-build-passes"]:::guardrail
    task_03_replace_meta_refresh_gr_1["02-diagram-refresh-tests-pass"]:::guardrail
    task_03_replace_meta_refresh_gr_2["03-neighbour-diagram-coverage-survives"]:::guardrail
  end
  style task_03_replace_meta_refresh fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_raise_attempt_route_resolved["04-raise-attempt-route-resolved"]
    task_04_raise_attempt_route_resolved_gr_0["01-build-passes"]:::guardrail
    task_04_raise_attempt_route_resolved_gr_1["02-route-event-raised-before-the-action-runs"]:::guardrail
    task_04_raise_attempt_route_resolved_gr_2["03-both-decorators-forward-the-route-event"]:::guardrail
    task_04_raise_attempt_route_resolved_gr_3["04-model-disclosure-neighbours-still-pass"]:::guardrail
  end
  style task_04_raise_attempt_route_resolved fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_model_in_row["05-author-tests-model-in-row"]
    task_05_author_tests_model_in_row_gr_0["01-build-passes"]:::guardrail
    task_05_author_tests_model_in_row_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_05_author_tests_model_in_row fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_render_model_in_row_and_index["06-render-model-in-row-and-index"]
    task_06_render_model_in_row_and_index_gr_0["01-build-passes"]:::guardrail
    task_06_render_model_in_row_and_index_gr_1["02-model-in-row-tests-pass"]:::guardrail
    task_06_render_model_in_row_and_index_gr_2["03-live-table-has-a-populated-model-column"]:::guardrail
    task_06_render_model_in_row_and_index_gr_3["04-model-and-log-site-neighbours-still-pass"]:::guardrail
  end
  style task_06_render_model_in_row_and_index fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_record_visibility_surfaces_in_ssot["07-record-visibility-surfaces-in-ssot"]
    task_07_record_visibility_surfaces_in_ssot_gr_0["01-ssot-records-the-visibility-surfaces"]:::guardrail
    task_07_record_visibility_surfaces_in_ssot_gr_1["02-domain-knowledge-records-the-visibility-surfaces"]:::guardrail
  end
  style task_07_record_visibility_surfaces_in_ssot fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-suite-passes"]:::guardrail
    plan_guardrails_2["03-integration-suite-passes"]:::guardrail
    plan_guardrails_3["04-union-artifacts-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_serve_diagram
  task_01_author_tests_serve_diagram --> task_02_serve_diagram_from_log_site
  task_02_serve_diagram_from_log_site --> task_03_replace_meta_refresh
  task_03_replace_meta_refresh --> task_04_raise_attempt_route_resolved
  task_04_raise_attempt_route_resolved --> task_05_author_tests_model_in_row
  task_05_author_tests_model_in_row --> task_06_render_model_in_row_and_index
  task_06_render_model_in_row_and_index --> task_07_record_visibility_surfaces_in_ssot
  task_07_record_visibility_surfaces_in_ssot --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
