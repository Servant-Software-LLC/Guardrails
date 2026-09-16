## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `18-implement-terminal-gate-names-supply`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "18-implement-terminal-gate-names-supply": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Make `SuppliedTerminalGateHaltTests` pass. Design 41 §6 "Later gate halts", the DECIDED answer to
`d41-terminal-gate-names-supply`: **the terminal gate halt names supplied and refreshed content.**

One file: `src/Guardrails.Cli/PlanGuardrailPhase.cs`.

### Reuse the shipped reader — do not write a second one

`Guardrails.Core.Journal.UnauthoredContentNote` is a shipped `public static class` with
`HeadlineSuffix(JournalDocument)` and `DetailLines(JournalDocument)`. It is THE ONE reader that answers
"what is in this tree that no task authored?", and it deliberately reads BOTH `supplied[]` and
`refreshed[]` — its own doc comment says a consumer that reads only one of them is the defect it exists to
prevent. `Scheduler.BuildGateHalt` already calls it for wave entry/exit gate halts; read that method and
mirror it. Do not re-implement the rendering, do not read `document.Supplied` or `document.Refreshed`
directly, and do not hand-format a sha truncation — call the reader.

### Where each half goes, and why

`RunHalt` has `Headline`, `FailedChecks` and `LogDir`, and **no `Detail` field** — unlike the `WaveHalt` the
Scheduler builds. `RunHalt.cs` is outside your writeScope, so adding one is not available to you and is not
wanted. Use the two surfaces this file already owns:

- **`HeadlineSuffix`** → append to the `RunHalt.Headline` you build on failure, AFTER everything it already
  says, exactly as `Scheduler.BuildGateHalt` appends it last.
- **`DetailLines`** → write to `heartbeatOut`, the `TextWriter` this method already takes; in production
  `RunCommand` passes the run's own console (`io.Out`). It is nullable — write nothing when it is null.
  `GuardrailHeartbeat` never owns or closes that writer (it captures a lambda and its `Dispose` only stops a
  timer), so writing to it after the checks have finished is safe. The terminal phase runs OUTSIDE the
  Spectre live region, so plain lines there cannot corrupt a live table (#145).

**Getting the document.** This phase has no `RunJournal`. `PlanPhaseJournalWriter.Update(planDirectory,
document => …)` hands you the document currently on disk inside its lambda — that is the natural place to
read it. Note that the failing path already calls `Update` once; you need the journal's `supplied[]` /
`refreshed[]` in hand at the moment you compose the halt.

### The never-weaker half is the one to get right

`FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt` asserts BYTE-EQUALITY of the halt
headline for a run that supplied and refreshed nothing — which is nearly every run. `HeadlineSuffix` returns
`null` for that case, so appending nothing is the whole of it; guard on the null and never synthesise an
"(no unauthored content)" note. The same applies to the detail lines: empty means write nothing, not a
blank line and not a header. And a PASSING gate writes no halt at all, so the disclosure must ride on the
halt rather than becoming an unconditional announcement on every green run.

### Scope notes

- `Scheduler.cs` already discloses at wave gates and needs no change. It is not in your writeScope.
- Do not touch `UnauthoredContentNote.cs`, `RunHalt.cs` or `JournalModel.cs`.

Do NOT edit the authored tests; emit `{"needsHuman": "<why>"}` if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/PlanGuardrailPhase.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
