<!-- guardrails:graph v1 source-sha256=82c7e32f5634688958b42ff55eef2568c7031ece03d6b6271f6a6cd4f526215e -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave3-injection-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_04_fix_the_advice_01_author_tests_salvage_advice["wave-04-fix-the-advice/01-author-tests-salvage-advice"]
    task_wave_04_fix_the_advice_01_author_tests_salvage_advice_gr_0["01-tests-build"]:::guardrail
    task_wave_04_fix_the_advice_01_author_tests_salvage_advice_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_wave_04_fix_the_advice_01_author_tests_salvage_advice fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_fix_the_advice_02_implement_salvage_advice["wave-04-fix-the-advice/02-implement-salvage-advice"]
    task_wave_04_fix_the_advice_02_implement_salvage_advice_gr_0["01-advice-tests-pass"]:::guardrail
    task_wave_04_fix_the_advice_02_implement_salvage_advice_gr_1["02-no-ungranted-command-emitted"]:::guardrail
  end
  style task_wave_04_fix_the_advice_02_implement_salvage_advice fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory["wave-04-fix-the-advice/03-reconcile-promptcomposer-advisory"]
    task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory_gr_0["01-advisory-has-no-unusable-recipe"]:::guardrail
    task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory_gr_1["02-composer-tests-pass"]:::guardrail
  end
  style task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_fix_the_advice_04_reconcile_containment_hook_message["wave-04-fix-the-advice/04-reconcile-containment-hook-message"]
    task_wave_04_fix_the_advice_04_reconcile_containment_hook_message_gr_0["01-hook-message-reconciled"]:::guardrail
    task_wave_04_fix_the_advice_04_reconcile_containment_hook_message_gr_1["02-hook-logic-unchanged"]:::guardrail
  end
  style task_wave_04_fix_the_advice_04_reconcile_containment_hook_message fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-union-conflict-marker-free"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_04_fix_the_advice_01_author_tests_salvage_advice
  task_wave_04_fix_the_advice_01_author_tests_salvage_advice --> task_wave_04_fix_the_advice_02_implement_salvage_advice
  task_wave_04_fix_the_advice_02_implement_salvage_advice --> task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory
  task_wave_04_fix_the_advice_02_implement_salvage_advice --> task_wave_04_fix_the_advice_04_reconcile_containment_hook_message
  task_wave_04_fix_the_advice_03_reconcile_promptcomposer_advisory --> plan_guardrails
  task_wave_04_fix_the_advice_04_reconcile_containment_hook_message --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
