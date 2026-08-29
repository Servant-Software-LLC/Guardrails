<!-- guardrails:graph v1 source-sha256=52b6576d8aac2ba3ec9e20375f6e924a2b879fd0dbb5be059e2bbed2d359f4c6 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-preflight-phase-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_sample_verifier["01-author-tests-sample-verifier"]
    task_01_author_tests_sample_verifier_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_sample_verifier_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_sample_verifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_sample_verifier["02-implement-sample-verifier"]
    task_02_implement_sample_verifier_gr_0["01-build-passes"]:::guardrail
    task_02_implement_sample_verifier_gr_1["02-sample-verifier-tests-pass"]:::guardrail
  end
  style task_02_implement_sample_verifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_add_samples_verify_command["03-add-samples-verify-command"]
    task_03_add_samples_verify_command_gr_0["01-verb-drives-the-shared-verifier"]:::guardrail
    task_03_add_samples_verify_command_gr_1["02-build-passes"]:::guardrail
    task_03_add_samples_verify_command_gr_2["03-verb-is-reachable-and-catches-a-bad-pair"]:::guardrail
    task_03_add_samples_verify_command_gr_3["04-samples-command-tests-pass"]:::guardrail
  end
  style task_03_add_samples_verify_command fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_author_tests_verifier_wiring["04-author-tests-verifier-wiring"]
    task_04_author_tests_verifier_wiring_gr_0["01-test-drives-the-real-phase"]:::guardrail
    task_04_author_tests_verifier_wiring_gr_1["02-build-passes"]:::guardrail
    task_04_author_tests_verifier_wiring_gr_2["03-tests-fail-on-unwired-phase"]:::guardrail
  end
  style task_04_author_tests_verifier_wiring fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_wire_verifier_into_preflight["05-wire-verifier-into-preflight"]
    task_05_wire_verifier_into_preflight_gr_0["01-wiring-test-drives-the-real-phase"]:::guardrail
    task_05_wire_verifier_into_preflight_gr_1["02-build-passes"]:::guardrail
    task_05_wire_verifier_into_preflight_gr_2["03-wiring-tests-pass"]:::guardrail
    task_05_wire_verifier_into_preflight_gr_3["04-preflight-phase-regression"]:::guardrail
  end
  style task_05_wire_verifier_into_preflight fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_record_samples_verify_in_ssot["06-record-samples-verify-in-ssot"]
    task_06_record_samples_verify_in_ssot_gr_0["01-ssot-records-the-verb-and-the-step"]:::guardrail
    task_06_record_samples_verify_in_ssot_gr_1["02-domain-knowledge-records-the-verb-and-the-step"]:::guardrail
  end
  style task_06_record_samples_verify_in_ssot fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-core-suite-passes"]:::guardrail
    plan_guardrails_2["03-integration-suite-passes"]:::guardrail
    plan_guardrails_3["04-union-artifacts-sound"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_sample_verifier
  task_01_author_tests_sample_verifier --> task_02_implement_sample_verifier
  task_02_implement_sample_verifier --> task_03_add_samples_verify_command
  task_03_add_samples_verify_command --> task_04_author_tests_verifier_wiring
  task_04_author_tests_verifier_wiring --> task_05_wire_verifier_into_preflight
  task_05_wire_verifier_into_preflight --> task_06_record_samples_verify_in_ssot
  task_06_record_samples_verify_in_ssot --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
