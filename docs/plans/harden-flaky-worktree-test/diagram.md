<!-- guardrails:graph v1 source-sha256=299b01c5a4a610496f63b61c987f25f9702efbf6aef6edb6b80afb980112fe8e -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-core-tests-green-excluding-target"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_harden_worktree_barrier_test["01-harden-worktree-barrier-test"]
    task_01_harden_worktree_barrier_test_gr_0["01-build-passes"]:::guardrail
    task_01_harden_worktree_barrier_test_gr_1["02-assertions-not-weakened"]:::guardrail
    task_01_harden_worktree_barrier_test_gr_2["03-no-retry-wrapper"]:::guardrail
    task_01_harden_worktree_barrier_test_gr_3["04-barrier-test-passes-repeatedly"]:::guardrail
  end
  style task_01_harden_worktree_barrier_test fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-core-tests-project-passes"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_harden_worktree_barrier_test
  task_01_harden_worktree_barrier_test --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
