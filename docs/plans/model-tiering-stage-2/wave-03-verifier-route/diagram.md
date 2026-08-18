<!-- guardrails:graph v1 source-sha256=14d8a7cb65c8c3c5b160321304cf6762408bdf3e7f37da79bd5f136bf0bda16b -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave-02-artifacts-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_03_verifier_route_01_author_tests_judge_resolution["wave-03-verifier-route/01-author-tests-judge-resolution"]
    task_wave_03_verifier_route_01_author_tests_judge_resolution_gr_0["01-build-passes"]:::guardrail
    task_wave_03_verifier_route_01_author_tests_judge_resolution_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_verifier_route_01_author_tests_judge_resolution_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_03_verifier_route_01_author_tests_judge_resolution fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_verifier_route_02_implement_judge_resolution["wave-03-verifier-route/02-implement-judge-resolution"]
    task_wave_03_verifier_route_02_implement_judge_resolution_gr_0["01-judge-resolution-tests-pass"]:::guardrail
  end
  style task_wave_03_verifier_route_02_implement_judge_resolution fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_verifier_route_03_author_tests_judge_provenance_schema["wave-03-verifier-route/03-author-tests-judge-provenance-schema"]
    task_wave_03_verifier_route_03_author_tests_judge_provenance_schema_gr_0["01-build-passes"]:::guardrail
    task_wave_03_verifier_route_03_author_tests_judge_provenance_schema_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_03_verifier_route_03_author_tests_judge_provenance_schema_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_03_verifier_route_03_author_tests_judge_provenance_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_verifier_route_04_implement_judge_provenance_schema["wave-03-verifier-route/04-implement-judge-provenance-schema"]
    task_wave_03_verifier_route_04_implement_judge_provenance_schema_gr_0["01-schema-tests-pass"]:::guardrail
  end
  style task_wave_03_verifier_route_04_implement_judge_provenance_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_verifier_route_05_author_tests_stage2_conformance_judge["wave-03-verifier-route/05-author-tests-stage2-conformance-judge"]
    task_wave_03_verifier_route_05_author_tests_stage2_conformance_judge_gr_0["01-covers-required-judge-behaviors"]:::guardrail
    task_wave_03_verifier_route_05_author_tests_stage2_conformance_judge_gr_1["02-build-passes"]:::guardrail
    task_wave_03_verifier_route_05_author_tests_stage2_conformance_judge_gr_2["03-tests-fail-on-current-code"]:::guardrail
  end
  style task_wave_03_verifier_route_05_author_tests_stage2_conformance_judge fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_verifier_route_06_wire_judge_resolution_into_guardrail_runner["wave-03-verifier-route/06-wire-judge-resolution-into-guardrail-runner"]
    task_wave_03_verifier_route_06_wire_judge_resolution_into_guardrail_runner_gr_0["01-judge-route-actually-used"]:::guardrail
    task_wave_03_verifier_route_06_wire_judge_resolution_into_guardrail_runner_gr_1["02-conformance-judge-tests-pass"]:::guardrail
  end
  style task_wave_03_verifier_route_06_wire_judge_resolution_into_guardrail_runner fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_verifier_route_07_carry_judge_provenance_to_journal["wave-03-verifier-route/07-carry-judge-provenance-to-journal"]
    task_wave_03_verifier_route_07_carry_judge_provenance_to_journal_gr_0["01-both-journal-paths-carry-judge"]:::guardrail
    task_wave_03_verifier_route_07_carry_judge_provenance_to_journal_gr_1["02-conformance-suite-passes"]:::guardrail
  end
  style task_wave_03_verifier_route_07_carry_judge_provenance_to_journal fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_verifier_route_08_author_tests_verifier_advisory["wave-03-verifier-route/08-author-tests-verifier-advisory"]
    task_wave_03_verifier_route_08_author_tests_verifier_advisory_gr_0["01-build-passes"]:::guardrail
    task_wave_03_verifier_route_08_author_tests_verifier_advisory_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_verifier_route_08_author_tests_verifier_advisory_gr_2["03-covers-advisory-behaviors"]:::guardrail
  end
  style task_wave_03_verifier_route_08_author_tests_verifier_advisory fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_verifier_route_09_implement_verifier_advisory["wave-03-verifier-route/09-implement-verifier-advisory"]
    task_wave_03_verifier_route_09_implement_verifier_advisory_gr_0["01-advisory-tests-pass"]:::guardrail
  end
  style task_wave_03_verifier_route_09_implement_verifier_advisory fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_verifier_route_10_land_ssot_judge_deltas["wave-03-verifier-route/10-land-ssot-judge-deltas"]
    task_wave_03_verifier_route_10_land_ssot_judge_deltas_gr_0["01-ssot-judge-deltas-landed"]:::guardrail
  end
  style task_wave_03_verifier_route_10_land_ssot_judge_deltas fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-wave-union-builds"]:::guardrail
    plan_guardrails_1["02-stage2-conformance-green"]:::guardrail
    plan_guardrails_2["03-wave3-unit-suites-green"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_03_verifier_route_01_author_tests_judge_resolution
  plan_preflights --> task_wave_03_verifier_route_03_author_tests_judge_provenance_schema
  task_wave_03_verifier_route_01_author_tests_judge_resolution --> task_wave_03_verifier_route_02_implement_judge_resolution
  task_wave_03_verifier_route_02_implement_judge_resolution --> task_wave_03_verifier_route_05_author_tests_stage2_conformance_judge
  task_wave_03_verifier_route_02_implement_judge_resolution --> task_wave_03_verifier_route_08_author_tests_verifier_advisory
  task_wave_03_verifier_route_03_author_tests_judge_provenance_schema --> task_wave_03_verifier_route_04_implement_judge_provenance_schema
  task_wave_03_verifier_route_04_implement_judge_provenance_schema --> task_wave_03_verifier_route_05_author_tests_stage2_conformance_judge
  task_wave_03_verifier_route_05_author_tests_stage2_conformance_judge --> task_wave_03_verifier_route_06_wire_judge_resolution_into_guardrail_runner
  task_wave_03_verifier_route_06_wire_judge_resolution_into_guardrail_runner --> task_wave_03_verifier_route_07_carry_judge_provenance_to_journal
  task_wave_03_verifier_route_07_carry_judge_provenance_to_journal --> task_wave_03_verifier_route_10_land_ssot_judge_deltas
  task_wave_03_verifier_route_08_author_tests_verifier_advisory --> task_wave_03_verifier_route_09_implement_verifier_advisory
  task_wave_03_verifier_route_09_implement_verifier_advisory --> task_wave_03_verifier_route_10_land_ssot_judge_deltas
  task_wave_03_verifier_route_10_land_ssot_judge_deltas --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
