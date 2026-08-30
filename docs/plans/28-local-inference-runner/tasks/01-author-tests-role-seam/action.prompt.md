## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-tests-role-seam": { "someKey": "someValue" } }`.
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
One artifact: the test file. Task `00-land-the-required-role-seam` has already added the
`PromptRole` enum, made `PromptInvocation.Role` `required`, and set `Role = PromptRole.Action` at
every construction site in `src` and `tests`. **You do not touch any of that.** The solution compiles
when you start; your job is to write the tests that pin what each site's role SHOULD be.

**Write `tests/Guardrails.Core.Tests/Prompts/PromptRoleSeamTests.cs`**, class name
**`PromptRoleSeamTests`** (pinned - the guardrail filters on it).

Write **one `[Fact]` per site**, each pinned to the method name below, asserting that site passes its
correct role. Drive each producer and capture the `PromptInvocation` it builds - a fake `IPromptRunner`
that records its argument is the honest way here; do NOT assert by reading the same field the producer
reads, and do NOT test the enum in isolation.

| Site | Correct role (task 02 makes these pass) |
|---|---|
| `ActionRunner.cs` | `Action` |
| `WaveBreakdownInvoker.cs` | `Action` |
| `AiMergeResolver.cs` | `Action` |
| `GuardrailRunner.cs` | `Guardrail` |
| `Overwatch.cs` | `Advisory` |
| `NeedsHumanTriage.cs` | `Advisory` |
| `CriticalityJudge.cs` | `Advisory` - target-typed `=> new()`, which a grep for `new PromptInvocation` does NOT find |

Pin these exact method names - the red census binds to them:

- `ActionRunner_PassesActionRole`
- `WaveBreakdownInvoker_PassesActionRole`
- `AiMergeResolver_PassesActionRole`
- `GuardrailRunner_PassesGuardrailRole`
- `Overwatch_PassesAdvisoryRole`
- `NeedsHumanTriage_PassesAdvisoryRole`
- `CriticalityJudge_PassesAdvisoryRole`

### The red bar, and why three passing is the proof

Task 00 set every site to `Action`, deliberately including the four where that is WRONG. So against the
tree you start from:

- the three `Action` sites (`ActionRunner`, `WaveBreakdownInvoker`, `AiMergeResolver`) **PASS**, and
- the four others (`GuardrailRunner`, `Overwatch`, `NeedsHumanTriage`, `CriticalityJudge`) **FAIL**.

That 3-pass/4-fail split is the discriminator: all-seven-failing would suggest your tests are broken
rather than the code, and all-seven-passing would mean they are not bound to the real code path at all.
Do not "fix" the four failures - they are the deliverable. Task `02-assign-roles-at-seven-sites` turns
them green.

**Do NOT edit any production file.** If a test cannot be written without a `src` change, that is a real
finding: write `needsHuman` describing what is missing rather than reaching outside your scope.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Prompts/PromptRoleSeamTests.cs`. The harness runs a `git diff` check after
this task and rejects any edit outside that path - including production files, neighbouring test files,
and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a
compile error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
