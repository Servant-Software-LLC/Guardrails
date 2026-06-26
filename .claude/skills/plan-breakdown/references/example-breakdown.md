# Worked example — a complete breakdown, end to end

This is the few-shot reference for the plan-breakdown procedure. Input: a small,
reviewed plan. Output: a 4-task folder — including one **inserted** task the plan
never mentioned, because a guardrail needed it.

## Input plan (`add-stats-flag.md`)

> # Add a --stats flag to the inventory CLI
>
> Our `inventory` CLI (C#, `src/Inventory.Cli`, tests in `tests/Inventory.Tests`)
> needs a `--stats` flag that prints the total item count and the count per
> category, read from `data/items.json`.
>
> 1. Implement `--stats` in the CLI. Output format: one line `total: N`, then one
>    line per category `«category»: N`, sorted by category name.
> 2. Update README.md to document the new flag.
>
> Done when the flag works against the sample data file and the docs mention it.

## Step 1 — scratch table

| Item | Deliverable artifact | Completion evidence available | Hinted deps |
|---|---|---|---|
| Implement `--stats` | code in `src/Inventory.Cli` | exact output format is specified → testable | needs tests that encode the format |
| Update README | `README.md` section | "docs mention it" → file-contains | after the flag exists (documents real behavior) |

Non-executable plan content: none (both items have observable deliverables).

## Steps 2–4 — sizing, DAG, guardrail reasoning (the thinking)

- "Implement --stats" is one verifiable outcome. Its strongest guardrail is
  **specific-tests-pass** — the output format is fully specified, perfect for tests.
  But the tests don't exist. **Catalogue rule: INSERT a test-author task upstream.**
  `--stats` is a **behavioral** deliverable (a `StatsCommand` class computes the counts),
  so by the stub-based TDD rule (catalogue → "Stub-based TDD"; SKILL Step 5) the test-author
  task ALSO writes the **minimal stub** (`StatsCommand` whose `Render` throws
  `NotImplementedException`) so the tests COMPILE, and its guardrails are the two-guardrail
  pair **`build-passes`** (the test file is type-correct against the stub) + **`tests-fail-on-stubs`**
  (the tests run and FAIL against the throwing stub — true TDD red, not a compile failure that
  garbage could fake). The implementation task then fills real logic over the stub.
- README update is a separate task — its verification (file-contains) is a different
  character than the implementation's (tests), and bundling it would make a doc typo
  burn an expensive implementation retry.
- A terminal **whole-suite green** task closes the DAG (the only place "all tests
  pass" is allowed).
- Edges: `02-implement` depends on `01-author-tests` (guardrail dependency: 02's
  guardrail runs 01's artifact). `03-update-readme` depends on `02-implement`
  (semantic: documents real behavior). `04-suite-green` depends on 02 and 03.
  No prose-order-only edges.

Plan said 2 steps; the breakdown emits **4 tasks**. That delta is the skill working
as designed, not scope creep.

## Steps 5–6 — every generated file

### `add-stats-flag/guardrails.json`

```jsonc
{
  "version": 1,
  "maxParallelism": 4,
  "defaultRetries": 2,
  "defaultTimeoutSeconds": 1800,
  "guardrailMode": "failFast",
  "workspace": "..",
  "promptRunners": {
    "default": "claude",
    "claude": {
      "command": "claude",
      "permissionMode": "acceptEdits",
      "allowedTools": ["Read", "Edit", "Write", "Grep", "Glob", "Bash(dotnet *)"],
      "maxTurns": 50,
      "guardrailOverrides": {
        "permissionMode": "default",
        "allowedTools": ["Read", "Grep", "Glob", "Write"],
        "maxTurns": 15
      }
    }
  }
}
```

### `tasks/01-author-stats-tests/` — **INSERTED TASK** (behavioral type → tests + minimal stubs)

`task.json` — `--stats` is **behavioral** (a `StatsCommand` class renders the output), so this
test-author task writes BOTH the test file AND the minimal stub the tests compile against, and its
`writeScope` covers both paths (#155). That scope is the test-author half of the TDD test-protection:
this task may write the test file and the stub; the implementation task (02) declares a `writeScope`
that EXCLUDES the test file but TARGETS the stub (`src/Inventory.Cli/` covers it), so the harness's
deterministic write-scope check (SSOT §3.4) catches an implementation that edits the **tests** instead
of fixing the code. No `captureHashes`, no `restoreOnRetry`, no downstream `tests-untouched` guardrail.
```jsonc
{
  "description": "Author failing unit tests + a minimal StatsCommand stub for the --stats output format (total line + sorted per-category lines)",
  "dependsOn": [],
  "writeScope": [
    "tests/Inventory.Tests/StatsCommandTests.cs",
    "src/Inventory.Cli/StatsCommand.cs"
  ]
}
```

`action.prompt.md`
```markdown
## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `01-author-stats-tests`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-stats-tests": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"}
  to the state-out path and stop.

## Task
Author unit tests in `tests/Inventory.Tests/StatsCommandTests.cs` (trait/category
`Stats`) that encode the --stats contract BEFORE it is implemented, plus the **minimal
stub** the tests compile against in `src/Inventory.Cli/StatsCommand.cs`.

**Scope boundary (harness-enforced):** Write only to
`tests/Inventory.Tests/StatsCommandTests.cs` and `src/Inventory.Cli/StatsCommand.cs` (the
stub). After this task completes, the harness runs a `git diff` check and rejects any edit
outside these paths — including changes to other production files, neighbouring test files,
or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you
hit a compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

The stub is a real `StatsCommand` class whose `Render(...)` method
`throw new NotImplementedException();` — just enough for the test project to COMPILE. The tests
must then encode the behavior:
- `StatsCommand.Render` against a fixture items file produces `total: N` first,
- then one `«category»: N` line per category, sorted ordinally by category name.
Use the existing test conventions in tests/Inventory.Tests. The tests MUST **compile** (against
the stub) and **fail** (the stub throws) — failing is intentional, NOT compiling is a mistake.
Do not implement the real --stats logic.

You do NOT need to hash anything or write to state. Publish nothing to state.
```

`guardrails/01-build-passes.ps1`
```powershell
# catches: a test file that does not COMPILE - garbage or a real type error. With the minimal
#          StatsCommand stub in place the test project must build; a non-compiling "test" exits
#          dotnet test non-zero identically to a failing one, so without this the red signal is
#          gameable by garbage (#155).
dotnet build tests/Inventory.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Inventory.Tests does not build - the test file or the StatsCommand stub is not type-correct"
    exit 1
}
exit 0
```

`guardrails/02-tests-fail-on-stubs.ps1`
```powershell
# catches: tautological tests — tests that PASS against the NotImplementedException stub encode
#          nothing about the new behavior. The build being green (guardrail 01) means a non-zero
#          exit here unambiguously means the tests RAN and FAILED against the stub = TDD red (#155).
dotnet test tests/Inventory.Tests --filter "Category=Stats" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the Stats tests PASS against the NotImplementedException stub - they are tautological"
    exit 1
}
exit 0
```

(No `state-fragment-written` guardrail is needed: this task publishes nothing to state. Its
test file is protected downstream by task 02's `writeScope` excluding it, not by any state
hand-off — while task 02's scope still TARGETS the `StatsCommand.cs` stub so it can fill the logic.)

### `tasks/02-implement-stats-flag/`

`task.json` — declares a `writeScope` scoped to the implementation source. `src/Inventory.Cli/`
COVERS the `StatsCommand.cs` stub task 01 wrote (so this task may fill its logic) and EXCLUDES the
test file `tests/Inventory.Tests/StatsCommandTests.cs`. The harness's deterministic write-scope check
(SSOT §3.4) then fails this task if its diff touches the test file — "the implementation may not write
the tests", enforced without any hash compare or `tests-untouched` guardrail.
```jsonc
{
  "description": "Implement --stats in src/Inventory.Cli (fill StatsCommand logic over the stub) so the Stats tests pass",
  "dependsOn": ["01-author-stats-tests"],
  "writeScope": ["src/Inventory.Cli/"]
}
```

`action.prompt.md` — same harness-contract header, then:
```markdown
## Task
Implement the `--stats` flag in `src/Inventory.Cli` by filling real logic into the
`StatsCommand.Render(...)` stub (replace the `NotImplementedException`) per the format the
Stats tests encode (total line, then sorted per-category lines, read from data/items.json).
Make the `Category=Stats` tests pass WITHOUT modifying the tests — editing
`tests/Inventory.Tests/StatsCommandTests.cs` is outside this task's writeScope and fails the
harness's git-diff check. If the authored tests are genuinely wrong, write
`{"needsHuman": "<why>"}` rather than editing them. Publish nothing to state.
```

`guardrails/01-build.ps1` → `# catches: code that doesn't compile` + `dotnet build --nologo -v q`, exit-code contract.

`guardrails/02-stats-tests-pass.ps1`
```powershell
# catches: an implementation whose output deviates from the specified format
dotnet test tests/Inventory.Tests --filter "Category=Stats" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "Stats tests failing - flag not implemented to spec"
    exit 1
}
exit 0
```

No `tests-untouched` guardrail is needed. This task's `writeScope` (`src/Inventory.Cli/`) EXCLUDES
`tests/Inventory.Tests/StatsCommandTests.cs`, so the harness's deterministic write-scope check
(SSOT §3.4) fails the task if its diff edits the test file — "make the tests pass without modifying
them", enforced by a read-only `git diff` membership test rather than a hash compare. No shared-state
hash exists to forge, so the cross-task poisoning surface is gone entirely.

### `tasks/03-update-readme/`

`task.json`
```jsonc
{ "description": "Document --stats in README.md", "dependsOn": ["02-implement-stats-flag"] }
```

`action.prompt.md` — harness-contract header, then: document the flag with a usage
example matching the real output.

`guardrails/01-readme-mentions-flag.ps1`
```powershell
# catches: claimed doc update that never landed or doesn't mention the flag
$readme = Get-Content README.md -Raw
if ($readme -notmatch '--stats') {
    Write-Output "README.md does not mention --stats"
    exit 1
}
if ($readme -notmatch 'total:') {
    Write-Output "README.md lacks a usage example showing the output"
    exit 1
}
exit 0
```

### `tasks/04-suite-green/` — terminal integration task

`task.json` — sets `integrationGate: true` (SSOT §3.3): this is the run's terminal whole-repo
integration gate, the lone sink the two leaf tasks (02, 03) fan into. The plan has ≥2 leaf tasks
and a fan-in, so validation requires **exactly one** `integrationGate: true` sink (GR2017) carrying
**at least one** `scope: "integration"` guardrail (GR2018) — `01-full-suite` below is that guardrail.
A terminal whole-suite gate is a genuinely repo-wide task, so it declares **no `writeScope`**.
```jsonc
{
  "description": "Whole test suite green (terminal gate)",
  "dependsOn": ["02-implement-stats-flag", "03-update-readme"],
  "integrationGate": true
}
```

`action.ps1` → `exit 0` (a pure verification task; the guardrail is the point).

`guardrails/01-full-suite.ps1`
```powershell
# catches: the --stats work regressing anything elsewhere in the suite
dotnet test --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "full suite has failures after the --stats work"
    exit 1
}
exit 0
```

`guardrails/01-full-suite.json` — marks the whole-suite check `scope: "integration"` (SSOT §4.3),
so it joins the run's integration-guardrail set re-run at every union point and on the terminal
gate's merged HEAD. This is the `scope: "integration"` guardrail GR2018 requires on the gate sink.
```jsonc
{
  "description": "Whole test suite green",
  "scope": "integration"
}
```

## Step 7 — the closing report (what the skill says to the user)

> Breakdown of `add-stats-flag.md` → `add-stats-flag/` — **4 tasks** (plan listed 2):
>
> | Task | Action | Guardrails (archetypes) | dependsOn |
> |---|---|---|---|
> | 01-author-stats-tests *(INSERTED)* | prompt | build-passes (3), tests-fail-on-stubs (8); `writeScope` owns the test file + the `StatsCommand` stub; Scope boundary paragraph | — |
> | 02-implement-stats-flag | prompt | build (3), stats-tests-pass (4); `writeScope` targets the stub, EXCLUDES the test file | 01 |
> | 03-update-readme | prompt | readme-mentions-flag (1) | 02 |
> | 04-suite-green | script | full-suite (4, terminal-only, `scope: "integration"`); `integrationGate: true` | 02, 03 |
>
> Inserted: `01-author-stats-tests` — because 02's strongest guardrail is "Stats tests
> pass" and those tests didn't exist. `--stats` is behavioral, so this task also writes the
> minimal `StatsCommand` stub (`NotImplementedException`) the tests compile against, and its
> `build-passes` + `tests-fail-on-stubs` pair makes the TDD "red" mean "compiles AND fails"
> rather than "exits non-zero" (which non-compiling garbage would fake — #155). Its
> `action.prompt.md` carries a **Scope boundary (harness-enforced)** paragraph naming both
> allowed paths and redirecting an upstream compile error to `needsHuman` (#154). The TDD
> pair's `writeScope` declarations protect the tests deterministically: task 01 owns
> `tests/Inventory.Tests/StatsCommandTests.cs` AND `src/Inventory.Cli/StatsCommand.cs`; task
> 02's scope (`src/Inventory.Cli/`) TARGETS the stub but EXCLUDES the test file, so the
> harness's read-only write-scope check (SSOT §3.4) fails 02 if its diff edits the tests — no
> hashing, no `tests-untouched` guardrail. `04-suite-green` is the terminal `integrationGate:
> true` sink the two leaves fan into; its full-suite check is `scope: "integration"`.
> `guardrails validate add-stats-flag` → OK.
>
> **This is a draft.** Review the folder — especially the guardrails — edit, delete,
> or add, then run `/guardrails-review add-stats-flag` before executing.

---

# Negative example — the same plan, broken down badly

```
add-stats-flag/
└── tasks/01-do-everything/
    ├── action.prompt.md        # "implement --stats and update the README"
    └── guardrails/
        └── 01-looks-good.prompt.md   # "review the changes and pass if they look right"
```

Violations, by rule:
- **One task, two verification characters** (tests vs file-contains) — sizing rule 2.
- **Prompt-judge as the ONLY guardrail** — demotion gate question 2 (never alone).
- **"Looks right" is vibes** — gate question 3 (criterion-specific or demote).
- **Echo-judge risk** — the judge reviews "the changes", i.e. whatever the action
  says it did — gate question 4.
- **No inserted test task**, so nothing proves the output format — the deterministic
  evidence the plan offered ("format: one line total: N…") was thrown away.
- A doc typo retry would re-run the whole implementation — retry-cheapness violated.

A wrong implementation that prints unsorted categories, mislabels the total, and
never touches the README has a real chance of passing this folder. That is the
failure mode this skill exists to prevent.

---

# The state-out key is the FOLDER NAME, never the stableId (#164)

The worked plan above publishes nothing to state, so here is the shape for a task that
DOES. Suppose `01-research-tsw-write-mechanism` (folder name) has
`"stableId": "j9hf6y"` in its `task.json` and must publish its chosen mechanism for a
downstream task to branch on. The fragment it writes to `GUARDRAILS_STATE_OUT`:

```json
{ "01-research-tsw-write-mechanism": { "tsw_mechanism_recommended": "rest-api" } }
```

The single top-level key is the task's **FOLDER NAME** — the directory the `task.json`
lives in. It is **NOT** the `stableId`. This is wrong and the harness rejects it on
**every** attempt as a foreign/unowned key (the #164 failure loop — attempt 1 rejected,
files rolled back, every retry repeats it, dead-ending at `needsHuman`):

```json
{ "j9hf6y": { "tsw_mechanism_recommended": "rest-api" } }   // WRONG — stableId as key
```

`stableId` is an **internal regeneration-identity token** (§11), never the state key.
The producing prompt's `## Task` shows the correct folder-name-keyed fragment, the
harness-contract header repeats the rule, and the state-output guardrail indexes the
same folder name (`$fragment.'01-research-tsw-write-mechanism'.tsw_mechanism_recommended`)
— prompt, header, and guardrail all agree on the folder name as the key.
