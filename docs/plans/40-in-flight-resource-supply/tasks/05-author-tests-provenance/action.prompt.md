## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `05-author-tests-provenance`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "05-author-tests-provenance": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author failing tests AND minimal stubs for the `supplied[]` provenance record — design 40 §4.

**Test file:** `tests/Guardrails.Core.Tests/Supply/SuppliedProvenanceTests.cs`
**Test class:** `SuppliedProvenanceTests`
**Stub file:** `src/Guardrails.Core/Journal/SuppliedRecord.cs`

Every test carries `[Trait("Category", "Supply")]`.

**Read this before writing the round-trip test.** A journal converter added on the WRITE side
only is not a half-working feature, it is a run-killer: every plan-phase journal write is a
read-modify-write, so the very next one throws and the run dies at the terminal gate after
paying for the whole DAG. That defect shipped and was caught late once already in this repo.
So the round-trip test is not optional and it must cover BOTH directions.

**Pin these behaviours to these EXACT method names:**

- `SuppliedRecord_RoundTripsThroughTheJournalJson` — serialize then deserialize; every field
  survives. BOTH converter directions.
- `SuppliedRecord_CarriesAtCommitPathsAndBytes` — the four fields §4 names.
- `Journal_WithNoSuppliedSection_RoundTripsUnchanged` — a run that never supplied anything is
  byte-identical to today. The never-weaker requirement, asserted rather than assumed.
- `Journal_AppendsASecondSupplyWithoutLosingTheFirst` — `supplied[]` is a list; two supplies in
  one run both survive.

The tests MUST COMPILE and FAIL against the stubs. Do NOT implement the behaviour.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them — including other production files, neighbouring tests, or any `.csproj`. An out-of-scope
edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is
missing>"}` to the state-out path and stop.

