<!-- guardrails:graph v1 source-sha256=085642e73a616929c34a306450235c2f88636072f7d8bf8dd9f118d7ecc794a1 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave1-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_02_dial_config_01_ssot_autonomy_delta["wave-02-dial-config/01-ssot-autonomy-delta"]
    task_wave_02_dial_config_01_ssot_autonomy_delta_gr_0["01-ssot-autonomy-documented"]:::guardrail
  end
  style task_wave_02_dial_config_01_ssot_autonomy_delta fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_dial_config_02_author_tests_autonomy_config["wave-02-dial-config/02-author-tests-autonomy-config"]
    task_wave_02_dial_config_02_author_tests_autonomy_config_gr_0["01-tests-build"]:::guardrail
    task_wave_02_dial_config_02_author_tests_autonomy_config_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_02_dial_config_02_author_tests_autonomy_config fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_dial_config_03_implement_autonomy_config["wave-02-dial-config/03-implement-autonomy-config"]
    task_wave_02_dial_config_03_implement_autonomy_config_gr_0["01-autonomy-config-tests-pass"]:::guardrail
  end
  style task_wave_02_dial_config_03_implement_autonomy_config fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_dial_config_04_author_tests_autonomy_validation["wave-02-dial-config/04-author-tests-autonomy-validation"]
    task_wave_02_dial_config_04_author_tests_autonomy_validation_gr_0["01-tests-build"]:::guardrail
    task_wave_02_dial_config_04_author_tests_autonomy_validation_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_02_dial_config_04_author_tests_autonomy_validation_gr_2["03-covers-gr2040-cases"]:::guardrail
  end
  style task_wave_02_dial_config_04_author_tests_autonomy_validation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_dial_config_05_implement_autonomy_validation["wave-02-dial-config/05-implement-autonomy-validation"]
    task_wave_02_dial_config_05_implement_autonomy_validation_gr_0["01-autonomy-validation-tests-pass"]:::guardrail
  end
  style task_wave_02_dial_config_05_implement_autonomy_validation fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_dial_config_06_author_tests_autonomous_cli["wave-02-dial-config/06-author-tests-autonomous-cli"]
    task_wave_02_dial_config_06_author_tests_autonomous_cli_gr_0["01-tests-build"]:::guardrail
    task_wave_02_dial_config_06_author_tests_autonomous_cli_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_02_dial_config_06_author_tests_autonomous_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_dial_config_07_implement_autonomous_cli["wave-02-dial-config/07-implement-autonomous-cli"]
    task_wave_02_dial_config_07_implement_autonomous_cli_gr_0["01-autonomous-cli-tests-pass"]:::guardrail
  end
  style task_wave_02_dial_config_07_implement_autonomous_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-wave2-union-clean"]:::guardrail
    plan_guardrails_1["02-wave2-solution-builds"]:::guardrail
    plan_guardrails_2["03-wave2-dial-tests-pass"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_02_dial_config_01_ssot_autonomy_delta
  plan_preflights --> task_wave_02_dial_config_02_author_tests_autonomy_config
  task_wave_02_dial_config_02_author_tests_autonomy_config --> task_wave_02_dial_config_03_implement_autonomy_config
  task_wave_02_dial_config_03_implement_autonomy_config --> task_wave_02_dial_config_04_author_tests_autonomy_validation
  task_wave_02_dial_config_03_implement_autonomy_config --> task_wave_02_dial_config_06_author_tests_autonomous_cli
  task_wave_02_dial_config_04_author_tests_autonomy_validation --> task_wave_02_dial_config_05_implement_autonomy_validation
  task_wave_02_dial_config_05_implement_autonomy_validation --> task_wave_02_dial_config_07_implement_autonomous_cli
  task_wave_02_dial_config_06_author_tests_autonomous_cli --> task_wave_02_dial_config_07_implement_autonomous_cli
  task_wave_02_dial_config_01_ssot_autonomy_delta --> plan_guardrails
  task_wave_02_dial_config_07_implement_autonomous_cli --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
