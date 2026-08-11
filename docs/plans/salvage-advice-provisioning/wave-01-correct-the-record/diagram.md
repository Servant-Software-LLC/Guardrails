<!-- guardrails:graph v1 source-sha256=c672344f57458462ebb1fcd68340100284e4c0ce5bf00e5e58e1e84075535411 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-retrypolicy-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-salvage-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_01_correct_the_record_01_correct_retrypolicy_rationale["wave-01-correct-the-record/01-correct-retrypolicy-rationale"]
    task_wave_01_correct_the_record_01_correct_retrypolicy_rationale_gr_0["01-false-claims-removed"]:::guardrail
    task_wave_01_correct_the_record_01_correct_retrypolicy_rationale_gr_1["02-builds"]:::guardrail
  end
  style task_wave_01_correct_the_record_01_correct_retrypolicy_rationale fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_correct_the_record_02_fix_ssot_drift["wave-01-correct-the-record/02-fix-ssot-drift"]
    task_wave_01_correct_the_record_02_fix_ssot_drift_gr_0["01-ssot-names-granted-route"]:::guardrail
  end
  style task_wave_01_correct_the_record_02_fix_ssot_drift fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording["wave-01-correct-the-record/03-correct-plan-breakdown-allowlist-wording"]
    task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording_gr_0["01-floor-not-ceiling-wording"]:::guardrail
  end
  style task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording["wave-01-correct-the-record/04-correct-guardrails-review-allowlist-wording"]
    task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording_gr_0["01-floor-not-ceiling-wording"]:::guardrail
  end
  style task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording["wave-01-correct-the-record/05-correct-guardrails-domain-knowledge-allowlist-wording"]
    task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording_gr_0["01-floor-not-ceiling-wording"]:::guardrail
  end
  style task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-corrections-union-intact"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_01_correct_the_record_01_correct_retrypolicy_rationale
  plan_preflights --> task_wave_01_correct_the_record_02_fix_ssot_drift
  plan_preflights --> task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording
  plan_preflights --> task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording
  plan_preflights --> task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording
  task_wave_01_correct_the_record_01_correct_retrypolicy_rationale --> plan_guardrails
  task_wave_01_correct_the_record_02_fix_ssot_drift --> plan_guardrails
  task_wave_01_correct_the_record_03_correct_plan_breakdown_allowlist_wording --> plan_guardrails
  task_wave_01_correct_the_record_04_correct_guardrails_review_allowlist_wording --> plan_guardrails
  task_wave_01_correct_the_record_05_correct_guardrails_domain_knowledge_allowlist_wording --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
