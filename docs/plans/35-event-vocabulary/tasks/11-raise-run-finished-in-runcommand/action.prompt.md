## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `11-raise-run-finished-in-runcommand`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "11-raise-run-finished-in-runcommand": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "11-raise-run-finished-in-runcommand": { "someKey": "someValue" },
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

Raise `RunFinished` from the composition root, on every path a run can leave by.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/Commands/RunCommand.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT edit the tests authored upstream.** They are the specification. If one is genuinely wrong,
write `{"needsHuman": "<why>"}` to the state-out path and stop rather than changing it - an
out-of-scope edit to a test file fails the task immediately and consumes a retry.

### The existing `finally` is NOT wide enough - this is the whole point

The `finally` that calls `TrySettleFinalSitesAfterFault` sits in a try that opens **below** the two
`ExecuteAsync` call sites, with no `catch` between. An unhandled throw out of the Scheduler - the
largest fault surface in the process - unwinds straight past it. Reusing it would ship a
`run-finished` that fires on every path except the one an unattended supervisor most needs.

**Find the current structure by reading the file** - locate the `if (live)` branch and the two
`ExecuteAsync` call sites, and grep for `TrySettleFinalSitesAfterFault`. Do not rely on line numbers
from this prompt or any other document; earlier tasks in this plan have already edited this file.

### The change

Hoist the chain variable and open a **new outer bracket** around both the DAG and the terminal phase:

```csharp
OnTheFlyDiagramObserver? diagramObserver = null;   // was assigned in both branches
int? resolvedExitCode = null;
string? faultKind = null;
try
{
    if (live) { ... } else { ... }        // both branches call ExecuteAsync
    bool finalSitesSettled = false;       // the EXISTING block, UNCHANGED, nested inside
    try { ... } finally { ... }
}
catch (Exception ex) { faultKind = ex.GetType().Name; throw; }   // TYPE only; BARE rethrow
finally { diagramObserver?.RunFinished(resolvedExitCode, faultKind); }
```

Four things are load-bearing:

- **`resolvedExitCode` is set immediately above each `return` in the inner block.** Never read it from
  `Finish`'s return value: the terminal-gate-failure branch overrides it, so a row built from `Finish`
  would report a verdict the run did not reach.
- **`ex.GetType().Name` - the TYPE, never `ex.Message`.** The message is the one value that can carry
  an absolute path, a token, or a fragment of source, and these rows are destined for an
  operator-supplied webhook URL.
- **Bare `throw;`, not `throw ex;` and not swallowing.** The exception must propagate unchanged; this
  `catch` exists only to record the type name.
- **`diagramObserver?.`** - a throw before the chain is built must raise nothing, correctly.

**Leave the `finalSitesSettled` block exactly as it is, nested inside.** Widening *it* would newly fire
`TrySettleFinalSitesAfterFault` on a mid-DAG fault - arguably an improvement, definitely a separate
change, and not this one.

### Done when

All seven `RunFinishedExitPathTests` tests pass and nothing else in either suite regresses.
