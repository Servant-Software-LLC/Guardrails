## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `19-author-tests-overwatcher-autoresolve`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "19-author-tests-overwatcher-autoresolve": { "someKey": "someValue" } }`.
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

Author failing tests for the overwatcher behaviour the review DECIDED (design 40 §3).

**Test file:** `tests/Guardrails.Core.Tests/Supply/OverwatchSupplyAutoResolveTests.cs`
**Test class:** `OverwatchSupplyAutoResolveTests`

Every test carries `[Trait("Category", "Supply")]`.

**The decision, and the caution that survives it.** The design's first draft declined the
auto-resolve outright; the reviewer's call is that at `dial:critical` the operator has already
accepted machine judgement. The caution is NOT withdrawn and these tests encode it: applying the
fix is mechanical, but *deciding that the file in the operator's checkout is the file the task
should have* is a judgement, and getting it wrong commits an arbitrary file onto the run's base.

**Pin these behaviours to these EXACT method names:**

- `BelowCritical_ProposesTheCommandSequence_AndDoesNotRunIt` — every dial below critical.
- `AtCritical_AutoResolves` — the DECIDED behaviour.
- `AtCritical_WritesTheProvenanceRecordNamingTheOverwatcherAsSupplier` — **the condition on the
  decision, not a nicety.** An auto-resolve that leaves no provenance is indistinguishable from
  a task's own work, which is precisely what §4 exists to prevent and what the #453 triage would
  need.
- `AutoResolve_DoesNotCertifyAnythingUnverified` — the task's gates still run in full afterwards.
  This is what keeps the behaviour on the right side of the standing ruling that forbids
  `dial:critical` with `proceed-unreviewed`: nothing is certified that was not verified.

The tests MUST COMPILE and FAIL. Do NOT implement the behaviour.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

