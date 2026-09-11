## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `03-author-tests-drain`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "03-author-tests-drain": { "someKey": "someValue" } }`.
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

Author failing tests AND minimal stubs for the drain step — design 40 §2.

**Test file:** `tests/Guardrails.Core.Tests/Supply/SuppliedDrainTests.cs`
**Test class:** `SuppliedDrainTests`
**Stub file:** `src/Guardrails.Core/Execution/SuppliedDrain.cs`

Every test carries `[Trait("Category", "Supply")]`.

**Drive a REAL git repository**, not a fake of one. This class's whole job is to commit onto a
real base, so a faked git seam would prove nothing about the thing it exists to do. Build the
repo in a temp directory and tear it down. Git-for-Windows requires care that has bitten this
repo before: strip read-only attributes before `Directory.Delete` (git marks loose objects
read-only), and normalize `core.autocrlf=false` so content hashes are deterministic.

**Pin these behaviours to these EXACT method names:**

- `Drain_OnAnEmptyStagingTree_DoesNothingAndMakesNoCommit` — §2 step 1, the never-weaker
  requirement: the feature is completely inert for a run that does not use it.
- `Drain_CopiesEveryStagedFileToItsWorkspacePath` — the staged tree lands at the paths §1 says.
- `Drain_CommitsWithTheSuppliedByOperatorTrailer` — the commit message carries
  `Supplied-By-Operator: guardrails supply` and `Guardrails-Run: <runId>` (§4).
- `Drain_DeletesTheStagingTreeAfterCommitting` — the tree is harness-owned and drained, so a
  second drain must not re-commit the same files.
- `Drain_ReturnsTheCommittedPathsAndByteCount` — the caller needs these for the §4 provenance
  record and the §2 announcement.

The tests MUST COMPILE and FAIL against the stubs. Do NOT implement the behaviour.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them — including other production files, neighbouring tests, or any `.csproj`. An out-of-scope
edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is
missing>"}` to the state-out path and stop.

