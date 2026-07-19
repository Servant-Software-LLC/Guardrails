## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-02-dial-config/06-author-tests-autonomous-cli` — NOT the stableId. (This task publishes nothing
  to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING integration tests (TDD red) for the autonomous-mode CLI flags `--autonomous` and
`--dial` on the `run` command (issue #361, doc 12 §3.4, decided §10 I/N), plus the MINIMAL option stubs
to compile. The repo uses **xUnit (xunit.v3)** and `System.CommandLine`; invoke the `run` command
in-process like `tests/Guardrails.Integration.Tests/ReviewMarkerCliTests.cs` does (build a root, add
`RunCommand.Create(io)`, `Parse(args).InvokeAsync()`, capture a `StringConsoleIo`). Use **`--dry-run`**
so the plan validates + the options resolve but the DAG does NOT execute — the flags' effect is
observable from the resolved-autonomy summary line and warnings, with no heavy run.

Write two artifacts (both in scope):

1. **The test file** `tests/Guardrails.Integration.Tests/AutonomousModeCliTests.cs` — tests that must
   FAIL against the stubs:
   - **`--autonomous` defaults + required cost cap**: `run <plan> --autonomous --dry-run` (with NO cost
     cap in config or flags) resolves `escalationThreshold: high` AND prints a LOUD warning that it is
     applying the built-in `$20` `maxCostUsd` default (assert the summary line shows
     `escalationThreshold=high` and the warning mentions the `$20` default / `maxCostUsd`).
   - **Effective cap set ⇒ no default warning**: `run <plan> --autonomous --max-cost-usd 50 --dry-run`
     (or the repo's cost flag) resolves without the built-in-default warning.
   - **`--dial <level>` overrides**: `run <plan> --autonomous --dial critical --dry-run` resolves
     `escalationThreshold: critical`.
   - **Invalid dial**: `run <plan> --dial bogus` exits non-zero with a message naming the invalid value.
   - **GR2040 on the EFFECTIVE post-flag config (B1)**: a fixture plan whose `guardrails.json` sets
     `autonomy.gateThresholds.review-gate: "proceed-unreviewed"` + `autonomy.escalationThreshold: "high"`
     (which is VALID at load — `high` is not `critical`), invoked as `run <fixture> --dial critical
     --dry-run`, exits **NON-ZERO AND** stdout contains **`GR2040`** — the flag pushes the effective
     end-state to the forbidden `critical` + `proceed-unreviewed` compound, which nothing catches at load.
     This is a BLACK-BOX CLI assertion (it drives the command and greps stdout for `GR2040`; it does NOT
     reference the validator predicate type), so this task's `dependsOn: 03` is unchanged.
   Use a small committed fixture plan (build one as existing integration tests do).

2. **The minimal stubs** in `src/Guardrails.Cli/Commands/RunCommand.cs`: add the `--autonomous`
   (boolean) and `--dial <level>` (string) options so the args parse, and — if the repo has no cost flag
   — a `--max-cost-usd` option. Leave the RESOLUTION unimplemented (the options are accepted but the
   resolved-autonomy summary line / loud warning / dial-validation are NOT produced), so the tests FAIL.
   Do NOT break the existing `run` behaviour or the existing `--autonomy` option. The tests must COMPILE
   and FAIL (not compiling is a mistake). Do NOT implement the resolution.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/AutonomousModeCliTests.cs` and
`src/Guardrails.Cli/Commands/RunCommand.cs`. After this task the harness runs a `git diff` check and
rejects any edit outside these paths. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error from a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` and stop.
