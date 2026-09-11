## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `09-author-tests-wave-delivered-journal`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-author-tests-wave-delivered-journal": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author failing tests AND the stub for the journal record — design 39 §4/§5.

**Test file:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveredJournalTests.cs`
**Test class:** `WaveDeliveredJournalTests`
**Stub:** `src/Guardrails.Core/Journal/WaveDeliveredRecord.cs`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Read this before writing the round-trip test.** A journal converter added on the WRITE side only is
not a half-working feature, it is a run-killer: every plan-phase journal write is a read-modify-write,
so the very next one throws and the run dies after paying for the whole DAG. That shipped here once
already (#625) and was caught late.

**`covers` is not decoration.** §5: it names the non-delivering waves that rode along, so the report
can say what a merge actually carried. Without it a reader cannot tell which work a given commit
shipped.

**Pin these behaviours to these EXACT method names:**

- `Delivered_RoundTripsThroughTheJournalJson` — BOTH converter directions.
- `Delivered_CarriesAtCommitAndCovers`
- `Covers_NamesTheNonDeliveringWavesThatRodeAlong`
- `ANonDeliveredWave_RecordsDeliveredNull` — null, not absent: the report distinguishes "held" from
  "not reached".
- `TheDeliveryIsJournaledRunning_BeforeTheMerge` — the #625 rule: the state is written before the
  action, so a crash mid-merge is recoverable and post-mortem tooling can see it happened.

The tests MUST COMPILE and FAIL. Do NOT implement the record.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside them. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

