## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `10-author-tests-supply-command`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "10-author-tests-supply-command": { "someKey": "someValue" } }`.
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

Author failing tests AND the stub for `guardrails supply <plan> <path>...` — design 40 §6.

**Test file:** `tests/Guardrails.Integration.Tests/Supply/SupplyCommandTests.cs`
**Test class:** `SupplyCommandTests`
**Stub file:** `src/Guardrails.Cli/Commands/SupplyCommand.cs`

Every test carries `[Trait("Category", "Supply")]`.

**Drive the REAL composition root** — `CommandFactory.BuildRootCommand(io)` then
`root.Parse(args).InvokeAsync()`. A command that is written, tested in isolation and never
registered is a command the operator does not have; parsing through the real root fails if the
verb is not wired.

**Pin these behaviours to these EXACT method names:**

- `Supply_StagesTheFileUnderLogsRunIdSupplied` — the file lands where §1 says.
- `Supply_PrintsWhereItLandedAndWhichBoundaryWillPickItUp` — §6 requires BOTH. An operator who
  is not told the boundary does not know whether to wait or to resume.
- `Supply_RefusesWhenThereIsNoResumableRunAtAll` — no journal, or a journal whose every task has
  settled.
- `Supply_DoesNotRequireALiveRun` — **the load-bearing one.** The measured incident is a run that
  had already HALTED and exited; a `supply` that required a live run would refuse precisely when
  it is needed. The design's first draft got this wrong, which is why it is pinned.
- `Supply_RefusesAPathOutsideTheWorkspace` — GR2019's traversal rule at the CLI boundary.

The tests MUST COMPILE and FAIL. Do NOT implement the verb.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

