<!-- guardrails:graph v1 source-sha256=8a4a6657677aedb8150ff9bd925574324a96ac384337fc88bea849f50d85b362 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave3-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_04_review_gate_policy_01_ssot_phase4_delta["wave-04-review-gate-policy/01-ssot-phase4-delta"]
    task_wave_04_review_gate_policy_01_ssot_phase4_delta_gr_0["01-ssot-phase4-documented"]:::guardrail
  end
  style task_wave_04_review_gate_policy_01_ssot_phase4_delta fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy["wave-04-review-gate-policy/02-author-tests-run-outcome-policy"]
    task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy_gr_0["01-tests-build"]:::guardrail
    task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy_gr_2["03-covers-outcome-cases"]:::guardrail
  end
  style task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_review_gate_policy_03_implement_run_outcome_policy["wave-04-review-gate-policy/03-implement-run-outcome-policy"]
    task_wave_04_review_gate_policy_03_implement_run_outcome_policy_gr_0["01-run-outcome-policy-tests-pass"]:::guardrail
  end
  style task_wave_04_review_gate_policy_03_implement_run_outcome_policy fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution["wave-04-review-gate-policy/04-author-tests-review-gate-resolution"]
    task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution_gr_0["01-tests-build"]:::guardrail
    task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution_gr_2["03-covers-review-gate-cases"]:::guardrail
  end
  style task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop["wave-04-review-gate-policy/05-wire-review-gate-resolution-into-wave-loop"]
    task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop_gr_0["01-review-gate-resolution-structural"]:::guardrail
    task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop_gr_1["02-no-forged-review-marker"]:::guardrail
    task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop_gr_2["03-review-gate-resolution-tests-pass"]:::guardrail
  end
  style task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier["wave-04-review-gate-policy/06-author-tests-overwatch-auto-tier"]
    task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier_gr_0["01-tests-build"]:::guardrail
    task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier_gr_2["03-covers-auto-tier-cases"]:::guardrail
  end
  style task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier["wave-04-review-gate-policy/07-implement-overwatch-auto-tier"]
    task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier_gr_0["01-auto-tier-structural"]:::guardrail
    task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier_gr_1["02-overwatch-auto-tier-tests-pass"]:::guardrail
  end
  style task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring["wave-04-review-gate-policy/08-author-tests-run-outcome-wiring"]
    task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring_gr_0["01-tests-build"]:::guardrail
    task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring_gr_2["03-covers-wiring-facts"]:::guardrail
  end
  style task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize["wave-04-review-gate-policy/09-wire-run-outcome-into-finalize"]
    task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize_gr_0["01-solution-builds"]:::guardrail
    task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize_gr_1["02-finalize-run-outcome-structural"]:::guardrail
  end
  style task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify["wave-04-review-gate-policy/10-wire-proceeded-unreviewed-exit-and-verify"]
    task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify_gr_0["01-exit-code-proceeded-unreviewed-structural"]:::guardrail
    task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify_gr_1["02-run-outcome-wiring-tests-pass"]:::guardrail
  end
  style task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-wave4-union-clean"]:::guardrail
    plan_guardrails_1["02-wave4-solution-builds"]:::guardrail
    plan_guardrails_2["03-wave4-review-gate-policy-tests-pass"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_04_review_gate_policy_01_ssot_phase4_delta
  plan_preflights --> task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy
  plan_preflights --> task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution
  plan_preflights --> task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier
  plan_preflights --> task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring
  task_wave_04_review_gate_policy_02_author_tests_run_outcome_policy --> task_wave_04_review_gate_policy_03_implement_run_outcome_policy
  task_wave_04_review_gate_policy_03_implement_run_outcome_policy --> task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize
  task_wave_04_review_gate_policy_04_author_tests_review_gate_resolution --> task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop
  task_wave_04_review_gate_policy_05_wire_review_gate_resolution_into_wave_loop --> task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize
  task_wave_04_review_gate_policy_06_author_tests_overwatch_auto_tier --> task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier
  task_wave_04_review_gate_policy_08_author_tests_run_outcome_wiring --> task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify
  task_wave_04_review_gate_policy_09_wire_run_outcome_into_finalize --> task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify
  task_wave_04_review_gate_policy_01_ssot_phase4_delta --> plan_guardrails
  task_wave_04_review_gate_policy_07_implement_overwatch_auto_tier --> plan_guardrails
  task_wave_04_review_gate_policy_10_wire_proceeded_unreviewed_exit_and_verify --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
