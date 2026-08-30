## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `10-implement-telemetry-command`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "10-implement-telemetry-command": { "someKey": "someValue" } }`.
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

Implement the `guardrails telemetry` verb in `src/Guardrails.Cli/Commands/TelemetryCommand.cs` so that
`tests/Guardrails.Integration.Tests/Commands/TelemetryCommandTests.cs` passes. Read that test file
first; it is the specification.

**Do NOT edit the authored tests**, and **do NOT touch `src/Guardrails.Cli/CommandFactory.cs`** — the
registration is the next task's whole deliverable, and its wiring test
(`TelemetryCommandWiringTests`) is expected to keep FAILING until then. Your own guardrail filters on
`TelemetryCommandTests` only, so a still-red wiring test is correct at this point, not a problem to fix.

The verb has three subcommands:

- **`telemetry ingest [plan-folder]`** — reads the folder's `state/run.json` through the ETL and writes
  rows. Given a directory of plans, ingests each one that has a journal; a folder without one is a
  reported no-op, not an error. This is the backfill path, and it is the reason the corpus can be
  populated from runs already on disk today.
- **`telemetry report`** — renders the stratified table.
- **`telemetry purge`** — empties the corpus.

Follow the shape of the existing verbs (`src/Guardrails.Cli/Commands/ProvidersCommand.cs` and
`SamplesCommand.cs` are the closest models): a static `Create(IConsoleIo io)` returning the `Command`,
all output through `io`, never `Console` directly.

**Two things this task owns that nothing else does:**

1. **Resolving the default corpus root** — `~/.guardrails/telemetry/` — since every Core type takes its
   root as a parameter on purpose. Use the same `Environment.SpecialFolder.UserProfile` idiom
   `src/Guardrails.Cli/SkillsInstaller.cs` already uses. Allow an override so a test never writes to the
   real corpus. **Expose the resolution as an `internal static` member of `TelemetryCommand`, not a
   private one** — task 13 wires run-end ingest in `RunCommand.cs` and must call exactly this resolution.
   Its `writeScope` is that one file, so a private member leaves it no in-scope option but to duplicate
   the logic, and two copies of "where does the corpus live" drift silently.
2. **The opt-out**, honoured before anything is written. The mechanism is already fixed by task 02:
   the environment variable **`GUARDRAILS_TELEMETRY=off`**, checked inside the store. Do not invent a
   second switch (a flag, a config key) and do not re-read the variable here — call the store, so the
   verb and run-end ingest cannot disagree about whether collection is on. Collection is on by default
   (the charter's `collection-default` decision), so the off switch has to be real, or the default is
   not honest.

Construct the **real** `TelemetryIngest`, `TelemetryCorpusStore`, `TelemetryReport` and
`TelemetryFailureClassifier`. Do not re-implement any of their logic inline and do not introduce
interfaces for them — they are in-repo collaborators that this plan already built and tested.
