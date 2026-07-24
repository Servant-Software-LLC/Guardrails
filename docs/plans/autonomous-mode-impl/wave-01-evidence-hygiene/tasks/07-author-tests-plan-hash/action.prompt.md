## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-01-evidence-hygiene/07-author-tests-plan-hash` — NOT the stableId. (This task publishes nothing
  to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING integration tests (TDD red) for a new read-only CLI command
`guardrails plan-hash <folder>` (issue #366, doc 16 OD-3 / §12.2 — the affordance the
`/guardrails-review` skill needs to embed the plan hash for F2a), plus the MINIMAL stub to compile. The
command prints the plan's `PlanDefinitionHash` (`sha256:…`). The repo uses **xUnit (xunit.v3)** and
`System.CommandLine`.

Write these artifacts (all in scope):

1. **The test file** `tests/Guardrails.Integration.Tests/PlanHashCliTests.cs` — tests that must FAIL
   against the stub:
   - **Prints the hash**: invoke the command through the REAL production dispatch —
     `CommandFactory.BuildRootCommand(io)` (NOT a hand-assembled `new RootCommand()` — driving the real
     factory proves the command is WIRED into production dispatch, #120) — with `["plan-hash", planDir]`,
     assert exit 0 and that stdout contains the `sha256:…` value equal to
     `Guardrails.Core.Journal.PlanDefinitionHash.Compute(loadedPlan)` for the same plan.
   - **Determinism**: two invocations on the same unchanged plan print the SAME hash.
   Use a small committed fixture plan (build one the way existing integration tests do, e.g.
   `ScriptPlanBuilder`, or load an `examples/` plan) and a `StringConsoleIo` double.

2. **The minimal stub** `src/Guardrails.Cli/Commands/PlanHashCommand.cs` — a `Create(IConsoleIo io)`
   factory returning the `plan-hash` command with a `<folder>` argument, whose handler
   `throw new NotImplementedException();`. **Register it in
   `src/Guardrails.Cli/CommandFactory.BuildRootCommand`** (add `rootCommand.Add(PlanHashCommand.Create(io))`)
   so the real dispatch reaches it and the test above can drive it. The tests MUST COMPILE and FAIL
   (failing is intentional; not compiling is a mistake). Do NOT implement the hash printing.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/PlanHashCliTests.cs`,
`src/Guardrails.Cli/Commands/PlanHashCommand.cs`, and `src/Guardrails.Cli/CommandFactory.cs`. After this
task the harness runs a `git diff` check and rejects any edit outside these paths. An out-of-scope edit
fails the task immediately and consumes a retry. If you hit a compile error from a missing symbol in
another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` and stop.
