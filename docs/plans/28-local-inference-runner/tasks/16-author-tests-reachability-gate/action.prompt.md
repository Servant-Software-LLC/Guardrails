## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "16-author-tests-reachability-gate": { "someKey": "someValue" } }`.
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

`tests/Guardrails.Core.Tests/Loading/ActionReachabilityGateTests.cs`, class
**`ActionReachabilityGateTests`** (pinned). Tests only.

**GR2066 (error)** fires when an `openai-compat` block is reachable for an **Action**. There are five
routes and **each needs its own test** - the plan is explicit that a single combined test would let a
route regress unnoticed:

1. the block declares `routing` - it would become a tier candidate for actors
2. it is the **effective default** - the `default` pointer **OR the sole declared runner**, because
   `PromptRunnerRegistry.ResolveDefault` treats those identically. **Write both halves.** The
   sole-declared-runner half is the one no `default` pointer names, and route 2 fires on the most
   natural misconfiguration there is: a plan with a single local runner.
3. a task's `action.runner` names it
4. an **action prompt's YAML frontmatter `runner:`** names it - this is the route the validator
   cannot see today and requires task 17's loader fold
5. it is declared under a reserved Action-role profile name (`ai-merge` or `breakdown`)

And the **negatives**, which are what keep the gate from being a blunt ban: an `openai-compat` block
pinned by a **judge guardrail's** frontmatter `runner:` is LEGAL and must NOT fire, and so is one
named `overwatch` or `ai-triage` (the Advisory reserved profiles). Those two are the entire point of
v1 - if the gate fires on them, the flagship deliverable is unreachable.

All must FAIL today. **Do NOT implement it.**

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Loading/ActionReachabilityGateTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
