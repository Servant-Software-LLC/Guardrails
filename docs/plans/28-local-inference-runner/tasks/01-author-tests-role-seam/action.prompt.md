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

Read: **plan sections 3.4 and 6.5**.

## Task

### What to build

Two artifacts, in one task because a `required` member is a **source break at every construction
site** - there is no intermediate state in which the solution compiles, so this cannot be split.

**1. The seam, in `src/Guardrails.Core/Prompts/PromptInvocation.cs`.** Add the `PromptRole` enum
(`Action`, `Guardrail`, `Advisory`) and a `public required PromptRole Role { get; init; }` property,
with the XML docs the plan's section 3.4 gives verbatim. `required`, never defaulted - a default
would let a new call site silently acquire the permissive value, and the compiler is the gate.

Also fold in section 6.5's **empty-path convention**: move it out of the comment it lives in today
and into the XML docs of `StreamLogPath`, `WorkingDirectory` and `PlanDirectory`, stating that an
EMPTY string is legal and means "no log / no cwd / no plan dir", not "abort".

**2. The STUB assignment at all seven sites, plus the tests that pin the real values.**

Set every one of the seven construction sites to `PromptRole.Action` - **deliberately wrong for
four of them** - so the tests you write go RED. Mark each with a `// STUB (task 02 assigns the real
role)` comment. The seven sites, from the plan's own table:

| Site | Correct role (task 02 sets this) |
|---|---|
| `ActionRunner.cs` | `Action` |
| `WaveBreakdownInvoker.cs` | `Action` |
| `AiMergeResolver.cs` | `Action` |
| `GuardrailRunner.cs` | `Guardrail` |
| `Overwatch.cs` | `Advisory` |
| `NeedsHumanTriage.cs` | `Advisory` |
| `CriticalityJudge.cs` | `Advisory` - **target-typed `=> new()`**, which a grep for
  `new PromptInvocation` does NOT find. Verified: that grep returns exactly 6 today. |

**3. The test file** `tests/Guardrails.Core.Tests/Prompts/PromptRoleSeamTests.cs`, class name
**`PromptRoleSeamTests`** (pinned - the guardrail filters on it).

Write **one `[Fact]` per site**, each pinned to the method name below, asserting that site passes
its correct role. Drive each producer and capture the `PromptInvocation` it builds - a fake
`IPromptRunner` that records its argument is the honest way here; do NOT assert by reading the same
field the producer reads, and do NOT test the enum in isolation.

Pin these exact method names - the red census binds to them:

- `ActionRunner_PassesActionRole`
- `WaveBreakdownInvoker_PassesActionRole`
- `AiMergeResolver_PassesActionRole`
- `GuardrailRunner_PassesGuardrailRole`
- `Overwatch_PassesAdvisoryRole`
- `NeedsHumanTriage_PassesAdvisoryRole`
- `CriticalityJudge_PassesAdvisoryRole`

Four of the seven MUST FAIL against your stub (the three `Action` sites pass, which is correct and
is the discriminator proving the tests are bound to the real code path).

Every existing test fixture that constructs a `PromptInvocation` will stop compiling. Fixing those
is IN SCOPE only for files inside your writeScope - if a fixture outside it breaks, that is a real
finding: write `needsHuman` rather than editing out of scope.

**Do NOT implement the correct assignments.** That is task 02's deliverable.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Prompts/PromptRoleSeamTests.cs`, `src/Guardrails.Core/Prompts/PromptInvocation.cs`, `src/Guardrails.Core/Execution/ActionRunner.cs`, `src/Guardrails.Core/Execution/WaveBreakdownInvoker.cs`, `src/Guardrails.Core/Execution/AiMergeResolver.cs`, `src/Guardrails.Core/Execution/GuardrailRunner.cs`, `src/Guardrails.Core/Execution/Overwatch.cs`, `src/Guardrails.Core/Execution/NeedsHumanTriage.cs`, `src/Guardrails.Core/Execution/CriticalityJudge.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
