## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `09-author-tests-telemetry-command`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "09-author-tests-telemetry-command": { "someKey": "someValue" } }`.
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

Author the FAILING tests, plus the minimal stub they compile against, for the **`guardrails telemetry`
CLI verb** — and, in a SEPARATE test class, the composition-root wiring proof.

**Write only to these three files:**
- `tests/Guardrails.Integration.Tests/Commands/TelemetryCommandTests.cs` — the verb's behaviour
- `tests/Guardrails.Integration.Tests/Commands/TelemetryCommandWiringTests.cs` — the wiring proof
- `src/Guardrails.Cli/Commands/TelemetryCommand.cs` (stub)

**Scope boundary (harness-enforced):** Write only to those three paths. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — including `CommandFactory.cs`,
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. **In particular: do NOT register the command in
`src/Guardrails.Cli/CommandFactory.cs`.** That registration is task 11's entire deliverable, and doing
it here would make the wiring test green before the task that exists to earn it. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Two classes, and the split is load-bearing.** The behaviour tests and the wiring test are made green
by two different tasks, so they must be separately selectable:

- **`TelemetryCommandTests`** — drives the command directly (build the `Command` object and invoke it),
  so it goes green in task 10, before registration exists.
- **`TelemetryCommandWiringTests`** — drives `CommandFactory.BuildRootCommand(io)` and invokes
  `telemetry ...` through the REAL root, so it can only go green in task 11.

Both classes live in namespace `Guardrails.Integration.Tests.Commands`, and every class and method
carries `[Trait("Category", "ModelEvidence")]`. Model both on the existing
`tests/Guardrails.Integration.Tests/Commands/SamplesCommandTests.cs` — same `StringConsoleIo` capture,
same real-root idiom.

**Pin these behaviours to these exact test method names:**

| behaviour | class | test method name |
|---|---|---|
| ingest writes rows from a plan journal | `TelemetryCommandTests` | `Ingest_WritesRowsFromAPlanJournal` |
| report prints the stratified table | `TelemetryCommandTests` | `Report_PrintsTheStratifiedTable` |
| purge empties the corpus | `TelemetryCommandTests` | `Purge_EmptiesTheCorpus` |
| opt-out honoured end to end | `TelemetryCommandTests` | `Ingest_WhenOptedOut_WritesNothing` |
| the verb is reachable from the REAL root | `TelemetryCommandWiringTests` | `Telemetry_IsReachableFrom_CommandFactoryBuildRootCommand` |

**Design constraints:**
- Every test points the corpus at a **temp directory** it deletes afterwards. No test may touch the real
  `~/.guardrails/telemetry/` — a test that writes to the user's actual corpus poisons the very data this
  plan exists to collect.
- The command constructs the **real** `TelemetryIngest`, `TelemetryCorpusStore`, `TelemetryReport` and
  `TelemetryFailureClassifier` over that temp root. Do NOT introduce an interface so the tests can
  inject fakes of them: they are in-repo collaborators that already work, and faking them here would
  make the verb pass over a broken composition (charter §5's "the gate is not the whole truth", and the
  reason this plan's seam ledger is empty).
- The wiring test asserts an **observable only the registered verb produces** — that invoking
  `telemetry ...` through `BuildRootCommand` does the work — not merely that the parse did not error.
  An unregistered verb must make it fail.

**The tests MUST COMPILE and FAIL** against a `NotImplementedException` stub. Do NOT implement the verb.
