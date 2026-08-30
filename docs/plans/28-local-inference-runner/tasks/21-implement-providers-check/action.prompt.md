## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "21-implement-providers-check": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements part of `docs/plans/28-local-inference-runner.md`. READ THE SECTION(S) NAMED BELOW before you start -
the plan carries the reasoning, the rejected alternatives, and the exact file:line evidence.
Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

Read: **plan section 8**.

## Task

### What to build

Make `ProvidersCheckTests` pass. Add `check` to `src/Guardrails.Cli/Commands/ProvidersCommand.cs`:
`guardrails providers check <block-name>` runs one probe per dialect assumption against the
operator's REAL endpoint and reports each **met / unmet / unknown**.

Reuse task 19's probe logic rather than writing a second copy - a second implementation of the same
probe is how the two drift, and the plan is explicit about extract-never-copy for exactly this reason.

**Not in CI, not in `run`, not in `validate`.** It is the opt-in verb whose whole job is to be run by
a human against real hardware before trusting a config - the first contact with the Mac Studio should
be this verb, not a live `guardrails run`.

Exit non-zero only when the endpoint cannot be reached at all. An `unmet` or `unknown` assumption is
information, not a failure.

**Do NOT edit the test file.**

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/Commands/ProvidersCommand.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
