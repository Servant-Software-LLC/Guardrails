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
  But the tests don't exist. **Catalogue rule: INSERT a test-author task upstream**
  whose own guardrails include **tests-fail-on-current-code** (proving the tests
  actually encode the not-yet-built behavior).
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

### `tasks/01-author-stats-tests/` — **INSERTED TASK**

`task.json`
```jsonc
{
  "description": "Author failing unit tests that encode the --stats output format (total line + sorted per-category lines)",
  "dependsOn": []
}
```

`action.prompt.md`
```markdown
## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"}
  to the state-out path and stop.

## Task
Author unit tests in `tests/Inventory.Tests/StatsCommandTests.cs` (trait/category
`Stats`) that encode the --stats contract BEFORE it is implemented:
- `inventory --stats` against a fixture items file prints `total: N` first,
- then one `«category»: N` line per category, sorted ordinally by category name.
Use the existing test conventions in tests/Inventory.Tests. The tests MUST fail (or
be unable to find the flag) against the current code — they test behavior that does
not exist yet. Do not implement the flag.
Publish nothing to state.
```

`guardrails/01-tests-build.ps1`
```powershell
# catches: test code that doesn't compile (a "test" that can't run verifies nothing)
dotnet build tests/Inventory.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Output "tests/Inventory.Tests does not build"; exit 1 }
exit 0
```

`guardrails/02-tests-fail-on-current-code.ps1`
```powershell
# catches: tautological tests — tests that already pass against code with NO --stats
#          flag encode nothing about the new behavior
dotnet test tests/Inventory.Tests --filter "Category=Stats" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the new Stats tests PASS against current code - they are tautological"
    exit 1
}
exit 0
```

### `tasks/02-implement-stats-flag/`

`task.json`
```jsonc
{
  "description": "Implement --stats in src/Inventory.Cli so the Stats tests pass",
  "dependsOn": ["01-author-stats-tests"]
}
```

`action.prompt.md` — same harness-contract header, then:
```markdown
## Task
Implement the `--stats` flag in `src/Inventory.Cli` per the format the Stats tests
encode (total line, then sorted per-category lines, read from data/items.json).
Make the `Category=Stats` tests pass without modifying the tests.
Publish nothing to state.
```

`guardrails/01-build.ps1` → `# catches: code that doesn't compile` + `dotnet build --nologo -v q`, exit-code contract.

`guardrails/02-stats-tests-pass.ps1`
```powershell
# catches: an implementation whose output deviates from the specified format
dotnet test tests/Inventory.Tests --filter "Category=Stats" --nologo
if ($LASTEXITCODE -ne 0) { Write-Output "Stats tests failing - flag not implemented to spec"; exit 1 }
exit 0
```

`guardrails/03-tests-untouched.ps1`
```powershell
# catches: "making tests pass" by editing the tests instead of the implementation
$changed = git diff --name-only HEAD -- tests/Inventory.Tests/StatsCommandTests.cs
if ($changed) { Write-Output "StatsCommandTests.cs was modified by the implementation task"; exit 1 }
exit 0
```

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
if ($readme -notmatch '--stats') { Write-Output "README.md does not mention --stats"; exit 1 }
if ($readme -notmatch 'total:') { Write-Output "README.md lacks a usage example showing the output"; exit 1 }
exit 0
```

### `tasks/04-suite-green/` — terminal integration task

`task.json`
```jsonc
{ "description": "Whole test suite green (terminal gate)", "dependsOn": ["02-implement-stats-flag", "03-update-readme"] }
```

`action.ps1` → `exit 0` (a pure verification task; the guardrail is the point).

`guardrails/01-full-suite.ps1`
```powershell
# catches: the --stats work regressing anything elsewhere in the suite
dotnet test --nologo
if ($LASTEXITCODE -ne 0) { Write-Output "full suite has failures after the --stats work"; exit 1 }
exit 0
```

## Step 7 — the closing report (what the skill says to the user)

> Breakdown of `add-stats-flag.md` → `add-stats-flag/` — **4 tasks** (plan listed 2):
>
> | Task | Action | Guardrails (archetypes) | dependsOn |
> |---|---|---|---|
> | 01-author-stats-tests *(INSERTED)* | prompt | tests-build (3), tests-fail-on-current-code (8) | — |
> | 02-implement-stats-flag | prompt | build (3), stats-tests-pass (4), tests-untouched (1) | 01 |
> | 03-update-readme | prompt | readme-mentions-flag (1) | 02 |
> | 04-suite-green | script | full-suite (4, terminal-only) | 02, 03 |
>
> Inserted: `01-author-stats-tests` — because 02's strongest guardrail is "Stats tests
> pass" and those tests didn't exist. Its tests-fail-on-current-code guardrail proves
> they're not tautological. `guardrails validate add-stats-flag` → OK.
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
