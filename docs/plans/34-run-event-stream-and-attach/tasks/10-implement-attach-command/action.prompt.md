## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `10-implement-attach-command`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "10-implement-attach-command": { "someKey": "someValue" } }`.
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

Make `AttachReplayTests` pass by implementing `guardrails attach <plan-folder>` in
`src/Guardrails.Cli/Commands/AttachCommand.cs`.

It tails `logs/<runId>/observer.jsonl` (task 08's projection) and drives a **real `LiveRunObserver`** in
the attaching terminal. Do NOT re-render the table yourself - constructing the shipped renderer from
replayed events is the entire point, and a second renderer would drift.

Requirements:

- no server, no port, no lifetime to manage - it reads a file
- multiple watchers attach concurrently with no contention, and attaching writes nothing to the run
- attaching after the run has ended replays to completion
- a missing or unreadable `observer.jsonl` produces one actionable line, not a stack trace
- the TTY requirement belongs to the ATTACHING client, never to the run

Register the verb in `src/Guardrails.Cli/CommandFactory.cs` - `BuildRootCommand` is where every other
verb is added (`rootCommand.Add(LogsCommand.Create(io));` and its siblings; grep for `rootCommand.Add`
rather than citing a line number). That file is IN your writeScope precisely because an unregistered
command is unreachable from a real `guardrails` invocation and exists only for the tests.

Do NOT edit the authored tests.
