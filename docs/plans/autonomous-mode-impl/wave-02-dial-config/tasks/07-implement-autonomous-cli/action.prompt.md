## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-02-dial-config/07-implement-autonomous-cli` — NOT the stableId. (This task publishes nothing to
  state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the `--autonomous` / `--dial` CLI resolution on the `run` command in
`src/Guardrails.Cli/Commands/RunCommand.cs`, filling real logic over the option stubs the previous task
authored. The design of record is `docs/plans/12-autonomous-mode.md` §3.4 and decided values §10 I/N
(read first). Make the authored `AutonomousModeCliTests` pass WITHOUT editing them.

Implement:
- **`--autonomous`**: sets `autonomyPolicy: auto` and, if the config omits an `autonomy` block, applies
  one with `escalationThreshold: high` (best-guess only low/moderate — the conservative default, §10 N).
- **`--dial <level>`**: overrides the run-wide `escalationThreshold` (`low`/`moderate`/`high`/`critical`);
  an unrecognized value exits non-zero with a clear message naming the invalid value.
- **`--autonomous` REQUIRES an effective `maxCostUsd`**: if neither the config nor a cost flag
  (`--max-cost-usd`, or the repo's existing cost option) sets one, apply the built-in **`$20`** default
  AND print a LOUD warning that it is doing so (an unattended run must never run uncapped). When a cost
  cap IS set, do not print the default warning.
- Make the resolution + warning surface under **`--dry-run`** (so it is observable without executing the
  DAG): print a concise resolved-autonomy summary line the tests assert on (e.g.
  `escalationThreshold=high`, the effective `maxCostUsd`). Do NOT change the existing `--autonomy`
  option, the existing `run` execution, or any other command.

Note (do NOT implement here): the GR2040 compound-config error (`proceed-unreviewed` + a reachable
`critical`) is a LOAD-TIME validation (a separate task owns it); `--dial critical` merely SETS the dial —
the validator raises GR2040 when the effective config reaches the forbidden end-state.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Commands/RunCommand.cs`. Do NOT edit the authored tests — if a test is genuinely
wrong, emit `{"needsHuman": "<why>"}` rather than changing it (an out-of-scope edit fails the write-scope
check and burns a retry).

Completion criteria (your guardrail checks these): `AutonomousModeCliTests` and the existing
`DryRunCliTests` / `ReviewMarkerCliTests` in `tests/Guardrails.Integration.Tests` all pass.
