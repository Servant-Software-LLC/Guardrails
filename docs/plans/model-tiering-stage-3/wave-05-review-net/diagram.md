<!-- guardrails:graph v1 source-sha256=1519636f3f752cf419159640ae650e898f78a55dfd9e939c86b81aa915c219c5 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-tier-vocabulary-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_05_review_net_01_author_tests_tier_classification_audit["wave-05-review-net/01-author-tests-tier-classification-audit"]
    task_wave_05_review_net_01_author_tests_tier_classification_audit_gr_0["01-tests-build"]:::guardrail
    task_wave_05_review_net_01_author_tests_tier_classification_audit_gr_1["02-tests-red-census"]:::guardrail
  end
  style task_wave_05_review_net_01_author_tests_tier_classification_audit fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_05_review_net_02_implement_tier_classification_audit["wave-05-review-net/02-implement-tier-classification-audit"]
    task_wave_05_review_net_02_implement_tier_classification_audit_pf_0["01-stub-delivered"]:::preflight
    task_wave_05_review_net_02_implement_tier_classification_audit_gr_0["01-no-diagnostic-code-no-validator"]:::guardrail
    task_wave_05_review_net_02_implement_tier_classification_audit_gr_1["02-audit-tests-pass"]:::guardrail
  end
  style task_wave_05_review_net_02_implement_tier_classification_audit fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_05_review_net_03_author_tests_review_net_doctrine["wave-05-review-net/03-author-tests-review-net-doctrine"]
    task_wave_05_review_net_03_author_tests_review_net_doctrine_gr_0["01-tests-build"]:::guardrail
    task_wave_05_review_net_03_author_tests_review_net_doctrine_gr_1["02-anchors-red-census"]:::guardrail
  end
  style task_wave_05_review_net_03_author_tests_review_net_doctrine fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_05_review_net_04_add_model_appropriateness_probe["wave-05-review-net/04-add-model-appropriateness-probe"]
    task_wave_05_review_net_04_add_model_appropriateness_probe_pf_0["01-anchors-delivered"]:::preflight
    task_wave_05_review_net_04_add_model_appropriateness_probe_gr_0["01-review-skill-intact"]:::guardrail
    task_wave_05_review_net_04_add_model_appropriateness_probe_gr_1["02-anchor-tests-pass"]:::guardrail
  end
  style task_wave_05_review_net_04_add_model_appropriateness_probe fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-suites-pass"]:::guardrail
    plan_guardrails_2["03-wave-deliverables-present"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_05_review_net_01_author_tests_tier_classification_audit
  plan_preflights --> task_wave_05_review_net_03_author_tests_review_net_doctrine
  task_wave_05_review_net_01_author_tests_tier_classification_audit --> task_wave_05_review_net_02_implement_tier_classification_audit
  task_wave_05_review_net_03_author_tests_review_net_doctrine --> task_wave_05_review_net_04_add_model_appropriateness_probe
  task_wave_05_review_net_02_implement_tier_classification_audit --> plan_guardrails
  task_wave_05_review_net_04_add_model_appropriateness_probe --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
