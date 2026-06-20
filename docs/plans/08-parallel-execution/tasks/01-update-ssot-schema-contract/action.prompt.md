## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Apply plan 08's `## Schema changes` block to the SSOT, `docs/plans/02-schemas-and-contracts.md`
(the single source of truth). The plan document `docs/plans/08-parallel-execution.md` §"Schema
changes (exact `02-schemas-and-contracts.md` edits)" is the verbatim spec — apply it exactly, in
the same character this plan implements (contract-first). Specifically:

1. **§1 plan-folder layout** — add the "Workspace must be a git repository top-level" block (plan
   branch `guardrails/<plan-name>` + harness-owned integration worktree + segment worktrees with
   reuse; `worktreeRoot`; non-git rejected by **GR2015**). Move the `state/logs/...` layout to a
   top-level `logs/<runId>/<task-id>/attempt-N/` sibling of `state/`.
2. **§2 `guardrails.json`** — add `worktreeRoot`, `runOnCurrentBranch`, `mergeOnSuccess`; set
   `maxParallelism` default **3** with the documented rationale.
3. **§3 `task.json`** — DELETE `exclusive`, `captureHashes`, `restoreOnRetry` and remove §3.1/§3.1.1
   in full (with the pointer note). ADD `integrationGate` and `writeScope`. Add §3.3 (terminal gate
   + GR2017/GR2018), §3.4 (write-scope check + GR2019/GR2020), and the §3.2 worktree-reuse semantics.
4. **§4.3** — add the per-guardrail `scope: "integration" | "local"` section + the integration-guardrail set.
5. **§5.3** — replace with the FF / union integration + the unified atomic settle + AI-merge merge-env-contract.
6. **§9.1** — add the AI-merge worker section.
7. **Diagnostic codes** — RETIRE the GR2013/GR2014 triad meanings (record the retirement in a code
   comment) and add the FRESH allocation **GR2015 (not-git), GR2016 (MAX_PATH), GR2017 (missing gate),
   GR2018 (empty integration set), GR2019 (scope escapes workspace), GR2020 (vacuous scope)** with the
   severities from the plan's table.

Do NOT edit any harness source, tests, or the plan-breakdown skill in this task — SSOT prose only.
First verify the live SSOT state (plan 07's edits were never applied, per the plan), then edit.
Publish nothing to state.
