## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "21-implement-reachability-gate": { "someKey": "someValue" } }`.
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

Read: **plan section 3.7**.

## Task

### What to build

Make `ActionReachabilityGateTests` pass. Two coupled deliverables:

**1. Make route 4 visible - EXTRACT the frontmatter reader, never copy it.** `ActionRunner` resolves
`route?.Runner?.Name ?? task.Action.Runner ?? promptFile.Frontmatter.Runner`, so an action prompt with
`runner: local-qwen` in its YAML reaches the block for an Action. But `PlanLoader.ApplyPromptFrontmatter`
folds only `scope` and `tier`, and **only onto guardrails** - a prompt ACTION's frontmatter is never
folded onto the plan definition, so `PlanValidator` has nothing to read.

Extract `PlanLoader`'s frontmatter reader into a shared helper and have the loader fold an action
prompt's `runner` onto the task definition, **purely so validation can see it**. `ActionRunner`'s
resolution chain is untouched, so precedence does not move.

**Extract, never copy.** A fourth independent frontmatter parser is how the two sites drift.

**2. Implement GR2066** over all five routes in `PlanValidator`, allocating the code in
`DiagnosticCodes.cs` (task 19 already advanced the marker; GR2066 is yours).

Do not fire on the two LEGAL reachability paths: a judge guardrail's frontmatter pin, and the
`overwatch` / `ai-triage` Advisory profile names.

**Do NOT edit the test file.**

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Loading/PlanValidator.cs`, `src/Guardrails.Core/Loading/DiagnosticCodes.cs`, `src/Guardrails.Core/Loading/PlanLoader.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
