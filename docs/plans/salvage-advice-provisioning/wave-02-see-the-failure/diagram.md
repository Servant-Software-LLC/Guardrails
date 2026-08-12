<!-- guardrails:graph v1 source-sha256=df52da186d4043f9ad2671dcc020e3fa3e252cd538f4ac33ca8b8b02b5bb94a1 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave1-corrections-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection["wave-02-see-the-failure/01-author-tests-bash-refusal-detection"]
    task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection_gr_0["01-tests-build"]:::guardrail
    task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_see_the_failure_02_implement_bash_refusal_detection["wave-02-see-the-failure/02-implement-bash-refusal-detection"]
    task_wave_02_see_the_failure_02_implement_bash_refusal_detection_gr_0["01-refusal-tests-pass"]:::guardrail
  end
  style task_wave_02_see_the_failure_02_implement_bash_refusal_detection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-scanner-tests-pass"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection
  task_wave_02_see_the_failure_01_author_tests_bash_refusal_detection --> task_wave_02_see_the_failure_02_implement_bash_refusal_detection
  task_wave_02_see_the_failure_02_implement_bash_refusal_detection --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
