<!-- guardrails:graph v1 source-sha256=4e96589c5c52428422f98d3b83fb623854d362dba6886b51f0b976b46eebe185 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
    plan_preflights_1["02-baseline-integration-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_corpus_store["01-author-tests-corpus-store"]
    task_01_author_tests_corpus_store_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_corpus_store_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_corpus_store fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_corpus_store["02-implement-corpus-store"]
    task_02_implement_corpus_store_gr_0["01-build-passes"]:::guardrail
    task_02_implement_corpus_store_gr_1["02-corpus-store-tests-pass"]:::guardrail
  end
  style task_02_implement_corpus_store fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_failure_classifier["03-author-tests-failure-classifier"]
    task_03_author_tests_failure_classifier_gr_0["01-build-passes"]:::guardrail
    task_03_author_tests_failure_classifier_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_03_author_tests_failure_classifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_failure_classifier["04-implement-failure-classifier"]
    task_04_implement_failure_classifier_gr_0["01-build-passes"]:::guardrail
    task_04_implement_failure_classifier_gr_1["02-failure-classifier-tests-pass"]:::guardrail
  end
  style task_04_implement_failure_classifier fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_journal_etl["05-author-tests-journal-etl"]
    task_05_author_tests_journal_etl_gr_0["01-build-passes"]:::guardrail
    task_05_author_tests_journal_etl_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_05_author_tests_journal_etl fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_implement_journal_etl["06-implement-journal-etl"]
    task_06_implement_journal_etl_gr_0["01-build-passes"]:::guardrail
    task_06_implement_journal_etl_gr_1["02-journal-etl-tests-pass"]:::guardrail
  end
  style task_06_implement_journal_etl fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_corpus_report["07-author-tests-corpus-report"]
    task_07_author_tests_corpus_report_gr_0["01-build-passes"]:::guardrail
    task_07_author_tests_corpus_report_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_07_author_tests_corpus_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_implement_corpus_report["08-implement-corpus-report"]
    task_08_implement_corpus_report_gr_0["01-build-passes"]:::guardrail
    task_08_implement_corpus_report_gr_1["02-corpus-report-tests-pass"]:::guardrail
  end
  style task_08_implement_corpus_report fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_author_tests_telemetry_command["09-author-tests-telemetry-command"]
    task_09_author_tests_telemetry_command_gr_0["01-build-passes"]:::guardrail
    task_09_author_tests_telemetry_command_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_09_author_tests_telemetry_command fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_implement_telemetry_command["10-implement-telemetry-command"]
    task_10_implement_telemetry_command_gr_0["01-build-passes"]:::guardrail
    task_10_implement_telemetry_command_gr_1["02-telemetry-command-tests-pass"]:::guardrail
  end
  style task_10_implement_telemetry_command fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_11_wire_telemetry_command["11-wire-telemetry-command"]
    task_11_wire_telemetry_command_gr_0["01-build-passes"]:::guardrail
    task_11_wire_telemetry_command_gr_1["02-verb-reachable-from-real-root"]:::guardrail
  end
  style task_11_wire_telemetry_command fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_12_author_tests_run_end_ingest["12-author-tests-run-end-ingest"]
    task_12_author_tests_run_end_ingest_gr_0["01-build-passes"]:::guardrail
    task_12_author_tests_run_end_ingest_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_12_author_tests_run_end_ingest fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_13_wire_run_end_ingest["13-wire-run-end-ingest"]
    task_13_wire_run_end_ingest_gr_0["01-build-passes"]:::guardrail
    task_13_wire_run_end_ingest_gr_1["02-run-end-ingest-wired"]:::guardrail
  end
  style task_13_wire_run_end_ingest fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_14_record_telemetry_surfaces_in_ssot["14-record-telemetry-surfaces-in-ssot"]
    task_14_record_telemetry_surfaces_in_ssot_gr_0["01-surfaces-recorded"]:::guardrail
  end
  style task_14_record_telemetry_surfaces_in_ssot fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_corpus_store
  task_01_author_tests_corpus_store --> task_02_implement_corpus_store
  task_02_implement_corpus_store --> task_03_author_tests_failure_classifier
  task_03_author_tests_failure_classifier --> task_04_implement_failure_classifier
  task_04_implement_failure_classifier --> task_05_author_tests_journal_etl
  task_05_author_tests_journal_etl --> task_06_implement_journal_etl
  task_06_implement_journal_etl --> task_07_author_tests_corpus_report
  task_07_author_tests_corpus_report --> task_08_implement_corpus_report
  task_08_implement_corpus_report --> task_09_author_tests_telemetry_command
  task_09_author_tests_telemetry_command --> task_10_implement_telemetry_command
  task_10_implement_telemetry_command --> task_11_wire_telemetry_command
  task_11_wire_telemetry_command --> task_12_author_tests_run_end_ingest
  task_12_author_tests_run_end_ingest --> task_13_wire_run_end_ingest
  task_13_wire_run_end_ingest --> task_14_record_telemetry_surfaces_in_ssot
  task_14_record_telemetry_surfaces_in_ssot --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
