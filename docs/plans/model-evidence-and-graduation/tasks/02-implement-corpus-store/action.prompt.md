## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `02-implement-corpus-store`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "02-implement-corpus-store": { "someKey": "someValue" } }`.
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

Fill real logic over the stubs in `src/Guardrails.Core/Telemetry/TelemetryRow.cs` and
`src/Guardrails.Core/Telemetry/TelemetryCorpusStore.cs` so that
`tests/Guardrails.Core.Tests/Telemetry/TelemetryCorpusStoreTests.cs` — authored by the previous task —
passes. Read that test file first; it is the specification.

**Do NOT edit the authored tests.** Make them pass by fixing the implementation. If a test is genuinely
wrong or incompatible with a sane implementation, write
`{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the state-out path rather than
changing it — an out-of-scope edit to the test file fails the write-scope check and burns a retry.

What the store is, restated so the implementation does not drift from the design of record
(`docs/plans/model-evidence-and-graduation.charter.md` §9):

- **Append-only JSONL** under a corpus root the caller supplies. One JSON object per line; appending
  never rewrites an existing line.
- **`schemaVersion` on every row** — the corpus outlives any one build, so a row must say which shape
  it is.
- **Month-rotated** files, so the corpus grows by file rather than without bound.
- **Idempotent on `(runId, taskId, attempt)`** — this is what makes `telemetry ingest` safe to re-run
  over a plan whose rows are already recorded. Prefer a mechanism that survives a process restart (the
  key is derivable from the rows already on disk) over an in-memory set that forgets.
- **Opt-out** — when collection is disabled the store writes nothing at all, creating no files.
  **The mechanism is the environment variable `GUARDRAILS_TELEMETRY=off`** (any other value, or unset,
  means collection is ON — that is the recorded default). This is the single definition for the whole
  plan: the CLI verb (task 10) and run-end ingest (task 13) both honour the same variable rather than
  inventing their own switch, and they check it by calling into this store rather than re-reading the
  environment themselves. Two mechanisms for one decision is how a machine ends up opted out of one path
  and not the other, which is worse than no opt-out at all — the operator believes collection is off.
- **Purge** — removes every row under the corpus root, and is safe on an empty corpus.
- **Cost and token fields are independently nullable.** Null means "never reported"; it is NOT zero.
  A costless local provider reports volume and no money; a runner that reports no usage reports money
  and no volume. Never write `0` where the source reported nothing — the same null-versus-zero
  distinction `src/Guardrails.Core/Journal/JournalTierSpend.cs` already draws. Read its class comment
  before choosing a default; the reasoning there is the reasoning here.

Nothing in this task resolves `~/.guardrails/telemetry/` — the store takes its root as a parameter.
Resolving the default location belongs to the CLI task later in this plan.
