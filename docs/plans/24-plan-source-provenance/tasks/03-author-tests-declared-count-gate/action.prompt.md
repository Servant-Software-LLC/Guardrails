## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "03-author-tests-declared-count-gate": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
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

Author **failing xUnit tests** for a new `DeclaredCountGate`, plus the **minimal stubs** those tests
compile against.

**Write exactly two files:**

1. `tests/Guardrails.Core.Tests/PlanSource/DeclaredCountGateTests.cs` — the test file. The test class
   MUST be named **`DeclaredCountGateTests`** and every test MUST carry
   `[Trait("Category", "PlanSourceProvenance")]`. Both are load-bearing: this task pair's guardrails
   filter on the class name, and the plan's baseline preflight excludes that trait.
2. `src/Guardrails.Core/Breakdown/DeclaredCountGate.cs` — minimal skeleton stubs ONLY, whose members
   throw `NotImplementedException`, so the test project COMPILES.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/PlanSource/DeclaredCountGateTests.cs` and
`src/Guardrails.Core/Breakdown/DeclaredCountGate.cs` (the stub file). After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths — including changes to other
production files, neighbouring test files (`PlanSourceRecordTests.cs` in particular, which a sibling
task owns), or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.
If you hit a compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

The test project is SDK-style and globs `**/*.cs`, so a new `PlanSource/` subdirectory needs **no**
`.csproj` edit. Use the namespace `Guardrails.Core.Tests.PlanSource`, matching the
folder-per-namespace convention the existing `tests/Guardrails.Core.Tests/Execution/` files use.

**The tests MUST COMPILE and FAIL against the stubs.** Failing is intentional; NOT compiling is a
mistake to fix. Do NOT implement the behaviour — write the tests and only the minimal throwing stubs.

### What the gate is

After the breakdown agent returns, the harness compares what it READ against what the agent PRODUCED
(`docs/plans/24-plan-source-provenance.md` section 4):

> The harness read a plan declaring **N** delegated decisions. The folder records **M**. If **N >= 1**
> and **M != N**, fail the breakdown.

**N** is `declaredDelegatedDecisions` from the plan-source record (a sibling task's deliverable — take
it as an input, an `int`, not something this gate re-derives). **M** is what the produced plan folder
records: the number of `## DECISION` sections in `<planFolder>/decisions.md`, and **0** when that file
does not exist. That absent-file case is the whole point — a breakdown that never ran the delegated-
decision scan produces no `decisions.md`, so M = 0, and the gate reds. It is the one case the
plan-root preflight structurally cannot catch, because that preflight is authored by the very agent it
polices.

The `## DECISION` heading shape is the `plan-breakdown` skill's own `decisions.md` format contract
(skill Step 0d.4) — read `.claude/skills/plan-breakdown/SKILL.md` section "0d.4 RECORD" before you
encode the counting rule, and encode what you find there rather than what this prompt paraphrases.

### The behaviours to encode, each bound to a PINNED test method name

Author exactly these test methods, named verbatim — the red census greps for these names:

| Test method name | Behaviour |
|---|---|
| `FailsWhenTheFolderRecordsFewerThanTheDeclaredCount` | N = 2, folder records 1 → the gate FAILS. |
| `FailsWhenTheFolderRecordsMoreThanTheDeclaredCount` | N = 2, folder records 3 → the gate FAILS. The rule is `M != N`, not `M < N` — both directions mean the agent and the plan disagree. |
| `FailsWhenNoDecisionsFileExistsAndThePlanDeclaresOne` | N = 1, **no `decisions.md` at all** ⇒ M = 0 → the gate FAILS. This is the never-scanned breakdown, the case the plan-root preflight cannot see. |
| `PassesWhenTheRecordedCountEqualsTheDeclaredCount` | N = 2, folder records 2 → the gate PASSES. |
| `PassesWhenThePlanDeclaresZero` | N = 0 → the gate PASSES regardless of what the folder records. The gate binds only at N >= 1; a plan with no count line is not evidence of anything. |
| `CountsOneDecisionPerSectionInTheDecisionsFile` | A `decisions.md` holding three `## DECISION` sections counts as M = 3 — the count is SECTIONS, not lines, not file existence. |
| `FailureMessageNamesTheDeclaredAndRecordedCounts` | The failure text contains both numbers. A gate that fails without saying `2` and `0` sends the reader to go and count by hand. |
| `FailureMessageStatesBothLimitsOfTheCheck` | The failure text STATES the two limits rather than leaving them to be discovered: (a) it proves the **count**, never that a decision was made **well**; (b) it depends on Charter's count-line guarantee, so markers present with **no** count line is a Charter bug to file there, not a plan defect. |

The last two are testable text assertions — assert on substrings that survive rewording (the two
numbers; a phrase such as "not" + "well", "Charter"). Do not pin the whole sentence; pin the facts
that must be in it.

### Fixtures

Build plan folders in a temp directory and clean up in a `finally` or `IDisposable` — never write into
the repository tree. A fixture is just a directory with (optionally) a `decisions.md` in it; the gate
takes the folder path and the declared count. Keep the fixtures minimal: this gate does not load a
plan, and a test that stands up a whole plan folder is testing something else.

### The stub file

`DeclaredCountGate.cs` needs only enough shape for the tests to compile — e.g. a static
`Evaluate(int declaredDelegatedDecisions, string planFolder)` returning a small result type exposing
whether it passed, the declared count, the recorded count and the failure message, with every body
`throw new NotImplementedException();`. You are authoring the contract here, so choose the shape you
think the implementation wants — but keep the stub minimal and do NOT implement it.
