## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `14-author-tests-post-delivery-refresh`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "14-author-tests-post-delivery-refresh": { "someKey": "someValue" } }`.
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

Author failing tests for the refresh DECIDED in review — design 39 §1c.

**Test file:** `tests/Guardrails.Integration.Tests/WaveDelivery/PostDeliveryRefreshTests.cs`
**Test class:** `PostDeliveryRefreshTests`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**The decision and its precise trigger.** Today the plan branch is CONTINUOUS: `RunWavedAsync` drains
every wave on it and `MergePlanBranchIntoUserBranch` is one-directional, so delivery publishes but does
not synchronise. DECIDED: **refresh only when the delivery was NOT a fast-forward.** A fast-forward
RESULT is itself proof the user's branch did not move, so the refresh is provably a no-op in the quiet
case and can be skipped with no extra probe.

**Why it matters, so the tests assert the right thing.** Once the user's branch advances
independently, delivery 2 becomes a merge commit on their branch only, delivery 3 can no longer
fast-forward, and each later delivery merges a plan branch one more wave out of date — with AI-merge
withheld by SSOT §5.3, a conflict HALTS the run.

**Pin these behaviours to these EXACT method names:**

- `AFastForwardDelivery_DoesNotRefresh` — the quiet case stays cheap; assert NO extra merge commit.
- `ANonFastForwardDelivery_RefreshesThePlanBranch`
- `AfterARefresh_TheNextWaveBuildsOnTheUsersNewCommits`
- `TheRefreshIsRecordedAsProvenance` — the refresh admits content NO TASK AUTHORED into the tree the
  next wave's exit gate runs over. Without a record, a broken teammate commit fails that gate and
  blames a wave that did nothing wrong.

The tests MUST COMPILE and FAIL. Do NOT implement the refresh.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside them. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

