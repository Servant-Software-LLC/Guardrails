<!-- guardrails:graph v1 source-sha256=1d479b89706394df361789db755e554c4a9bc6d725dd798dbe3f2be9b44bea51 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_runner_kind_and_axes["01-author-tests-runner-kind-and-axes"]
    task_01_author_tests_runner_kind_and_axes_gr_0["01-tests-build"]:::guardrail
    task_01_author_tests_runner_kind_and_axes_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_01_author_tests_runner_kind_and_axes_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_01_author_tests_runner_kind_and_axes fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_runner_kind_and_axes["02-implement-runner-kind-and-axes"]
    task_02_implement_runner_kind_and_axes_gr_0["01-tests-pass"]:::guardrail
    task_02_implement_runner_kind_and_axes_gr_1["02-ssot-updated"]:::guardrail
  end
  style task_02_implement_runner_kind_and_axes fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_registry_kind_dispatch["03-author-tests-registry-kind-dispatch"]
    task_03_author_tests_registry_kind_dispatch_gr_0["01-tests-build"]:::guardrail
    task_03_author_tests_registry_kind_dispatch_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_03_author_tests_registry_kind_dispatch_gr_2["03-asserts-no-silent-fallback"]:::guardrail
  end
  style task_03_author_tests_registry_kind_dispatch fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_registry_kind_dispatch["04-implement-registry-kind-dispatch"]
    task_04_implement_registry_kind_dispatch_gr_0["01-tests-pass"]:::guardrail
    task_04_implement_registry_kind_dispatch_gr_1["02-no-silent-claude-fallback"]:::guardrail
  end
  style task_04_implement_registry_kind_dispatch fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_action_tier["05-author-tests-action-tier"]
    task_05_author_tests_action_tier_gr_0["01-tests-build"]:::guardrail
    task_05_author_tests_action_tier_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_05_author_tests_action_tier_gr_2["03-covers-optionality"]:::guardrail
  end
  style task_05_author_tests_action_tier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_implement_action_tier["06-implement-action-tier"]
    task_06_implement_action_tier_gr_0["01-tests-pass"]:::guardrail
    task_06_implement_action_tier_gr_1["02-ssot-section-3-updated"]:::guardrail
  end
  style task_06_implement_action_tier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_prove_invariant_7_gate["07-prove-invariant-7-gate"]
    task_07_prove_invariant_7_gate_gr_0["01-proof-tests-pass"]:::guardrail
    task_07_prove_invariant_7_gate_gr_1["02-both-mechanisms-present"]:::guardrail
  end
  style task_07_prove_invariant_7_gate fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_update_plan_breakdown_skill_tiering["08-update-plan-breakdown-skill-tiering"]
    task_08_update_plan_breakdown_skill_tiering_gr_0["01-tiering-doctrine-present"]:::guardrail
  end
  style task_08_update_plan_breakdown_skill_tiering fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_update_guardrails_review_model_availability["09-update-guardrails-review-model-availability"]
    task_09_update_guardrails_review_model_availability_gr_0["01-check-documented"]:::guardrail
    task_09_update_guardrails_review_model_availability_gr_1["02-stays-read-only"]:::guardrail
  end
  style task_09_update_guardrails_review_model_availability fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-union-intact"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_runner_kind_and_axes
  plan_preflights --> task_03_author_tests_registry_kind_dispatch
  plan_preflights --> task_05_author_tests_action_tier
  task_01_author_tests_runner_kind_and_axes --> task_02_implement_runner_kind_and_axes
  task_02_implement_runner_kind_and_axes --> task_04_implement_registry_kind_dispatch
  task_02_implement_runner_kind_and_axes --> task_09_update_guardrails_review_model_availability
  task_03_author_tests_registry_kind_dispatch --> task_04_implement_registry_kind_dispatch
  task_05_author_tests_action_tier --> task_06_implement_action_tier
  task_06_implement_action_tier --> task_08_update_plan_breakdown_skill_tiering
  task_08_update_plan_breakdown_skill_tiering --> task_07_prove_invariant_7_gate
  task_04_implement_registry_kind_dispatch --> plan_guardrails
  task_07_prove_invariant_7_gate --> plan_guardrails
  task_09_update_guardrails_review_model_availability --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
