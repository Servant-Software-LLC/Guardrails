<!-- guardrails:graph v1 source-sha256=fd91c948fd43aac2144bd01b81816cbe2beea214ea0b1f407807668a9a7bfc53 body-sha256=471d042a4e298e3b40a5b1c66ad01a27496be772da7320afed9d32804e5334a9 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_01_contract_and_validate_01_record_lite_profile_contract["wave-01-contract-and-validate/01-record-lite-profile-contract"]
    task_wave_01_contract_and_validate_01_record_lite_profile_contract_gr_0["01-ssot-records-lite-contract"]:::guardrail
    task_wave_01_contract_and_validate_01_record_lite_profile_contract_gr_1["02-gr2090-reserved-by-name"]:::guardrail
    task_wave_01_contract_and_validate_01_record_lite_profile_contract_gr_2["03-core-tests-build"]:::guardrail
    task_wave_01_contract_and_validate_01_record_lite_profile_contract_gr_3["04-contract-tests-pass"]:::guardrail
  end
  style task_wave_01_contract_and_validate_01_record_lite_profile_contract fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_02_add_lite_script_host["wave-01-contract-and-validate/02-add-lite-script-host"]
    task_wave_01_contract_and_validate_02_add_lite_script_host_gr_0["01-integration-tests-build"]:::guardrail
    task_wave_01_contract_and_validate_02_add_lite_script_host_gr_1["02-script-host-tests-pass"]:::guardrail
  end
  style task_wave_01_contract_and_validate_02_add_lite_script_host fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_03_author_tests_lite_validate_load["wave-01-contract-and-validate/03-author-tests-lite-validate-load"]
    task_wave_01_contract_and_validate_03_author_tests_lite_validate_load_gr_0["01-build-passes"]:::guardrail
    task_wave_01_contract_and_validate_03_author_tests_lite_validate_load_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_01_contract_and_validate_03_author_tests_lite_validate_load fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_04_implement_lite_validate_load["wave-01-contract-and-validate/04-implement-lite-validate-load"]
    task_wave_01_contract_and_validate_04_implement_lite_validate_load_gr_0["01-build-passes"]:::guardrail
    task_wave_01_contract_and_validate_04_implement_lite_validate_load_gr_1["02-load-tests-pass"]:::guardrail
    task_wave_01_contract_and_validate_04_implement_lite_validate_load_gr_2["03-script-preamble"]:::guardrail
  end
  style task_wave_01_contract_and_validate_04_implement_lite_validate_load fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_05_author_tests_lite_validate_graph["wave-01-contract-and-validate/05-author-tests-lite-validate-graph"]
    task_wave_01_contract_and_validate_05_author_tests_lite_validate_graph_gr_0["01-build-passes"]:::guardrail
    task_wave_01_contract_and_validate_05_author_tests_lite_validate_graph_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_01_contract_and_validate_05_author_tests_lite_validate_graph fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_06_implement_lite_validate_graph["wave-01-contract-and-validate/06-implement-lite-validate-graph"]
    task_wave_01_contract_and_validate_06_implement_lite_validate_graph_gr_0["01-build-passes"]:::guardrail
    task_wave_01_contract_and_validate_06_implement_lite_validate_graph_gr_1["02-graph-tests-pass"]:::guardrail
    task_wave_01_contract_and_validate_06_implement_lite_validate_graph_gr_2["03-load-tests-still-pass"]:::guardrail
    task_wave_01_contract_and_validate_06_implement_lite_validate_graph_gr_3["04-script-preamble"]:::guardrail
  end
  style task_wave_01_contract_and_validate_06_implement_lite_validate_graph fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_07_author_tests_lite_validate_subset["wave-01-contract-and-validate/07-author-tests-lite-validate-subset"]
    task_wave_01_contract_and_validate_07_author_tests_lite_validate_subset_gr_0["01-build-passes"]:::guardrail
    task_wave_01_contract_and_validate_07_author_tests_lite_validate_subset_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_01_contract_and_validate_07_author_tests_lite_validate_subset fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity["wave-01-contract-and-validate/08-implement-lite-validate-subset-and-parity"]
    task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity_gr_0["01-build-passes"]:::guardrail
    task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity_gr_1["02-subset-and-parity-tests-pass"]:::guardrail
    task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity_gr_2["03-earlier-validate-tests-still-pass"]:::guardrail
  end
  style task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_09_author_tests_lite_hashes["wave-01-contract-and-validate/09-author-tests-lite-hashes"]
    task_wave_01_contract_and_validate_09_author_tests_lite_hashes_gr_0["01-integration-tests-build"]:::guardrail
    task_wave_01_contract_and_validate_09_author_tests_lite_hashes_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_01_contract_and_validate_09_author_tests_lite_hashes fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_10_implement_lite_hashes["wave-01-contract-and-validate/10-implement-lite-hashes"]
    task_wave_01_contract_and_validate_10_implement_lite_hashes_gr_0["01-integration-tests-build"]:::guardrail
    task_wave_01_contract_and_validate_10_implement_lite_hashes_gr_1["02-tests-pass"]:::guardrail
    task_wave_01_contract_and_validate_10_implement_lite_hashes_gr_2["03-no-stub-markers"]:::guardrail
  end
  style task_wave_01_contract_and_validate_10_implement_lite_hashes fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_11_author_tests_lite_lock["wave-01-contract-and-validate/11-author-tests-lite-lock"]
    task_wave_01_contract_and_validate_11_author_tests_lite_lock_gr_0["01-integration-tests-build"]:::guardrail
    task_wave_01_contract_and_validate_11_author_tests_lite_lock_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_01_contract_and_validate_11_author_tests_lite_lock fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_contract_and_validate_12_implement_lite_lock["wave-01-contract-and-validate/12-implement-lite-lock"]
    task_wave_01_contract_and_validate_12_implement_lite_lock_gr_0["01-integration-tests-build"]:::guardrail
    task_wave_01_contract_and_validate_12_implement_lite_lock_gr_1["02-tests-pass"]:::guardrail
    task_wave_01_contract_and_validate_12_implement_lite_lock_gr_2["03-uses-hash-module-no-stub"]:::guardrail
  end
  style task_wave_01_contract_and_validate_12_implement_lite_lock fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-lite-tests-pass"]:::guardrail
    plan_guardrails_2["03-no-stubs-remain"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_01_contract_and_validate_01_record_lite_profile_contract
  plan_preflights --> task_wave_01_contract_and_validate_02_add_lite_script_host
  task_wave_01_contract_and_validate_02_add_lite_script_host --> task_wave_01_contract_and_validate_03_author_tests_lite_validate_load
  task_wave_01_contract_and_validate_02_add_lite_script_host --> task_wave_01_contract_and_validate_05_author_tests_lite_validate_graph
  task_wave_01_contract_and_validate_02_add_lite_script_host --> task_wave_01_contract_and_validate_07_author_tests_lite_validate_subset
  task_wave_01_contract_and_validate_02_add_lite_script_host --> task_wave_01_contract_and_validate_09_author_tests_lite_hashes
  task_wave_01_contract_and_validate_02_add_lite_script_host --> task_wave_01_contract_and_validate_11_author_tests_lite_lock
  task_wave_01_contract_and_validate_03_author_tests_lite_validate_load --> task_wave_01_contract_and_validate_04_implement_lite_validate_load
  task_wave_01_contract_and_validate_03_author_tests_lite_validate_load --> task_wave_01_contract_and_validate_05_author_tests_lite_validate_graph
  task_wave_01_contract_and_validate_03_author_tests_lite_validate_load --> task_wave_01_contract_and_validate_07_author_tests_lite_validate_subset
  task_wave_01_contract_and_validate_04_implement_lite_validate_load --> task_wave_01_contract_and_validate_06_implement_lite_validate_graph
  task_wave_01_contract_and_validate_04_implement_lite_validate_load --> task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity
  task_wave_01_contract_and_validate_05_author_tests_lite_validate_graph --> task_wave_01_contract_and_validate_06_implement_lite_validate_graph
  task_wave_01_contract_and_validate_06_implement_lite_validate_graph --> task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity
  task_wave_01_contract_and_validate_07_author_tests_lite_validate_subset --> task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity
  task_wave_01_contract_and_validate_09_author_tests_lite_hashes --> task_wave_01_contract_and_validate_10_implement_lite_hashes
  task_wave_01_contract_and_validate_10_implement_lite_hashes --> task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity
  task_wave_01_contract_and_validate_10_implement_lite_hashes --> task_wave_01_contract_and_validate_12_implement_lite_lock
  task_wave_01_contract_and_validate_11_author_tests_lite_lock --> task_wave_01_contract_and_validate_12_implement_lite_lock
  task_wave_01_contract_and_validate_01_record_lite_profile_contract --> plan_guardrails
  task_wave_01_contract_and_validate_08_implement_lite_validate_subset_and_parity --> plan_guardrails
  task_wave_01_contract_and_validate_12_implement_lite_lock --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
