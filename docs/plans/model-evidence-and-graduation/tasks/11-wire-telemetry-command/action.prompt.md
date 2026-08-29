## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `11-wire-telemetry-command`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "11-wire-telemetry-command": { "someKey": "someValue" } }`.
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

Register the telemetry verb in the CLI composition root: add `TelemetryCommand.Create(io)` to
`CommandFactory.BuildRootCommand` in `src/Guardrails.Cli/CommandFactory.cs`, beside the other
`rootCommand.Add(...)` lines.

**This is a one-line change, and it is the line the whole plan is inert without.** Every task before it
built a component that is green in isolation: the store, the ETL, the classifier, the report, and a
`TelemetryCommand` whose own tests pass. None of that gives the shipped binary a `telemetry` verb. A
component that is constructed nowhere is reachable only from the test project — the recurring
false-green this repo has hit three times in one plan (#120), which is why the registration is a task
of its own with its own proof rather than a detail folded into task 10.

**Write only to `src/Guardrails.Cli/CommandFactory.cs`.** After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path. If `TelemetryCommand.Create` does not exist or
does not compile, do NOT fix it here — that is task 10's file, outside your scope. Write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Your guardrail is `tests/Guardrails.Integration.Tests/Commands/TelemetryCommandWiringTests.cs`, which
drives `CommandFactory.BuildRootCommand` — the REAL root, not a hand-built command — and asserts the
verb actually does its work through it. It fails before this change and passes after; that difference is
the entire proof, so do not weaken the test to meet it.
