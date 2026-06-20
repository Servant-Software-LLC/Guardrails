## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Update the **guardrails-domain-knowledge** skill (`.claude/skills/guardrails-domain-knowledge/**`) to
reflect plan 08's execution semantics (per the skill's SELF-UPDATING clause):
- Worktree isolation + the reuse/chaining topology (plan branch, segment-worktree reuse along linear
  chains, fan-out inherit-one + fork-the-rest, fan-in fork).
- The write-scope CHECK (deterministic, read-only; the triad replacement; scoped revert on failure).
- Integration: FF for linear chains (free), re-verified union for fan-in / non-FF; the B1 atomic settle;
  resume-by-trailer with the FF wrinkle.
- AI-merge as a v1 BYTE PRODUCER behind two deterministic checks, with the deterministic re-verify as
  the verdict; the integration-guardrail set (`scope: "integration"`) + the terminal integrationGate.
- The triad (`captureHashes`/`restoreOnRetry`/`exclusive`, `WorkspaceLock`) is REMOVED.

Update only the affected sections; keep the skill consistent with the now-updated SSOT
(`docs/plans/02-schemas-and-contracts.md`). Publish nothing to state.
