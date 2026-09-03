## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `14-document-the-streams-in-ssot`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "14-document-the-streams-in-ssot": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "14-document-the-streams-in-ssot": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

Document the two run streams in `docs/plans/02-schemas-and-contracts.md`.

**`events.jsonl` and `observer.jsonl` appear NOWHERE in that document today** - measured, zero
occurrences of either. Plan 34 shipped a public wire format, the most contract-shaped artifact in this
repo and the one an external consumer parses, with no SSOT entry at all. This task repays that.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

The rationale, and the exact wording to base these edits on, is in
`docs/plans/595-event-vocabulary-contract.md` under "Schema changes" - read it first and follow it.

### The three edits

**1.** Retitle section 8 to `## 8. Per-attempt log layout, and the run's own streams`.

**2.** Insert **`### 8.1 The run event stream (logs/<runId>/events.jsonl)`** and
**`### 8.2 The observer projection (logs/<runId>/observer.jsonl)`** at the end of section 8, before
`## 9. Prompt runners`.

Section 8.1 must cover: every emitted `kind`, including `run-finished`; the envelope fields; that
**`seq`, not `at`, is the ordering key** and why (`at` is neither unique nor monotonic under parallel
workers); that each `attempt-finished` field names its `TelemetryRow` twin; that a field the journal
does not hold is **omitted, not null**; that `run-finished` is the only run-scoped kind and carries no
`taskId`; that `faultKind` is a type name and **never a message**; and the rule that **absence of rows
means the DAG was not reached**, since the stream begins with the DAG rather than with the process.

State two limits honestly rather than implying guarantees the writer does not hold: delivery to a live
subscriber is **best-effort** and a consumer whose connection closes re-reads the file; and
single-writer is **per process** - nothing locks a plan folder, so two concurrent runs resolve the same
run id and both append.

Section 8.2 must say `observer.jsonl` mirrors observer CALLS for a renderer (it drives
`guardrails attach`), which is why its shape differs from 8.1's semantic stream, and that a consumer
skips members it does not recognise.

**3.** Add two sentences to section 12.2 where `GET /events` is described: the stream performs one
final read on shutdown so the terminal row is delivered rather than lost to the poll interval, and
delivery remains best-effort.

### Write it in the document's own voice

Match the surrounding sections' conventions - heading depth, table style, how other wire formats are
introduced. Do not restate the design document; state the contract.

### Done when

Both new subsections exist, section 12.2 covers the shutdown read, and the document still reads as one
piece rather than as an appended patch.
