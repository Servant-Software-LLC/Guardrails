# Worked example — a complete breakdown, end to end

This is the few-shot reference for the plan-breakdown procedure. Input: a small,
reviewed plan. Output: a **3-task** folder PLUS two inserted, plan-level four-folder artifacts the
plan never mentioned — a brownfield **positive-baseline preflight** (`<plan>/preflights/`, #181) and a
**terminal integration gate** (`<plan>/guardrails/`, §3.3) — and one **inserted TDD test-author task**,
each because a guardrail or doctrine needed it.

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

## Step 0 — brownfield (the area already has tests)

The plan modifies `src/Inventory.Cli`, an EXISTING CLI with an existing `tests/Inventory.Tests`
project. That makes this a **brownfield** plan (`$baselineArea = tests/Inventory.Tests`, ONE touched
test project). The **worth-it gate passes**: the test project pre-exists, the plan MODIFIES it (not
creates), the check is cheap/deterministic (a filtered `dotnet test` — a bounded run, not a
live-service boot/poll), it is strictly narrower than the terminal full-suite gate, and ≥2 work tasks
(`01-author-stats-tests`, `02-implement-stats-flag`) build on the area. So Step 5 emits ONE
**positive-baseline preflight CHECK** in the plan-root `<plan>/preflights/` folder so the existing
tests are confirmed green BEFORE the DAG runs ("never build on red", #181) — a guardrail-shaped FILE,
not a no-op ROOT task (the retired model). (A greenfield plan — a new project with no existing tests —
would SKIP the baseline and the report would state why; the runnable `examples/**` greeting demos are
greenfield and correctly carry no baseline.)

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
- Because the plan is brownfield and the worth-it gate passes (Step 0), one **positive-baseline
  preflight CHECK** `<plan>/preflights/01-baseline-inventory-tests-green.ps1` is emitted: a
  guardrail-shaped FILE (no task, no action) that runs the EXISTING `tests/Inventory.Tests` tests and
  asserts they pass on the starting code. The plan-root `preflights/` folder is evaluated ONCE, BEFORE
  the DAG, against the starting repo — so it implicitly gates every task with no edges to wire. Its
  check **filters** to the PRE-EXISTING tests (`--filter "Category!=Stats"`) — NOT a whole-project
  `dotnet test`, which would hit the #165/#176 compile-coupling trap once the `Stats` tests reference
  the not-yet-implemented `StatsCommand` — so it never goes red on the about-to-be-authored `Stats`
  tests (#181).
- A terminal **whole-suite green** gate closes the run in the plan-root `<plan>/guardrails/` folder
  (the only place "all tests pass" is allowed) — evaluated ONCE, at run end, on the merged HEAD, NOT a
  task. The baseline and the terminal gate are **complementary** — green START on the EXISTING area
  before the DAG, green END on EVERYTHING at the merged HEAD.
- Edges: `01-author-stats-tests` is a DAG root (`dependsOn: []`) — the `<plan>/preflights/` baseline
  gates it with no edge to author. `02-implement` depends on `01-author-stats-tests` (guardrail
  dependency: 02's guardrail runs 01's artifact). `03-update-readme` depends on nothing — the plan
  FULLY specifies the `--stats` output format, so the README's usage example is authored from that
  spec, not from the running implementation (sparsest DAG — a `03 → 02` edge would be a false edge that
  needlessly serializes the run). So **`02-implement` and `03-update-readme` are the plan's two
  leaves**; they integrate at run end, where the terminal `<plan>/guardrails/` folder re-runs the
  integration set on the merged HEAD. No prose-order-only edges.

Plan said 2 steps; the breakdown emits **3 tasks** plus a plan-level baseline preflight and a
plan-level terminal gate (a TDD test-author task, the implementation, the README, the
`<plan>/preflights/` baseline, and the `<plan>/guardrails/` terminal gate). That delta is the skill
working as designed, not scope creep.

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
      "allowedTools": [
        "Read", "Edit", "Write", "Grep", "Glob", "Bash(dotnet *)",
        "Bash(git log*)", "Bash(git diff*)", "Bash(git show*)", "Bash(git status*)"
      ],
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

The four `Bash(git …*)` entries are READ-ONLY inspection commands (#252) — this plan has ≥2 tasks
joined by `dependsOn` (`01-author-stats-tests` → `02-implement-stats-flag`), so
`02-implement-stats-flag`'s action prompt may reasonably want to run `git diff` / `git log` to see
exactly what `01-author-stats-tests` committed (the test file and its stub) before extending the stub
into real logic — the textbook case the default now covers. Note what is deliberately absent:
`restore`, `reset`, `checkout`, `push`, `commit`, `stash` — every state-mutating git operation stays
outside `allowedTools`.

### `preflights/01-baseline-inventory-tests-green.ps1` — **INSERTED** (brownfield positive-baseline preflight, #181)

Under the four-folder model the baseline is a guardrail-shaped **FILE** in the plan-root
`<plan>/preflights/` folder (the "Full Flight Checks"), evaluated ONCE, BEFORE the DAG, against the
starting repo — **not** a no-op ROOT task (the retired model). There is no `task.json`, no action, and
no `dependsOn` to wire: a failing preflight halts the run before any task is scheduled, so every task
is implicitly gated on it. The file IS the verification.

It runs the EXISTING `tests/Inventory.Tests`, **filtered to the pre-existing tests** (`Category!=Stats`)
so the about-to-be-authored `Stats` tests can never make the baseline red, and re-emits the failure
detail at the END so a red baseline's WHY reaches the halt feedback (#179; `stacks/dotnet.md §21`):
```powershell
# catches: a brownfield plan building on a RED base - the existing tests in tests/Inventory.Tests are
#          already failing on the starting code. Asserting them green BEFORE the DAG means a later
#          work task's tests-pass failure is attributable to THAT task, not pre-existing breakage, and
#          the new Stats tests' red is unambiguous (#181). Re-emits the failure DETAIL at the END so a
#          red baseline's WHY reaches the halt feedback (#179, §4.2). Filtered to the PRE-EXISTING
#          tests (Category!=Stats) - it must NOT run the about-to-be-authored Stats tests.
$out = dotnet test tests/Inventory.Tests --filter "Category!=Stats" --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the halt feedback) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    Write-Output "the existing tests in tests/Inventory.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181)"
    exit 1
}
exit 0
```
`preflights/01-baseline-inventory-tests-green.json`:
```jsonc
{ "description": "Existing area tests pass on the current code (baseline-green preflight, #181)" }
```

(No task, no action, no `writeScope`. This is the brownfield baseline-green preflight; a greenfield
plan would omit it entirely and the report would say why. A RED preflight halts the run before the DAG
— no retry budget is burned, because there is no task.)

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

`guardrails/02-stats-tests-pass.ps1` — captures the run, emits the full log, then **re-emits the
failure detail at the END** so the assertion/exception text lands in the harness retry-feedback tail
(the last ~60 lines), not just the `[FAIL] <name>` summary default `dotnet test` leaves there (#179;
`stacks/dotnet.md §4.2`):
```powershell
# catches: an implementation whose output deviates from the specified format. Re-emits the
#          assertion/exception lines at the END so they reach the harness retry-feedback tail -
#          default `dotnet test` prints them mid-run and ends with only `[FAIL] <name>` + a count,
#          so the tail would otherwise show WHAT failed, not WHY (#179).
$out = dotnet test tests/Inventory.Tests --filter "Category=Stats" --no-build --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    Write-Output "Stats tests failing - flag not implemented to spec (see failure details above)"
    exit 1
}
exit 0
```

No `tests-untouched` guardrail is needed. This task's `writeScope` (`src/Inventory.Cli/`) EXCLUDES
`tests/Inventory.Tests/StatsCommandTests.cs`, so the harness's deterministic write-scope check
(SSOT §3.4) fails the task if its diff edits the test file — "make the tests pass without modifying
them", enforced by a read-only `git diff` membership test rather than a hash compare. No shared-state
hash exists to forge, so the cross-task poisoning surface is gone entirely.

### `tasks/03-update-readme/` — the plan's second leaf

`task.json` — **`dependsOn: []`**: the plan fully specifies the `--stats` output format, so the
README's usage example is authored from that spec, independent of the running implementation. A
`03 → 02` edge would be a false edge that needlessly serializes the run (Step 3, sparsest DAG). This
makes `02-implement` and `03-update-readme` the plan's **two leaves** — the parallelism whose union at
run end the terminal `<plan>/guardrails/` folder verifies.
```jsonc
{ "description": "Document --stats in README.md (from the specified output format)", "dependsOn": [] }
```

`action.prompt.md` — harness-contract header, then: document the flag with a usage
example matching the specified output format.

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

### `guardrails/` — the terminal integration gate (plan-root FOLDER, §3.3)

The terminal whole-repo integration gate is the plan-root **`<plan>/guardrails/`** folder — evaluated
ONCE, at run end, on the merged plan-branch HEAD, **not a task** (the `integrationGate: true` task kind
is retired; still declaring it is a hard validation error, **GR2029**). There is no `task.json` and no
`action.*` here — the folder holds guardrail-shaped files evaluated by the terminal phase directly. The
plan has two leaves (`02-implement`, `03-update-readme`) in worktree mode (`maxParallelism: 4`), so
**GR2028** (the re-homed GR2018 content teeth) requires this folder to carry **≥1 deterministic check
that ACTUALLY re-runs the integration set**. It carries **two** files, cheapest-first, with the right
scopes (#165):

`guardrails/01-union-clean.ps1` — the `scope: "integration"` union invariant (the real integration-set
re-run that satisfies GR2028). It is a **union-safe conditional invariant** (conflict-marker-free +
non-empty), so it passes trivially at intermediate unions where a leaf has not integrated yet. It does
**NOT** assert "both leaves' work is present" (a terminal postcondition that would false-RED at the
first union) — it gates on each file being present, then verifies it:
```powershell
# catches: a union that left git conflict markers in the merged bytes, or an empty source/doc file —
# the deterministic verdict on EVERY union's bytes, re-run at each non-FF integration and the terminal HEAD.
# scope:"integration" → MUST be union-safe (#125): gate-then-verify, never "require both leaves present".
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
foreach ($rel in @('src/Inventory.Cli/StatsCommand.cs', 'README.md')) {
    $p = Join-Path $ws $rel
    if (-not (Test-Path $p)) { continue }   # not integrated at this union yet — fine
    $content = Get-Content -Raw -Path $p
    if ([string]::IsNullOrWhiteSpace($content)) {
        Write-Output "$rel is empty on the merged bytes"
        exit 1
    }
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {   # line-anchored, no bare '=======' — false-positive-free (#187)
        Write-Output "$rel contains git conflict markers — the union did not cleanly integrate"
        exit 1
    }
}
exit 0
```
`guardrails/01-union-clean.json`:
```jsonc
{
  "description": "Union invariant: merged files are non-empty and conflict-marker-free",
  "scope": "integration"
}
```

`guardrails/02-full-suite.ps1` — the whole test suite. This is **LOCAL** (no `scope` key): a full
suite is a **terminal postcondition**, not a union-safe invariant — it runs only here, ONCE, at the
terminal gate on the merged HEAD after both leaves have merged. Marking it `scope: "integration"`
would re-run it at every intermediate union and, on any TDD plan, red-halt a correct partial merge
(#165):
```powershell
# catches: the --stats work regressing anything elsewhere in the suite. Re-emits the failing
#          assertion/exception lines at the END so they reach the harness retry-feedback tail
#          (last ~60 lines), not just the `[FAIL] <name>` summary default `dotnet test` leaves (#179).
$out = dotnet test --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    Write-Output "full suite has failures after the --stats work (see failure details above)"
    exit 1
}
exit 0
```
`guardrails/02-full-suite.json` (LOCAL — the `scope` key is deliberately ABSENT):
```jsonc
{
  "description": "Whole test suite green"
}
```

## Step 7 — the closing report (what the skill says to the user)

> Breakdown of `add-stats-flag.md` → `add-stats-flag/` — **3 tasks** + a plan-level baseline
> preflight + a plan-level terminal gate (plan listed 2):
>
> | Task | Action | Guardrails (archetypes) | dependsOn |
> |---|---|---|---|
> | 01-author-stats-tests *(INSERTED)* | prompt | build-passes (3), tests-fail-on-stubs (8); `writeScope` owns the test file + the `StatsCommand` stub; Scope boundary paragraph | — (root) |
> | 02-implement-stats-flag | prompt | build (3), stats-tests-pass (4); `writeScope` targets the stub, EXCLUDES the test file | 01 |
> | 03-update-readme | prompt | readme-mentions-flag (1) | — (root, independent leaf) |
>
> | Plan-level folder | Files (archetypes) |
> |---|---|
> | `preflights/` *(INSERTED, #181)* | baseline-inventory-tests-green (existing `tests/Inventory.Tests`, `--filter "Category!=Stats"`, area-scoped, #179-re-emit) |
> | `guardrails/` (terminal gate, §3.3) | union-clean (union-safe invariant, `scope: "integration"`, satisfies GR2028) + full-suite (4, terminal-only, **LOCAL**) |
>
> Inserted (plan-level, `<plan>/preflights/`): `01-baseline-inventory-tests-green` — this is a
> **brownfield** plan (it modifies the existing `src/Inventory.Cli` with an existing
> `tests/Inventory.Tests`) and the worth-it gate passes (the area pre-exists, the plan modifies it, the
> check is cheap/deterministic, it is narrower than the terminal gate, and ≥2 work tasks build on the
> area), so one **positive-baseline preflight** confirms the EXISTING area tests pass on the starting
> code BEFORE the DAG runs ("never build on red", #181). It is a guardrail-shaped FILE in the plan-root
> `<plan>/preflights/` folder — NOT a no-op ROOT task (the retired model) — evaluated once against the
> starting repo, so it gates every task with no edges to author. It runs `tests/Inventory.Tests`
> **filtered** to the pre-existing tests (`--filter "Category!=Stats"` — NOT a whole-project `dotnet
> test`, which would false-red on the #165/#176 compile-coupling trap, and so it never goes red on the
> about-to-be-authored `Stats` tests) and asserts they pass, re-emitting the failure detail at the END
> (#179) so a red baseline's WHY reaches the halt feedback. It is **distinct** from the terminal
> `<plan>/guardrails/` gate: green START on the EXISTING area before the DAG vs green END on EVERYTHING
> at the merged HEAD. A RED preflight halts the run before any task is scheduled — a fast, actionable
> halt, with no retry budget burned (there is no task).
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
> hashing, no `tests-untouched` guardrail. The plan's two leaves (`02-implement`, `03-update-readme`)
> integrate at run end, where the terminal `<plan>/guardrails/` folder re-runs the integration set on
> the merged HEAD (the `integrationGate: true` task kind is retired — GR2029). Its GR2028
> `scope: "integration"` guardrail is the **union-safe** `01-union-clean` (conflict-marker-free +
> non-empty, gate-then-verify — safe at every union); the whole-suite `02-full-suite` is **LOCAL** (no
> `scope`), because a full suite is a terminal postcondition that would red-halt a correct intermediate
> union if it re-ran there (#165). `guardrails validate add-stats-flag` → OK.
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
- **No positive-baseline preflight** (`<plan>/preflights/`, #181), though the plan is brownfield (it
  builds on the existing `tests/Inventory.Tests`) — so if the area's existing tests were already red,
  the one task's guardrail would fail on pre-existing breakage and the failure would be
  misattributed to "implement --stats and update the README."
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
