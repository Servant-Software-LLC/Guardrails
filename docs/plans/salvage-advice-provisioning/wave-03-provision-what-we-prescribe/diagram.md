<!-- guardrails:graph v1 source-sha256=07beb175802b7fcaaa12f0317a2de8d4749e87988ede1230f845301fab8a15cd -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave2-scanner-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection["wave-03-provision-what-we-prescribe/01-author-tests-grant-injection"]
    task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection_gr_0["01-tests-build"]:::guardrail
    task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection_gr_2["03-no-write-verb-asserted"]:::guardrail
  end
  style task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_provision_what_we_prescribe_02_implement_grant_injection["wave-03-provision-what-we-prescribe/02-implement-grant-injection"]
    task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_0["01-injection-tests-pass"]:::guardrail
    task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_1["02-no-write-verb-injected"]:::guardrail
    task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_2["03-golden-args-tests-pass"]:::guardrail
    task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_3["04-golden-coverage-preserved"]:::guardrail
    task_wave_03_provision_what_we_prescribe_02_implement_grant_injection_gr_4["05-ssot-contract-line-landed"]:::guardrail
  end
  style task_wave_03_provision_what_we_prescribe_02_implement_grant_injection fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance["wave-03-provision-what-we-prescribe/03-record-injected-grants-in-provenance"]
    task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance_gr_0["01-provenance-records-injection"]:::guardrail
    task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance_gr_1["02-core-builds"]:::guardrail
    task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance_gr_2["03-log-header-echoes-injected-grants"]:::guardrail
  end
  style task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-injection-tests-pass"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection
  task_wave_03_provision_what_we_prescribe_01_author_tests_grant_injection --> task_wave_03_provision_what_we_prescribe_02_implement_grant_injection
  task_wave_03_provision_what_we_prescribe_02_implement_grant_injection --> task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance
  task_wave_03_provision_what_we_prescribe_03_record_injected_grants_in_provenance --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
