<!-- guardrails:graph v1 source-sha256=0e91341ed6fcaa1fc386606fde2e821707de4a3c7147556b50bbc4b65e98ea93 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_diagram_status_overlay_renderer["01-author-tests-diagram-status-overlay-renderer"]
    task_01_author_tests_diagram_status_overlay_renderer_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_diagram_status_overlay_renderer_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_diagram_status_overlay_renderer fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_diagram_status_overlay_renderer["02-implement-diagram-status-overlay-renderer"]
    task_02_implement_diagram_status_overlay_renderer_gr_0["01-build-passes"]:::guardrail
    task_02_implement_diagram_status_overlay_renderer_gr_1["02-tests-pass"]:::guardrail
  end
  style task_02_implement_diagram_status_overlay_renderer fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_diagram_observer["03-author-tests-diagram-observer"]
    task_03_author_tests_diagram_observer_gr_0["01-build-passes"]:::guardrail
    task_03_author_tests_diagram_observer_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_03_author_tests_diagram_observer fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_diagram_observer["04-implement-diagram-observer"]
    task_04_implement_diagram_observer_gr_0["01-build-passes"]:::guardrail
    task_04_implement_diagram_observer_gr_1["02-tests-pass"]:::guardrail
  end
  style task_04_implement_diagram_observer fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_diagramobserver_wiring["05-author-tests-diagramobserver-wiring"]
    task_05_author_tests_diagramobserver_wiring_gr_0["01-build-passes"]:::guardrail
    task_05_author_tests_diagramobserver_wiring_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_05_author_tests_diagramobserver_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_wire_diagramobserver_into_runcommand["06-wire-diagramobserver-into-runcommand"]
    task_06_wire_diagramobserver_into_runcommand_gr_0["01-build-passes"]:::guardrail
    task_06_wire_diagramobserver_into_runcommand_gr_1["02-composition-root-wiring-verified"]:::guardrail
  end
  style task_06_wire_diagramobserver_into_runcommand fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_diagram_search_box["07-author-tests-diagram-search-box"]
    task_07_author_tests_diagram_search_box_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_diagram_search_box_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_07_author_tests_diagram_search_box fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_implement_diagram_search_box["08-implement-diagram-search-box"]
    task_08_implement_diagram_search_box_gr_0["01-build-passes"]:::guardrail
    task_08_implement_diagram_search_box_gr_1["02-tests-pass"]:::guardrail
  end
  style task_08_implement_diagram_search_box fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-no-conflict-markers"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_diagram_status_overlay_renderer
  task_01_author_tests_diagram_status_overlay_renderer --> task_02_implement_diagram_status_overlay_renderer
  task_02_implement_diagram_status_overlay_renderer --> task_03_author_tests_diagram_observer
  task_02_implement_diagram_status_overlay_renderer --> task_05_author_tests_diagramobserver_wiring
  task_02_implement_diagram_status_overlay_renderer --> task_07_author_tests_diagram_search_box
  task_03_author_tests_diagram_observer --> task_04_implement_diagram_observer
  task_04_implement_diagram_observer --> task_06_wire_diagramobserver_into_runcommand
  task_05_author_tests_diagramobserver_wiring --> task_06_wire_diagramobserver_into_runcommand
  task_07_author_tests_diagram_search_box --> task_08_implement_diagram_search_box
  task_06_wire_diagramobserver_into_runcommand --> plan_guardrails
  task_08_implement_diagram_search_box --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
