## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `13-wire-run-end-ingest`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "13-wire-run-end-ingest": { "someKey": "someValue" } }`.
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

Wire run-end telemetry ingest into the CLI run path so that
`tests/Guardrails.Integration.Tests/RunEndTelemetryIngestTests.cs` passes. Read that test file first;
it is the specification.

**Write only to `src/Guardrails.Cli/Commands/RunCommand.cs`.** After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path. **Do NOT edit the authored test.** If it
is genuinely wrong, write `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` rather than
changing it.

**Where it goes.** `RunCommand.Finish` is the run-completion seam — the method that writes the durable
final log site and prints the summary. Find it by name (`private static int Finish(`); do not rely on a
line number, which will have moved. Ingest belongs there, after the journal is final.

**Follow the sibling that already makes this exact trip.** `WriteDurableFinalSite(logsRoot, plan,
planDirectory)` is called from that same method under the comment *"Best-effort: a render hiccup must
never change the run's exit code."* That is not a coincidence you can improve on — it is the pattern:

- **Wrap the ingest so no failure escapes.** A full disk, a locked file, a corpus root that does not
  exist: none of them may change the exit code, throw out of `Finish`, or suppress the summary. A
  telemetry feature that can fail a delivered run is worse than no telemetry feature.
- **Report the failure without escalating it.** Say one line on the console that ingest did not happen
  and why. Silence would be the defect this whole plan is about — a mechanism failing in the direction
  that looks fine.

**Every outcome ingests.** Place the call so it runs for a green run, a `needs-human` run and a halted
one alike. The failure attempts are the ones the corpus most needs; ingesting only successes would
build a corpus that flatters every model in it. The one branch that legitimately skips is the
definition-drift early return above `WriteDurableFinalSite` — nothing ran and no logs were written, so
there is nothing to ingest.

**Honour the opt-out here too — by CALLING, never by re-deriving.** Task 10 exposes the corpus-root
resolution as an `internal static` member of `TelemetryCommand`; call it. The opt-out is the environment
variable `GUARDRAILS_TELEMETRY=off`, checked inside the store — so you get it for free by going through
the same path, and you must NOT read the variable yourself here. Your `writeScope` is `RunCommand.cs`
alone, which is deliberate: a second copy of either rule is the only thing that could make the verb and
the run disagree about where the corpus is or whether collection is on, and it would do so silently.

Construct the real `TelemetryIngest` over the resolved root. Do not re-implement any part of the ETL
here — if something you need is not reachable from `RunCommand.cs`, that is a finding to report with
`{"needsHuman": …}`, not a reason to inline a second implementation.
