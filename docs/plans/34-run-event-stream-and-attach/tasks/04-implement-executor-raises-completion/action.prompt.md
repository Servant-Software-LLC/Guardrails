## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `04-implement-executor-raises-completion`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "04-implement-executor-raises-completion": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Make `TaskExecutorAttemptCompletionTests` pass: raise `observer.AttemptFinished(task, attempt, outcome)`
from `TaskExecutor` at every path where an attempt completes, carrying the SAME `AttemptOutcome` the
`AttemptRecord` is journaled with.

**ENUMERATE the completion paths; do not trust one grep marker.** `Outcome = AttemptOutcome` matches only
**2** sites, while `AttemptOutcome.` appears **18** times and `AttemptRecord` is constructed in **two**
files - `TaskExecutor.cs` AND `AttemptJournaler.cs`, both of which are in your writeScope for that reason.
Work out the full set yourself: every path on which an attempt REACHES A TERMINAL OUTCOME, success and
failure alike. The failure paths are the ones that matter - today provenance is carried onto the journal
record only on the SUCCESS paths, which is why the telemetry corpus reads 100% first-pass for every model,
and a fix that covers only the success paths reproduces exactly that defect one layer over.

If a completion path turns out to live in a file NOT in your writeScope, do not reach for it - write
`{"needsHuman": "<which path, which file>"}` to the state-out path and stop.

Raise the member from the ONE place the outcome is already decided; do not re-derive the outcome for the
observer call, and do not add a second enum. If a completion path exists that genuinely has no
`AttemptOutcome`, emit `{"needsHuman": "<which path, and why it has no outcome>"}` rather than inventing
one.

Do NOT edit the authored tests.
