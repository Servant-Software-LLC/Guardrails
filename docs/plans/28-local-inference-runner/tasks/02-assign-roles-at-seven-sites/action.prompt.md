## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "02-assign-roles-at-seven-sites": { "someKey": "someValue" } }`.
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

Read: **plan section 3.4**.

## Task

### What to build

Task 01 set all seven `PromptInvocation` construction sites to `PromptRole.Action` as an explicit
stub and wrote `PromptRoleSeamTests` pinning the correct value at each. Five of those tests are RED.

Replace each stub with the correct role from the plan's section 3.4 table:

| Site | Role |
|---|---|
| `ActionRunner.cs` | `Action` - the task action itself |
| `WaveBreakdownInvoker.cs` | `Action` - authors a task folder |
| `AiMergeResolver.cs` | `Action` - writes `GUARDRAILS_MERGE_OUT` |
| `GuardrailRunner.cs` | `Guardrail` - the judge |
| `Overwatch.cs` | `Advisory` - advisory never gates |
| `NeedsHumanTriage.cs` | `Advisory` - advisory never gates |
| `CriticalityJudge.cs` | `Advisory` - the **target-typed `=> new()`** site |

Remove the `// STUB` comments as you go.

The classification rule, so you can check yourself rather than copying the table: *does this prompt
write anything other than its own verdict file?* Yes then `Action`. No, and its output is a
pass/fail then `Guardrail`. No, and its output is advice then `Advisory`.

**Do NOT edit `PromptRoleSeamTests.cs`.** It is outside your writeScope; an edit to it fails the
write-scope check and burns a retry. If a test is genuinely wrong, emit `needsHuman` with the
reason rather than changing it.

`ClaudePromptRunner` must continue to ignore `Role` entirely - no Claude run changes by one byte.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/ActionRunner.cs`, `src/Guardrails.Core/Execution/WaveBreakdownInvoker.cs`, `src/Guardrails.Core/Execution/AiMergeResolver.cs`, `src/Guardrails.Core/Execution/GuardrailRunner.cs`, `src/Guardrails.Core/Execution/Overwatch.cs`, `src/Guardrails.Core/Execution/NeedsHumanTriage.cs`, `src/Guardrails.Core/Execution/CriticalityJudge.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
