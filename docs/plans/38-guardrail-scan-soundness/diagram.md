<!-- guardrails:graph v1 source-sha256=0e8a3f64ddf05214aa2124e6a0b19caf43344735477a37514783bc00823164aa -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-core-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_guardrail_abort["01-author-tests-guardrail-abort"]
    task_01_author_tests_guardrail_abort_gr_0["01-build-passes"]:::guardrail
    task_01_author_tests_guardrail_abort_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_01_author_tests_guardrail_abort fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_guardrail_abort["02-implement-guardrail-abort"]
    task_02_implement_guardrail_abort_gr_0["01-build-passes"]:::guardrail
    task_02_implement_guardrail_abort_gr_1["02-abort-tests-pass"]:::guardrail
    task_02_implement_guardrail_abort_gr_2["03-both-pwsh-templates-route-through-the-shim"]:::guardrail
    task_02_implement_guardrail_abort_gr_3["04-shim-preserves-real-exit-codes"]:::guardrail
  end
  style task_02_implement_guardrail_abort fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_fix_plan35_census_list_ordering["03-fix-plan35-census-list-ordering"]
    task_03_fix_plan35_census_list_ordering_gr_0["01-accumulator-created-before-use"]:::guardrail
  end
  style task_03_fix_plan35_census_list_ordering fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_author_tests_registry_entries["04-author-tests-registry-entries"]
    task_04_author_tests_registry_entries_gr_0["01-build-passes"]:::guardrail
    task_04_author_tests_registry_entries_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_04_author_tests_registry_entries fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_add_banned_pattern_entries["05-add-banned-pattern-entries"]
    task_05_add_banned_pattern_entries_gr_0["01-registry-tests-pass"]:::guardrail
  end
  style task_05_add_banned_pattern_entries fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_correct_scan_order_doctrine["06-correct-scan-order-doctrine"]
    task_06_correct_scan_order_doctrine_gr_0["01-scan-order-doctrine-corrected"]:::guardrail
  end
  style task_06_correct_scan_order_doctrine fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_add_rendered_vs_stored_probe["07-add-rendered-vs-stored-probe"]
    task_07_add_rendered_vs_stored_probe_gr_0["01-review-probes-present"]:::guardrail
  end
  style task_07_add_rendered_vs_stored_probe fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_record_shim_contract["08-record-shim-contract"]
    task_08_record_shim_contract_gr_0["01-ssot-carries-the-contract"]:::guardrail
  end
  style task_08_record_shim_contract fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_immunize_existing_guardrail_fixtures["09-immunize-existing-guardrail-fixtures"]
    task_09_immunize_existing_guardrail_fixtures_gr_0["01-fixture-exposure-removed"]:::guardrail
    task_09_immunize_existing_guardrail_fixtures_gr_1["02-build-passes"]:::guardrail
    task_09_immunize_existing_guardrail_fixtures_gr_2["03-touched-tests-still-pass"]:::guardrail
  end
  style task_09_immunize_existing_guardrail_fixtures fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-scan-soundness-union-verified"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_guardrail_abort
  plan_preflights --> task_03_fix_plan35_census_list_ordering
  plan_preflights --> task_04_author_tests_registry_entries
  plan_preflights --> task_06_correct_scan_order_doctrine
  plan_preflights --> task_07_add_rendered_vs_stored_probe
  plan_preflights --> task_09_immunize_existing_guardrail_fixtures
  task_01_author_tests_guardrail_abort --> task_02_implement_guardrail_abort
  task_02_implement_guardrail_abort --> task_08_record_shim_contract
  task_04_author_tests_registry_entries --> task_05_add_banned_pattern_entries
  task_09_immunize_existing_guardrail_fixtures --> task_05_add_banned_pattern_entries
  task_03_fix_plan35_census_list_ordering --> plan_guardrails
  task_05_add_banned_pattern_entries --> plan_guardrails
  task_06_correct_scan_order_doctrine --> plan_guardrails
  task_07_add_rendered_vs_stored_probe --> plan_guardrails
  task_08_record_shim_contract --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
