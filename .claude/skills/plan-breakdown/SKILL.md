---
name: plan-breakdown
description: |
  Break a reviewed markdown plan into a Guardrails task folder — a dependency DAG of
  tasks, each with an action (script or prompt) and deterministic-first guardrails —
  executable by the `guardrails` CLI. Use when the user says "break down this plan",
  "generate tasks for <plan>.md", or hands you a plan path with that intent.
  Input: path to a REVIEWED `.md` plan. Output: a `<plan-name>/` folder next to the
  plan, self-validated with `guardrails validate`, presented as a DRAFT for human
  review. The skill leans deterministic (tests/regex/exit codes) over prompt-judges,
  and INSERTS guardrail-enabling tasks the plan never mentioned (e.g. "author the
  unit tests" before "implement the feature").
---

# Plan Breakdown

Turn a reviewed plan into an executable task DAG whose guardrails a human approves
once — instead of reviewing every agent output forever. The output is always a
**draft**: the human edits it, then `/guardrails-review` runs an adversarial pass,
and only then does `guardrails run` execute it.

**References (load as needed):**
- `references/guardrail-catalogue.md` — archetypes, decision tree, demotion gate,
  anti-patterns (UNIVERSAL doctrine). **Read before Step 4, every time.**
- `references/stacks/<stack>.md` — the STACK-SPECIFIC idioms (build-descriptor
  registration, cross-module reference, structural impl regex, canonical build command,
  grep-scope traps). **Load the one matching the detected stack in Step 0** (only
  `stacks/dotnet.md` ships today). The catalogue holds the universal rule; the stack file
  holds the exact regex/command.
- `references/stacks/ui.md` — the **two-level UI-verification methodology** (#41/#78):
  Level A liveness smoke vs Level B behavioral interaction-flow, the `$e2eStack` detection
  ladder, and the v2 boundary. **Read when the plan is UI-facing**, alongside the
  `Step 4b / 5c — Two-level UI verification` section below.
- `references/schemas.md` — exact file formats to emit (excerpt of the SSOT,
  `docs/plans/02-schemas-and-contracts.md`).
- `references/example-breakdown.md` — a complete worked breakdown including an
  inserted task, plus a negative example. Read when in doubt about output shape.

## Step 0 — Preconditions

1. Resolve the plan path. If the file doesn't exist, stop and say so.
2. If `<plan-name>/` already exists next to the plan, **never silently clobber** —
   ask: **merge** (default, preserves human guardrail edits), overwrite, or abort. A human
   may have edited that folder. On **merge**, follow the regeneration flow in Step 8.
3. Confirm the plan is *reviewed* (ask if unclear). Breaking down an unreviewed plan
   multiplies its errors into N tasks.
4. Check `guardrails --version` works. If not on PATH, warn that Step 7
   self-validation will be skipped and the output is unverified.
5. Identify the **workspace** (the repo the plan operates on — normally the folder
   containing the plan) and what already exists there: test framework, linter,
   build system. Guardrail selection depends on what's real. **Record WHICH test
   framework is present, not merely whether one is** — set `$testFramework` by scanning
   existing test projects for the framework dependency (.NET: a `PackageReference` to
   `xunit` / `NUnit` / `MSTest.TestFramework` in any `*.csproj`; node: `jest` / `vitest`
   / `mocha` in `package.json`; python: `pytest`; etc.). If no test project exists, set
   `$testFramework = none` — that is the trigger for the framework-selection rule in
   Step 5, **not** a licence to pick one silently.

   **Also decide brownfield vs greenfield FOR THE TOUCHED AREA — it gates the Step 5
   positive-baseline `<plan>/preflights/` check (#181).** Beyond "is there a test framework at all", record whether
   the projects/modules the plan will MODIFY already have existing tests covering them:
   - **Brownfield** = the plan modifies project(s)/module(s) that ALREADY have existing tests
     in the touched area. Set `$baselineArea` to the existing test project(s), each scoped by a
     **`--filter`** that selects the CURRENTLY-GREEN existing tests of that area (e.g.
     `tests/Inventory.Tests` filtered to the pre-existing tests — `--filter "Category!=Stats"` if a
     later `author-tests` task will add a `Stats` category to that project). **Never a whole-project
     `dotnet test`** in the preflight — that hits the #165/#176 compile-coupling trap (Step 5). Record ONE
     entry per distinct touched test project (the baseline is deduped one-per-area in Step 5). This is
     the trigger to EMIT the baseline `<plan>/preflights/` check(s) in Step 5 — subject to the worth-it gate there.
   - **Greenfield** = a new project, or no existing tests in the touched area. Set
     `$baselineArea = none`. Step 5 SKIPS the baseline preflight (nothing to baseline) and the
     Step 7 report states the reason. Do NOT emit a vacuous baseline that runs zero tests or
     asserts "0 failed" over an empty set.
   A plan can be brownfield in one area and greenfield in another (it extends an existing
   project AND adds a new one); scope `$baselineArea` to the EXISTING-tests portion only, one entry
   per touched test project.
6. **Detect the stack** from the workspace and load the matching stack file
   (`references/stacks/<stack>.md`) BEFORE guardrail selection (Steps 4–6):

   | Workspace signal (any match) | Stack | Stack file |
   |---|---|---|
   | `*.slnx` · `*.sln` · `*.csproj` | dotnet | `references/stacks/dotnet.md` *(ships)* |
   | `build.gradle` · `build.gradle.kts` · `pom.xml` | jvm | *(not authored yet)* |
   | `package.json` | node | *(not authored yet)* |
   | `go.mod` | go | *(not authored yet)* |
   | `pyproject.toml` · `requirements.txt` | python | *(not authored yet)* |

   - **Ambiguous (mixed monorepo, multiple signals)** or **no stack file exists yet** for
     the detected stack → FALL BACK to the core catalogue and **warn the user explicitly**:
     "stack <X> detected but no stack file ships yet (or the workspace mixes stacks); I'll
     use only the universal catalogue, so stack-specific guardrails (build-descriptor
     registration, cross-module references, structural impl checks) may be incomplete —
     review those especially." Never silently emit stack-agnostic guardrails as if complete.
   - When exactly one stack is detected and its file exists, load it and use its idioms
     wherever the catalogue points to the stack file (Steps 4–6).
   - A `## Stack` declared in `guardrails-patterns.md` (substep 7) **overrides
     auto-detection** — a human declaring the stack resolves an otherwise-ambiguous
     monorepo, so load that stack's file directly instead of falling back.

   *Future stacks: jvm / node / go / python — add as real projects on those stacks surface
   gaps (issue #13's sequencing). The routing above is generic, so a new `stacks/<stack>.md`
   is drop-in: author the file, and detection already routes to it.*
7. **Read the repo pattern file, if present** (`guardrails-patterns.md` at the workspace
   root or under `.guardrails/`). It is an OPTIONAL, human-authored topology file — the
   project's `CLAUDE.md`-analogue for breakdowns — naming repo specifics no stack file can
   infer: the stack, the build-descriptor path, the shared-abstraction project name + its
   consumers, and project-layout notes. **When present, its specifics OVERRIDE/augment the
   stack file's generic guidance** (use the real solution path, the real abstraction project
   name in the pattern-2/3 guardrails). **When absent, proceed with the stack file alone** —
   it is high-value but never required. Expected shape:

   ```markdown
   # guardrails-patterns.md   (repo root or .guardrails/)

   ## Stack
   dotnet

   ## Build descriptor
   PoC/ConformedSources/WorksoftMigrator.slnx

   ## Shared abstractions project
   MigrationAbstractions — consumed by WorksoftMigrator.Desktop and WorksoftMigrator.Cli

   ## Project layout notes
   New UI projects live under PoC/ConformedSources/. Each new .csproj must be registered in
   WorksoftMigrator.slnx and have a <ProjectReference> from its consumer before the solution
   build guardrail is meaningful.
   ```

## Step 1 — Parse the plan into candidate work items

Read the whole plan. Extract numbered steps, deliverable-shaped headings, acceptance
criteria, "done when" language, and dependency words ("after", "requires", "once X
exists"). Build a scratch table:

| item | deliverable artifact(s) | completion evidence available in the plan | hinted deps |

Anything with **no observable deliverable** ("think about performance", "consider
edge cases") is flagged: it either merges into a neighboring task's guardrail or is
reported to the human as non-executable plan content. Never invent a task for it.

**A user-facing UI outcome IS a deliverable — record the UI surface, not just the
backend that would serve it.** When a plan describes something the *user sees or
operates* — "the user sees…", "a page that…", "served to the browser", "wizard
screen", "master/detail view", "tri-state tree", "next/back navigation", "renders…",
"a form/dashboard/grid" — that screen/page/component is a **first-class deliverable in
its own right**, NOT decoration on a backend route. The failure this guards against
(issue #66) is silent: UI language maps onto the nearest *backend* capability (the
route/handler/DTO that would feed the screen), that backend gets decomposed, and the
**UI surface is dropped** — the run goes fully green producing a JSON API with no
human-facing frontend. So for every UI-facing phrase, add a **distinct row** for the
UI artifact (`wizard.html` + its client JS/CSS, or the framework component) ALONGSIDE
any backend row that serves it — never collapse the two into one backend row. The
backend that serves a screen and the screen itself are two deliverables with two
different completion evidences; Step 4's UI-facing doctrine check and Step 5's
UI-implementation insertion act on these rows.

## Step 2 — Size the tasks

A task is right-sized when ALL hold:

1. **One verifiable outcome.** One primary artifact/behavior a guardrail can check.
   If describing the outcome needs "and", consider splitting.
2. **Guardrail-boundary rule (load-bearing):** split exactly where verification
   changes character. "Implement parser + write its tests" splits because the
   test-task's guardrails (the tests build + fail on stubs) differ from the
   parser-task's (tests pass). Conversely "create the file AND register it in the
   index" stays one task if a single guardrail checks both.
3. **One-session rule:** a competent agent finishes it in one focused session
   (≈ ≤ 30–45 min of agent work).
4. **Retry-cheapness:** a failed guardrail re-runs the whole action. If a one-line
   fix would redo an hour of work, the task is too coarse.
5. **TDD default for code deliverables.** When the primary deliverable is code (a
   library, feature, service behavior, or algorithm), the guardrail-boundary rule (rule
   2) almost always fires: a test-author task's "red" guardrail (`build-passes` +
   `tests-fail-on-stubs` for a behavioral type, or `tests-fail-on-current-code` for a
   data model — Step 5's stub-based TDD rule) and the implementation task's guardrail
   (`specific-tests-pass`) are different in character. Default to splitting into two
   consecutive tasks:

   1. `NN-author-tests-<feature>` — writes tests encoding the behavior BEFORE it exists
   2. `NM-implement-<feature>` — makes those tests pass without modifying them

   Collapse to a single task only when (a) tests for this behavior **already exist** in
   the repo, (b) the behavior is too simple to have meaningful unit tests, or (c) the
   deliverable is a **pure data model** (an enum/record/value type with no behavioral stub
   possible — the type declaration IS the implementation, so the TDD "red" has no stub-vs-real
   distinction; Step 5's stub-based TDD rule) — state the reason explicitly in the task
   description or breakdown report. When in doubt, split: the test-author task is cheap and
   its anti-tautology guardrails (`build-passes` + `tests-fail-on-stubs` for a behavioral
   type — Step 5) are the strongest anti-tautology check the skill has.

### Over-size split-check — a CHECK WITH TEETH, not advice (#111)

The right-sizing rules above describe the target; this check ENFORCES it. **Before emitting any
task, run it through the split-trigger. If ANY trigger fires, you MUST split the task and re-run the
triggers on each piece — do NOT emit the over-sized task and "note it."** A milestone-sized chunk that
maps to one task thrashes at run time: every failed guardrail re-runs the whole oversized action, and
it is the single most likely `needs-human` in a run (the exact retry-cheapness anti-pattern).

**Split-trigger — split when ANY holds:**
- **(a) Bundles multiple distinct deliverables.** The description reads "do X **and** Y **and** Z"
  with the conjuncts being separately-verifiable outcomes (add a gate **and** delete three classes
  **and** re-baseline the suite). Each distinct deliverable is its own task. (One outcome that needs
  "and" only to *describe* it — "create the file and register it in the index", checked by a single
  guardrail — is NOT this trigger; that is rule 2's single-guardrail case.)
- **(b) Wide blast radius.** The task creates/deletes/renames many files, or re-baselines many test
  references (a rough line: **deleting ≥3 source files, or touching ≳10 files / test references** in
  one action). A wide-blast task fails the retry-cheapness rule by construction: a one-line guardrail
  miss re-does the entire multi-file change. Split so each task's diff — and therefore its retry — is
  bounded.
- **(c) Maps 1:1 to a design milestone.** A plan milestone / phase / numbered section is NOT a task —
  it is a *bundle* of deliverables. If a candidate task is "implement Milestone M4," decompose it into
  the deliverables inside M4; never size a milestone 1:1 to a task.
- **(d) Retry re-runs expensive work.** Estimate what a single failed guardrail forces to redo. If a
  retry re-runs an hour of refactoring (a multi-deletion, a 100+-ref re-baseline), the task is
  mis-sized by definition. Split so each task's retry is cheap.

**Carry the plan's own feasibility signals into sizing (#111).** When the plan's
feasibility / self-critique / risk section flags a milestone as **heavy, over-packed, or
high-churn** ("~147 test refs", "over-packed", "large blast radius", "risky to do in one pass"),
treat that as a **fired trigger**: the breakdown MUST split that milestone rather than size it 1:1.
This signal already exists in the plan — do not let it die between the plan's risk section and your
task sizing.

**Corrective action when a trigger fires:** decompose the task into the smallest pieces that each
(i) carry one verifiable outcome, (ii) land in one session, and (iii) retry cheaply — scoping each
piece's test re-baseline to that piece. *Worked split:* a task bundling "add the git-required
validation gate + new error codes, delete `CapturedFileStore` + `FileHashCapture` +
`RestoreAncestorCaptures` + two validators, and re-baseline ~147 test refs" fires (a), (b), and (d).
Split it into e.g. (1) add the validation gate + error codes; (2) remove the two validators; (3)
delete the three capture classes + the retry-loop change — each with its test re-baseline scoped to
that piece, so each lands in a session and retries cheaply.

Heuristic: a typical feature plan yields **5–15 tasks**. TDD splitting doubles code
tasks (each code item becomes two tasks); this does not count against the threshold.
Under 3 or over 25 tasks after applying TDD → re-examine, and tell the user why if
it stands. **A count under the floor after splitting over-sized milestones is itself a signal**
that a milestone was sized 1:1 — re-run the split-trigger before settling on a small task count.

### Large/unbounded fan-out → scripted ETL, NOT an agent-per-item loop (#100)

The over-size split-trigger sizes by *deliverable count and blast radius*; this rule sizes by
**iteration cardinality**. When a task's deliverable is **"process N items where N is unknown and
potentially large"** — a web crawl/scrape, a bulk transform over an unknown-size glob, a mass API
fetch, a dataset ETL — the wrong model is an **agent-iterated loop** (one agent turn-budget covering
N fetch+convert+write cycles). Agent turns are the wrong unit for bulk work: a few hundred items blow
any reasonable turn budget, the action hits max-turns and is killed, and the retry hits the same wall
identically — a hard dead-end (`action-failed` → retries fail → `needs-human`) on a task that is
perfectly doable when structured as a script. **Raising `maxTurns` does not fix it; it only moves the
wall.**

**Detection heuristic — flag during sizing when a task fans out over an external or unknown-size set:**
a website / section / sitemap, a recursive glob, an API listing, "every page under…", "all files
matching…", "each record in…". The tell is *cardinality the plan cannot bound at breakdown time*
("8 expected" can turn out to be 409 actual). A retry-cheapness / one-session check on **"could this be
hundreds of items?"** trips the rule.

**When it fires, structure the work as a scripted bulk operation — three moves:**

1. **Scripted-ETL action (the volume happens off the turn budget).** The agent authors and runs **one
   script** that does the N-item work in a single execution (e.g. Playwright + HTML→markdown; a glob
   walk + transform). The agent's turns go to *writing, verifying, and running* the script — NOT to
   iterating items. This is a **`script` action**, not a `.prompt.md` that loops. Guard it with the
   ordinary script archetypes (file-exists on the output dir + command-exit-code / a count check), and
   verify the *recorded output*, not a replay.
2. **Discover-size-first.** When the set size is unknown, **enumerate/count before** committing to an
   approach, so sizing and any curation are calibrated to reality. This is its own cheap probe
   (enumerate the in-scope set, write the count to state or a manifest) and may be a separate upstream
   task feeding the ETL task.
3. **Split bulk-capture from per-item derivation.** Make the cheap, complete, **scripted capture** one
   task (deterministic, fits a session — dump all N items locally), and any **agent derivation/curation**
   a separate, **bounded** task over a *selected subset* — never "derive all N." "Crawl all 409 pages to
   local markdown" (scripted capture) then "curate a high-value committed subset" (bounded agent
   derivation) is the shape, not one agent told to "crawl and curate 409 pages."

The catalogue's scripted-ETL section holds the archetype detail and the decision-tree leaf
(`references/guardrail-catalogue.md` → "Bulk/unbounded fan-out"). Relation to siblings: this is
necessary but distinct from `maxTurns` budgeting (#94 — bulk fan-out does not scale with turns at all)
and from corpus-completeness guardrails (#99 — those *verify* the output; this *structures the task* so
it can be produced at all).

## Step 3 — Determine the DAG (`dependsOn`)

Edge sources, in priority order:
- **(a) artifact dependency** — task B consumes a file/state key task A produces;
- **(b) guardrail dependency** — B's guardrail executes A's artifact (tests, scripts);
- **(c) explicit plan ordering** ("after", "requires").

**Default to the sparsest correct DAG.** Plan prose order alone is NOT an edge —
parallelism is free, false edges serialize the run. Record a one-line justification
per edge (in the task description or the breakdown report). Verify acyclicity.

**(d) transitive COMPILATION dependency — a verified task compiles a test file that references
another task's type (#176).** Edge source (a) covers a task that reads another's FILE; this covers
a task that **compiles** a file referencing another's TYPE. When a task **B**'s verification runs
`dotnet build` / `dotnet test` (filtered or whole-suite), it compiles the **entire test project** —
including `.cs` test files authored by **other** tasks. If an ancestor test-author task **A** wrote a
test file that references a type **produced by an implementation task C**, then B's compilation
depends on C even though B never reads C's file directly — and if C is **not** already in B's
ancestor set, B's working tree lacks `C`'s output and the compile **fails on an error B cannot fix**
(the type lives in C's `writeScope`, not B's). The trapped agent then redefines the missing type in
its OWN scope to make the compile pass, colliding with C's copy at the AI-merge → a duplicate-class
CS0101 (the plan-0009 #176/#175/#174 failure chain). **Rule:** when a task's verification compiles a
test project containing an ancestor test-author task's tests that reference types from another
implementation task, add that implementation task to the verifying task's `dependsOn` — so its output
is present in the working tree and the test project compiles. (Sparsest-DAG caveat still applies:
add the edge only when an ancestor test file actually references the other task's type; do not couple
to every implementation task defensively. The `guardrails-review` "Transitive compilation dependency"
probe flags the case you miss.)

## Step 4 — Select guardrails (read `references/guardrail-catalogue.md` first)

Apply the decision tree per task, using BOTH layers loaded in Step 0: the universal
catalogue for the archetype, and the **stack file** (`references/stacks/<stack>.md`, plus
any `guardrails-patterns.md` topology) for the exact regex/command. Rules that are never
optional:
- 1–4 guardrails per task, **cheapest-first** filename order (`01-exists`,
  `02-builds`, `03-tests`, `04-review`).
- Every guardrail file opens with `# catches: <the wrong implementation it catches>`.
  Can't write the sentence → delete the guardrail.
- Every candidate prompt-judge passes the 4-question demotion gate or is demoted.
  A judge is never a task's only guardrail.
- Deterministic guardrails print ONE actionable failure line to stdout (it becomes
  retry feedback). Write the failure branch as a **multi-line `if` block** — the
  `Write-Output` reason and its `exit 1` each on their own indented line; never collapse
  the body onto the `if` line with `;` (`if (...) { Write-Output "..."; exit 1 }`). That
  reason line is what a human reviews and what the next attempt reads as feedback, so it
  must stand on its own line and be easy to scan. Applies to every archetype, build /
  exit-code checks included.
- **A `tests-pass` guardrail MUST re-emit the failure DETAIL at the END of stdout (#179).**
  The harness feeds back only the **tail** of a failed guardrail's stdout (last ~60 lines).
  Default `dotnet test` prints each failure's assertion/exception text mid-run and ends with
  only `[FAIL] <name>` + a count — so a bare `dotnet test; if ($LASTEXITCODE -ne 0) {…}` puts
  only the test NAMES in the tail and the next attempt sees WHAT failed, not WHY (it then
  retries blind — plan-0009 burned 12 attempts). For any guardrail that asserts tests PASS, use
  the catalogue's capture → emit-full-log → re-emit-failure-lines-at-the-end pattern (catalogue
  → "Failure detail must reach the retry tail"; .NET regex in `stacks/dotnet.md §4.2`). The
  INVERSE TDD-red checks (`tests-fail-on-stubs`, where a non-zero exit is success) do NOT
  re-emit. This is in addition to — not a replacement for — the single actionable reason line.
- "All tests pass" appears ONLY in the terminal `<plan>/guardrails/` folder (the terminal gate).
- **A full build / whole-suite test guardrail in the terminal `<plan>/guardrails/` folder is a
  terminal postcondition → keep it LOCAL (#165).** Do NOT mark `01-solution-builds` /
  `02-all-tests-pass` (the whole-repo build and full test suite) `scope: "integration"`.
  Omit the `scope` key (default `"local"`) so they run ONLY at the terminal gate — once, on the
  merged plan-branch HEAD, AFTER every upstream task has merged.
  That is the correct and ONLY moment a full build / full suite should run. A `scope:
  "integration"` guardrail re-runs at **every** union point (every fan-in / non-FF
  integration), on partial merges where downstream tasks have NOT run yet. In a TDD plan a
  Wave-2 union contains test files that reference types implemented in Wave 3+, so a whole
  build / whole suite FAILS at that intermediate union and the harness rolls the wave back —
  even though every per-task guardrail passed. That is exactly the **#125
  terminal-postcondition-at-integration-scope anti-pattern** (decision test: *"would this
  pass on a partial merge with a downstream task unsettled?"* — a full build/suite would
  NOT). Marking it integration-scoped red-halts a correct run. (Catalogue → "A
  `scope:"integration"` guardrail MUST be UNION-SAFE".)
- **The terminal `<plan>/guardrails/` folder MUST still carry ≥1 `scope: "integration"` UNION-SAFE
  invariant guardrail (GR2028, the re-homed GR2018 content teeth).** GR2028 requires the terminal
  folder to carry at least one real integration-set re-run; when that re-run is a union invariant it is
  the **conditional union invariant**,
  NOT the build/suite. It asserts something true of any valid intermediate union — "every
  produced file present is non-empty and conflict-marker-free", "every contribution PRESENT
  in the union is intact" — so it passes trivially BEFORE a contributing task has run. Its
  content checks MUST be **UNION-SAFE = CONDITIONAL**: `IF contribution X is present, verify
  it's real`, never `REQUIRE X present`. The conditional pattern (the `parallel-hello`
  template; `examples/parallel-hello/.../01-whole-repo-greeting.ps1`):

  ```powershell
  # Union-safe: gate on the artifact being present, then verify it — pass trivially when absent.
  $outDir = Join-Path $ws 'out'
  if (-not (Test-Path $outDir)) { exit 0 }   # nothing produced at this union yet — fine
  foreach ($f in Get-ChildItem -Path $outDir -Filter '*.txt' -File) {
      $content = Get-Content -Raw -Path $f.FullName
      if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {   # line-anchored ours/theirs — false-positive-free (#187)
          Write-Output ("out/" + $f.Name + " contains git conflict markers — the union did not cleanly integrate")
          exit 1
      }
  }
  exit 0
  ```

  **Line-anchor the marker regex (#187).** Match `(?m)^<<<<<<<` / `(?m)^>>>>>>>` — a real conflict
  writes both at column 0 — and DROP a bare `=======` check: the unanchored form false-fires on a
  `====` banner / Markdown setext underline / ASCII table rule and red-halts a correct run.

  A contribution-present check uses the same conditional shape — `if ($content -match
  "<token>") { if ($content -notmatch '<real-construct>') { $failures += "<token> present
  only as comment — construct missing" } }` — so it false-passes (correctly) before the
  contributing task has run, and tightens once that task's hunk lands. The
  overlapping-writeScope union-guardrail (next bullet, #132) IS this integration-scoped
  guardrail — make THAT, not the build/suite, the terminal folder's union invariant satisfying GR2028.
- **Overlapping writeScopes → author a `scope:"integration"` union-guardrail on the shared file
  (#132).** When ≥2 tasks have OVERLAPPING `writeScope`s on a shared file (colliding siblings the
  AI-merge unions), emit one `scope:"integration"` guardrail on the integration / fan-in task
  asserting the shared file's UNION invariant — the merged file still holds every sibling's
  contribution (each distinctive marker present, conflict-marker-free), union-safe (#125), as the
  texttools showcase does with `components-union-verified`. The union re-verify is integration-set-only
  (SSOT §4.3), so a dropped hunk on the shared file is re-verified at the union ONLY by an
  integration-scoped guardrail. Prefer **disjoint** scopes (a collision is usually a plan-shape smell);
  emit the union-guardrail when the overlap is genuine. (Catalogue → overlapping-writeScope
  union-guardrail.)
  - **Shared CODE file both tasks define into → add a DUPLICATE-DEFINITION check (#175).** When the
    overlapping-`writeScope` file is a CODE file and **both** colliding tasks could ADD a
    type/member DEFINITION to it (a `class`/`record`/`interface`/`enum`/method), the union-guardrail's
    conflict-marker + contribution-present checks are NOT enough: a 3-way / AI-merge of two branches
    that each appended the **same** new definition to **different** regions keeps **both** copies with
    **no textual conflict marker**, so the merged file holds a **duplicate definition** (CS0101) that
    only the build catches — the exact #175 trap that red-halted plan-0009's terminal gate. Add a
    **duplicate-definition count check** to the same `scope:"integration"` union-guardrail: count
    occurrences of each definition both siblings could add and fail when **>1**, naming the AI-merge
    duplicate. Keep it **union-safe/conditional** (#165) — place it inside the existing file-present
    gate so it passes trivially at a union where the file hasn't landed. The .NET realization is
    `[regex]::Matches($content,'class\s+<Name>').Count -gt 1` (`stacks/dotnet.md §19`). The harness can
    only *attribute* this collision at the gate (name the colliding `writeScope` pairs, SSOT §3.3); the
    duplicate-definition check is the authoring-side PREVENTION. (Catalogue → overlapping-writeScope
    union-guardrail, duplicate-definition sub-check.)
  - **On removing a dependency edge, re-evaluate the union guardrail's expected contributions (#159).**
    When a regeneration (Step 8) or a hand-edit **removes a dependency edge** (task B no longer
    `dependsOn` task A — e.g. a mode deferred, a producer demoted to a disconnected leaf), re-examine
    **every** `scope:"integration"` union guardrail on the fan-in task. If any of them still checks for
    a contribution token that **only task A could produce** and A is no longer in the fan-in task's
    **ancestor set** (no directed path A → fan-in), the guardrail has gone stale: it now implicitly
    requires a disconnected task to stay in the plan, and if A is later removed the integration gate
    fails spuriously with a confusing "shared file is missing `<token>`" — a stale-guardrail bug, not
    a merge failure. Resolve it one of two ways: **(a)** add an alternative DAG path from A to the
    fan-in task (make the dependency the guardrail relies on explicit), or **(b)** remove the now-stale
    contribution check for A's token from the union guardrail. This is the authoring-side complement to
    the `guardrails-review` "Union guardrail ancestor staleness" probe (#159).
- **Two-scope preflights/guardrails — the four-folder model REPLACES the `integrationGate: true`
  task + no-op ROOT/END scaffolding (deliverable 9, SSOT §1/§3.3).** On a harness version whose
  loader understands the four folders, do NOT emit an `integrationGate: true` sink task — a plan
  still declaring one gets a **hard validation error (GR2029)**, no coexistence window. Emit these
  four first-class folders instead:
  - **`<plan>/preflights/`** — the plan-root "Full Flight Checks", a sibling of `tasks/`,
    `guardrails.json`, `state/`. Evaluated ONCE, BEFORE the Scheduler builds waves, against the
    starting repo. This is where the **#181 positive baseline (REFRAMED, not replaced)** now
    lives: instead of a no-op ROOT task + `--filter`-scoped guardrail, emit a **positive check
    file** in this folder (e.g. `01-all-repo-tests-green`) asserting the currently-green
    precondition. Also the home for a **negative** assert-absent baseline (a one-shot,
    plan-level-only check that a not-yet-introduced artifact is genuinely absent at the start) —
    this cross-references the existing `tests-fail-on-current-code`/`tests-fail-on-stubs`
    anti-tautology archetype rather than forking a new one. **Remove the no-op ROOT/END task
    scaffolding and its #174/#182 short-circuit dependence from the baseline story** (the
    short-circuit remains a general §7 rule for any REAL task that no-ops elsewhere, untouched —
    it simply no longer participates in the baseline/preflight story), and **remove any simulated
    "precondition" scope value** (no third scope value exists under this model — only `"local"`
    (default) and `"integration"`).
  - **`<plan>/guardrails/`** — the plan-root "Terminal Gate", also a plan-root sibling. Evaluated
    ONCE, at run end, on the merged plan-branch HEAD. **The re-homed GR2018 authoring rule:** a
    multi-leaf/fan-in plan's `<plan>/guardrails/` folder MUST carry **≥1 real integration-set
    re-run** — a genuine whole-repo build/test/suite invocation, or a union invariant — NOT a
    tautological `exit 0` file; **content teeth survive the move from task to folder**
    (`validate` enforces this as **GR2028** on a multi-leaf/fan-in plan). `scope: "integration"`
    itself is **unchanged** — it remains the per-union tag driving the §4.3 per-union re-verify;
    only the terminal-sink TASK kind was retired.
  - **`tasks/<id>/preflights/`** — task-level, a sibling of the existing `tasks/<id>/guardrails/`.
    JIT dependency-delivery: evaluated in the consumer's own segment worktree at `taskBase`,
    BEFORE its attempt loop. Emit one whenever a `dependsOn` edge delivers a type/route/symbol/
    artifact a downstream task needs inside its own segment — confirming the producer's
    contribution actually landed before the attempt loop spends a turn building against
    possibly-absent bytes. Polarity here is **positive-monotone-safe** (never negative — a
    task-level check runs per-attempt against a segment that only grows, so a negative
    "not yet present" assertion would flip false as soon as an unrelated file lands).
  - `tasks/<id>/guardrails/` — the existing per-task postcondition folder, unchanged.

  All four folders share **one** guardrail-file parser/grammar with the existing
  `tasks/<id>/guardrails/` shape (`NN-name.ps1`/`.sh`/`.py` + optional `.json` sidecar, or
  `NN-name.prompt.md`; `catches:` comment required; ordinal sort) — they differ only in WHERE
  they live and WHEN they run.

  > **Superseded — the rule below describes the RETIRED `integrationGate: true` task mechanism**
  > (now a hard validation error, GR2029). It applies only when authoring for a harness version
  > that predates the four-folder loader, or a plan's own named, documented bootstrap exemption —
  > never silently. For a plan targeting a current harness, use the `<plan>/guardrails/` folder
  > above instead.
  >
  > A terminal integration task must declare `integrationGate: true` in its `task.json` — it
  > marks the terminal whole-repo integration gate, the final soundness boundary run once on
  > the fully merged plan-branch HEAD (SSOT §3.3, pre-four-folder). Validation enforced this: a
  > plan with ≥2 leaf tasks or any fan-in had to declare **exactly one** `integrationGate: true`
  > sink (**GR2017**, retired), and that sink had to carry **at least one** `scope: "integration"`
  > guardrail (**GR2018**, re-homed onto the folder above) — an empty gate verified nothing.

**Route through these doctrine checks every task (the decision tree's newer leaves):**
- **State output** — does this task's action write a state key (to `GUARDRAILS_STATE_OUT`)
  that a downstream task reads (via `GUARDRAILS_STATE_IN`)? Add the fragment-key-present
  guardrail (catalogue → state-output leaf): read `GUARDRAILS_STATE_FRAGMENT`, parse JSON,
  assert the key non-null + non-empty (+ allowed-set if a downstream task branches on it).
  **A task's action may write state ONLY under its own task FOLDER NAME as the single top-level
  key** — single-writer-per-key is enforced (SSOT §6.2). The key is the **directory name** the
  `task.json` lives in (e.g. `04-author-tests-tcapi-local`), **NOT** the task's `stableId` (an
  internal regeneration token — the harness rejects a fragment keyed by the `stableId` as a
  foreign/unowned key). Writing under another task's folder name or any shared key likewise
  rejects the fragment and fails the attempt every retry. The generated prompt must state this
  rule with a concrete `{ "<folder-name>": { … } }` example (Step 6 authoring rule).
- **Build-descriptor registration** — does the task add a module/project to a build
  descriptor (a `.csproj` to a `.slnx`)? Add the stack file's registration guardrail on the
  DESCRIPTOR, not just the new file (`stacks/dotnet.md §1`). A descriptor build passes with
  an unregistered project — file-exists + build-passes do NOT cover this.
- **Cross-module reference** — does this task create an abstraction a later task must
  consume? Add the stack file's reference-chain guardrail on the CONSUMER's project file
  (`stacks/dotnet.md §2`). Builds pass independently, so without this an agent can define a
  local copy of the interface and pass.
- **Composition-root wiring (#120 — the recurring lesson)** — does the plan add a **component
  that must be CONSTRUCTED and INJECTED at a production composition root or entry point** to do
  anything (an `IFoo` + `FooImpl` pair injected into a factory / `Program.cs` / DI registration /
  dispatch site / `RunCommand`)? The per-component tasks author-test + implement `FooImpl` against
  an **injected constructor seam** — each green — and the terminal whole-suite build + test passes,
  yet **nothing constructs `FooImpl` and hands it to the production assembler**, so the real entry
  point never takes the new branch and the feature is **inert** (reachable only from xUnit, which
  injects the seam itself). This is the highest-impact false-green the skill emits — it recurred 3×
  in one plan (engine, AI-merge, triage — all built, all dead from the CLI at the `SchedulerFactory`
  composition root). Two artifacts close it, generated in Step 5: an explicit **wiring task** (a
  named deliverable: construct `FooImpl` and inject it into the assembler, with a DAG sink depending
  on it) and a **composition-root guardrail** asserting the component is ACTUALLY wired in
  production — drive the REAL assembler and assert observable output the wired-only feature produces
  (strongest), or reflect on the constructed object for the non-null collaborator WITH a contrast
  case (the `Factory_Wires*` shape; catalogue → composition-root section, `stacks/dotnet.md §10`).
  The guardrail MUST NOT inject the seam itself, and the terminal whole-suite gate does NOT cover
  this (it is necessary but not sufficient). The signals (any one):
  - the plan introduces an `IFoo` + `FooImpl` pair (heuristic: every such pair needs a "wire
    `FooImpl` into the composition root" deliverable);
  - the component is reachable only via a constructor/DI seam the unit tests inject themselves;
  - the plan names a factory / `Program.cs` / `Startup` / DI registration / dispatch site /
    `RunCommand` that must construct, branch on, or inject the new component;
  - the feature activates only under a mode/flag (e.g. `maxParallelism > 1`) the production dispatch
    must honour — "machinery reachable only from xUnit" is the tell.

  (This is a sibling of the executable-entry-point-wiring check below but at the assembly layer:
  that one greps `Program.cs` + smoke-tests a route for a *server serving over a port*; this one
  asserts a *factory/container constructs and injects an internal collaborator*. A plan can need
  both — wire the entry point to the launcher AND wire a collaborator into the factory.)
- **Dispatch / factory pairing (#158 — is the RIGHT impl wired to the RIGHT mode?)** — does this task
  **dispatch from an enum / discriminated value to one of ≥2 concrete implementations**
  (`ImportMode.TcApiLocal → new TcApiLocalImporter()`, a `switch`/`if` selecting a handler per mode)?
  The build passes with the pairings **swapped** (either concrete type satisfies the interface in either
  branch), and if the dispatch tests inject a **substituted fake** (`RecordingImporter` / `FakeHandler`
  via DI) they assert only that *an* importer was called, never **which concrete type** — so an inverted
  wiring (Mode B → the wrong importer) ships fully green. A bare keyword check that all enum values AND
  all type names appear *somewhere* in the file does NOT catch it (all are present regardless of
  pairing). Add **one proximity check per pairing** (catalogue → "Dispatch / factory wiring";
  `stacks/dotnet.md §10d`): assert `<EnumValue>` sits within a bounded window (`[\s\S]{0,300}`,
  multiline-dotall, both orders) of `<ConcreteType>` in the dispatch file, scoped to that one file.
  **Decision gate:** if the dispatch tests already assert the concrete TYPE NAME
  (`Assert.IsType<TcApiLocalImporter>` on the resolved object), the test catches the swap — OMIT the
  proximity check and say so in the covering guardrail's `# catches:` comment. Distinct from #120
  composition-root wiring (which asks whether the impl is constructed/injected at all); this asks
  whether each mode got the right one. Fire only when **both** hold: ≥2 concrete impls selected by an
  enum, AND the dispatch tests use seam-injection (not type assertions).
- **Executable entry-point wiring** — does the plan describe a **server or CLI executable
  outcome** (signals below)? Component tasks (scaffold, handler, routes) each compile and
  unit-test green, and the terminal whole-solution build passes — yet *nothing wires the
  entry point to the handler*, so the binary builds and serves nothing. Unit tests cannot
  catch a missing `new Launcher().StartAsync()`. Two artifacts close this, generated in
  Step 5: a **wiring task** guarded by a static grep that the entry point references the
  launcher (catalogue → entry-point-wiring; `stacks/dotnet.md §7`), and after it a **live
  smoke-test task** that actually starts the binary, hits a route, and asserts a response
  (archetype #7 port/endpoint-answers; the start/poll/assert/teardown script in
  `stacks/dotnet.md §8`). The signals (any one):
  - plan phrases: "CLI entrypoint", "starts a server", "serves … to the browser",
    "loopback HTTP", "prints a URL", "listens on", "health endpoint";
  - a `.csproj` using `Microsoft.NET.Sdk.Web` or declaring `<OutputType>Exe</OutputType>`;
  - an explicit smoke-test statement in the plan (see Step 5's authoring note).

  This catches "the exe does what the plan says" vs merely "the code compiles" — the one
  gap a green build and passing unit tests leave open. (Scope: starting-and-serving ONLY;
  whether the *described UI was actually built and is served* is the next doctrine check —
  the two compose: this one proves the exe serves *something*, the UI-facing check proves
  the *something* is the UI the plan described.)
- **UI-facing deliverable** — does the plan describe a **user-facing screen/page/visual
  component served to the browser** (the Step 1 UI signals: "the user sees…", "a page
  that…", "served to the browser", "wizard screen", "master/detail view", "tri-state
  tree", "renders…", a form/dashboard/grid)? The component tasks decompose to backend
  routes/handlers/DTOs and unit tests — each green — and (with the entry-point-wiring check
  above) the binary even starts and serves. Yet **no task built the UI itself**: there is no
  HTML page, stylesheet, client JS, or `wwwroot`, and the served root returns JSON or a
  placeholder. A green build + passing unit tests + a 200 from `/` cannot catch a missing
  frontend — the route answers, it just answers with no UI. Two artifacts close this,
  generated in Step 5: a **UI-implementation task** per described screen (produces the
  HTML/JS/CSS or framework component that renders it and binds to the backend contract) and
  a pair of **UI-presence guardrails** — (a) a static asset-exists check that the page/asset
  file is present (catalogue → UI-presence; `stacks/dotnet.md §9`), and (b) a **served-markup
  assertion that EXTENDS the §8 smoke-test** (the same start/poll/teardown lifecycle, with an
  added assertion that the response body contains a known UI element/string from the page —
  not merely HTTP 200). Both are deterministic (asset grep; served-markup contains a known
  string) — never a prompt-judge "does this look like a good UI"; visual quality is out of
  scope, *presence and wiring of the described UI* is the deliverable. The exit-criteria
  self-review in Step 7 is the backstop: a plan promising a frontend that decomposed to zero
  UI tasks fails its own review.
- **Positive-effect / non-hollow output assertion** (#73) — does this task's action claim a
  **non-empty quantity of output** (a "how many items were processed" result: migration
  moved-count, items written, rows produced, entities created)? Typically the terminal/
  integration e2e task. A keyword-presence regex on the assertion
  (`Assert.*\([^)]*(Moved|Written|Count|Entities)`), a bare `Assert.NotNull(...)`, or a
  non-error `exit 0` is **hollow** — it passes on `Assert.Equal(0, writer.Count)`, certifying
  a no-op (a migration that moved zero entities goes green). Emit the **positivity** check
  instead: require a strictly positive value
  (`(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)`), or better, read the
  runner-recorded count / state key and assert `> 0`. Catalogue → positive-effect / non-hollow
  assertion.
- **Negative assertion — an EXCLUDED scenario must be verified ABSENT (#176)** — does this task's
  action prompt **explicitly exclude** a scenario/keyword the deliverable must NOT contain ("Mode C /
  `CommanderRest` is wizard-blocked — do NOT include it in the dispatch tests"; "the importer must NOT
  call `X` directly")? The positive `covers-key-behaviors` guardrail only checks that the **kept**
  scenarios are PRESENT — it says nothing about the excluded one, so the agent can include the removed
  scenario **undetected** (which is how the excluded `CommanderRest` slipped into plan-0009's dispatch
  tests and fed the #176 compile trap). Emit a **negative-assertion guardrail** — a fail-on-present
  check that the excluded keyword is ABSENT: `if ($content -match "CommanderRest") { Write-Output "…";
  exit 1 }` (scoped to the one file the task owns). It is a legitimate, deterministic archetype, the
  mirror of `covers-key-behaviors`; **pair it with** the positive coverage check (catalogue → negative
  assertion; `stacks/dotnet.md §20`). Note `guardrails validate`'s **GR2026 stays silent** on this
  guardrail's keyword — correctly, post-#177: GR2026 flags only POSITIVE require-present coverage tokens
  (SSOT §4.4), so a fail-on-present keyword intentionally absent from the prompt is NOT a stale-coverage
  warning. Do not omit or weaken the negative assertion to silence a (now non-existent) GR2026 warning.
- **A cross-cutting-output task OWNS the re-baseline of every golden it feeds (#193)** — the runtime
  mirror of the transitive-compilation edge (Step 3d, #176). When a task changes a **cross-cutting
  output shape** — a renderer, a hash, a serializer, a formatter, a message/wire schema, any code
  whose bytes flow into a **pinned literal / golden file / snapshot / approved output** that an
  EXISTING test asserts against — that task's `tests-pass` guardrail must not be allowed to sweep in a
  pre-existing golden the task cannot own. Two coupled authoring moves:
  1. **Scope the `tests-pass` `--filter` to THIS task's own tests** (a class-name / trait filter, not
     a broad `FullyQualifiedName~<substring>` that also matches pre-existing golden/snapshot tests) —
     the same "filter to THIS task's tests" rule as archetype #4, sharpened: a broad substring filter
     that pulls in a pre-existing golden test whose fixture this task's change invalidates traps the
     task on a test it can't edit (its `writeScope` excludes the fixture → write-scope check red-halts
     the fix → `needsHuman` loop).
  2. **If the change genuinely re-bakes a shared golden, OWN the re-baseline.** Widen this task's
     `writeScope` to include the affected golden fixture(s) + their pinned test, so regenerating them
     is in-scope; OR, when the re-baseline is large / distinct enough to be its own deliverable (Step
     2 over-size trigger — a 100+-golden re-bake), **insert a dedicated re-baseline task** (an ancestor
     this task `dependsOn`) that owns and regenerates every golden the cross-cutting change feeds. Do
     NOT leave a golden orphaned — owned by no task's `writeScope` yet asserted by a test the change
     breaks. (The `guardrails-review` "Orphaned golden swept in by a broad `tests-pass` `--filter`"
     probe, §2, flags the case you miss; catalogue → orphaned-golden / broad-filter trap.)
- **Structural impl / keyword match** — any "implements/extends/declares" check uses the
  stack file's declaration regex (`stacks/dotnet.md §3`), never a bare type-name grep. A
  property-declaration check must be **accessor-order-insensitive** (#112) — key on the
  declaration up to the brace (`public\s+TYPE\s+NAME\s*\{`), never a fixed leading `\{\s*get`,
  which false-passes on `{ init; get; }` (catalogue → structural-vs-keyword; `stacks/dotnet.md §3`).
- **Grep scope** — every file-content guardrail is scoped to the one file this task owns
  (catalogue → grep-scope contamination anti-pattern; `.NET` traps in `stacks/dotnet.md §5`).
- **Test-author needs a production testability seam (#84)** — while routing a test-author task,
  check **each behavior**: does expressing it as a test that can eventually PASS require a
  production-code **injection seam** that does not exist yet (a DI constructor overload, a factory
  delegate, an injectable interface, a fixture source)? The tell: the behavior injects a fake/double
  (`RecordingX`, `FakeX`, `InMemoryX`, a fixture source) into a type currently constructed only via a
  production constructor with no injection point. If yes, insert an **upstream production-seam task**
  (Step 5's #84 bullet) the test-author task `dependsOn` — do NOT let the test-author task invent the
  seam or rely on its `needsHuman` escape hatch. Distinct from the compile-coupled-DTO case (where the
  missing symbol is a type the *test* constructs) and from composition-root wiring #120 (which injects
  the *real* impl in production); the seam only opens the injection point so tests can supply a double.
- **Positive baseline (preflight) — a BROWNFIELD plan needs a green START before the DAG runs (#181).**
  This is the general **positive-baseline / preflight** archetype: a plan-level Full Flight Check that
  asserts a positive precondition ALREADY holding on the starting state ("never build on red"). Under the
  four-folder model it is a **positive check FILE in the plan-root `<plan>/preflights/` folder** (a
  sibling of `tasks/`, evaluated ONCE before the DAG against the starting repo) — NOT a no-op ROOT task.
  The **existing-area-tests-green** baseline is the canonical worked instance and the ONLY one the skill
  emits today; the same shape extends to other positive baselines (build-green, endpoint-up) by the same
  preflight-file pattern, none emitted yet. Is this a brownfield plan (Step 0 set `$baselineArea` ≠ none —
  it modifies project(s) that already have existing tests)? Before any inserted `author-tests` task adds
  its intentionally-FAILING new tests, and before any implementation task runs, the EXISTING unit tests in
  the touched area must pass on the CURRENT code. Without a green start, a work task's `tests-pass`
  guardrail can fail from PRE-EXISTING breakage (misattributed to the task → wasted retries → late
  `needsHuman`), and a new test's "red" is ambiguous (red-because-missing vs red-because-already-broken).
  When it fires (brownfield AND the worth-it gate passes), emit the **positive preflight check** in Step 5
  (`<plan>/preflights/01-baseline-<area>-tests-green.ps1`), one per touched area.
  - **Scope via `--filter` to the CURRENTLY-GREEN existing tests of the touched area — NEVER the whole
    suite/project.** Load-bearing: a whole-project `dotnet test` in the preflight hits the **#165/#176
    compile-coupling trap** (a mid-TDD project does not compile — its test project references types
    later implementation tasks have not produced yet), manufacturing a FALSE RED no work task can fix.
    The rule: the baseline targets the existing, currently-passing tests of the touched area ONLY.
  - **One baseline per AREA, deduped** — one preflight file per distinct touched test project, each scoped
    to its area, NOT a single global whole-suite preflight.
  - **The worth-it gate (a check with teeth) — emit ONLY when ALL hold:** the target pre-exists; the plan
    MODIFIES not creates it; the check is deterministic + cheap (a bounded, filtered command — a
    filtered `dotnet test` is fine; no live-service boot or network poll, which flakes); strictly narrower
    than the terminal `<plan>/guardrails/` gate; ≥2 work tasks build on the area; deduped per area.
    **Under-fire when unsure** — a missed baseline is just the status quo, a false baseline halts a
    correct plan before the DAG.
  - **Greenfield (`$baselineArea = none`) or worth-it gate fails → SKIP it and state why in the report**
    (nothing to baseline). Distinct from the terminal gate: the baseline preflight is a green START on
    EXISTING tests, evaluated once BEFORE the DAG; the terminal `<plan>/guardrails/` folder is a green END
    on the merged HEAD — complementary, state both. A RED baseline preflight halts the run before the DAG
    (the general Full-Flight-Check semantics), and #179 (re-emit form) makes its WHY reach the feedback.
    The negative "not yet present" baseline is NOT a new archetype — it already IS
    `tests-fail-on-current-code`/`tests-fail-on-stubs` (cross-reference, don't fork), and when emitted at
    plan level it is likewise a `<plan>/preflights/` check (assert-absent, plan-level-only). (Catalogue →
    "Baseline-green / start-from-green (preflight)"; the .NET realization is `stacks/dotnet.md §21`.)

## Step 5 — Insert guardrail-enabling tasks (the generative step)

For every selected guardrail whose precondition doesn't exist yet, generate the
upstream task that creates it:

- **Brownfield plan (Step 0 set `$baselineArea` ≠ none) → emit a positive-baseline (preflight) CHECK
  in `<plan>/preflights/` per touched area (#181).** "Never build on red": establish that the EXISTING
  tests in the touched area pass on the CURRENT code BEFORE the DAG runs. This is the general
  positive-baseline/preflight shape (a plan-level Full Flight Check evaluated once, before the DAG,
  against the starting repo — one cheap deterministic positive-precondition guardrail); the
  existing-area-tests-green instance below is the ONLY one emitted today, but the shape extends to other
  positive baselines (build-green, endpoint-up) unchanged. Emit
  **`<plan>/preflights/01-baseline-<area>-tests-green.ps1`** (use the real area name, e.g.
  `01-baseline-inventory-tests-green`; the plan-level `<plan>/preflights/` folder is a sibling of
  `tasks/`). This REPLACES the retired no-op ROOT task model — do NOT emit a `00-baseline-*` task with
  a `dependsOn: []` no-op action; the preflight folder runs before the DAG with no task, no edges:
  - **First, run the worth-it gate (a check with teeth) — emit ONLY when ALL hold:** the target
    pre-exists; the plan MODIFIES not creates it; the check is deterministic + cheap (a bounded,
    filtered command — a filtered `dotnet test` is fine; no live-service boot or network poll); strictly
    narrower than the terminal `<plan>/guardrails/` gate; ≥2 work tasks build on the area; deduped per
    area. **Under-fire when unsure** — a missed baseline is just the status quo (work tasks attribute
    their own failures the slow way); a false baseline halts a correct plan before the DAG. If the gate
    fails, SKIP and say why in the report.
  - **It is a guardrail-shaped preflight FILE, not a task** — a `.ps1`/`.sh`/`.py` file in
    `<plan>/preflights/` (same parser as `tasks/<id>/guardrails/`), opening with `# catches:`, that runs
    the check and exits 0/non-zero. **There is no action to make a no-op of** — the preflight folder is
    evaluated by the pre-DAG phase directly, so the retired "TRUE no-op `exit 0` action" scaffolding and
    its #174/#182 short-circuit dependence are GONE from the baseline story (a RED preflight simply halts
    the run before scheduling any task). The file IS the verification.
  - **The check: the EXISTING area tests PASS on the current code, scoped via `--filter`.** Run the
    EXISTING unit test project(s) covering the projects the plan modifies — `$baselineArea` from Step 0
    — and assert they ALL PASS (exit 0). **Scope to the CURRENTLY-GREEN existing tests of the touched
    area via `--filter` — NEVER the whole suite/project.** This is load-bearing: a whole-project
    `dotnet test` in the preflight hits the **#165/#176 compile-coupling trap** — a mid-TDD project does
    not compile (its test project references types later implementation tasks have not produced yet), so
    a whole-project test manufactures a FALSE RED no work task can fix, dead-ending the run. The rule:
    target the existing, currently-passing tests of the touched area ONLY. Keep it bounded — a too-wide
    scope also re-imports unrelated flakiness into the pre-DAG phase.
  - **One baseline per AREA, deduped.** Emit one preflight file per distinct touched test project, each
    scoped to its area — NOT a single global whole-repo preflight. Two independent touched test projects
    → two area preflight files; one area → one. Never collapse N areas into one whole-suite preflight,
    never two for the same area.
  - **It runs BEFORE the DAG — no `dependsOn`, no edges.** The preflight folder is evaluated once against
    the starting repo before the Scheduler builds any wave, so every task in the plan is implicitly gated
    on it — you do NOT wire work tasks to it (the retired model made every area work task
    `dependsOn` a no-op root; that scaffolding is gone). Acyclicity (Step 3) is unaffected — a preflight
    file is not a DAG node.
  - **Scope = EXISTING tests ONLY (the load-bearing constraint).** The preflight asserts the PRE-PLAN
    tests pass. It runs on the STARTING workspace state, BEFORE any inserted `author-tests` task adds its
    intentionally-FAILING new tests. So it must target the **existing** test project(s)/area and must NOT
    accidentally run (and fail on) the about-to-be-authored red tests. The pre-DAG phase evaluates it
    against the starting bytes (no new tests yet), which makes this natural; if `$baselineArea` is a whole
    test project that a later `author-tests` task will ALSO add failing tests into, prefer a `--filter`
    (or category) that selects only the pre-existing tests, so the baseline can never go red on tests that
    don't exist yet.
  - **The PASS check is a tests-pass archetype → it MUST use the #179 failure-detail-re-emit
    pattern** (capture → emit full log → re-emit failure-signal lines at the END), so a RED baseline's
    WHY (the failing assertion/exception) reaches the halt feedback, not just `[FAIL] <name>`. The
    .NET realization is `stacks/dotnet.md §21` (it reuses §4.2's re-emit form).
  - **A RED baseline preflight halts the run BEFORE the DAG.** A failing pre-DAG preflight stops the run
    before any task is scheduled (the general Full-Flight-Check semantics) — no retry budget is burned on
    a no-op, because there is no task. Make the check's final actionable line say so plainly, e.g. *"the
    area's existing tests are already failing on the starting code — fix the pre-existing breakage before
    this plan builds on it"* — that fast, actionable halt IS the correct outcome.
  - **The negative "not yet present" baseline is NOT a new archetype — cross-reference, don't fork.** The
    mirror ("a precondition that should be ABSENT is genuinely absent at the start") already IS
    `tests-fail-on-current-code`/`tests-fail-on-stubs` (and the #120 wired/not-wired contrast); reach for
    those. When emitted at plan level it is likewise a `<plan>/preflights/` check (an assert-absent,
    plan-level-only one-shot) — do not author a parallel "negative preflight" task.
  - **Greenfield → DO NOT emit it.** When `$baselineArea = none` (a new project / no existing tests in
    the touched area) there is nothing to baseline. SKIP the preflight and state the reason in the Step 7
    report. A vacuous baseline (running zero tests, or `dotnet test` over a project with no tests, which
    trivially "passes") is worse than none — it certifies nothing while looking like a gate.
- Code task and tests do not yet exist → insert `NN-author-tests-<feature>` BEFORE the
  implementation task (the TDD default in Step 2 means this fires for most code tasks).
  Three things follow automatically:

  **Test-author task guardrails — the "red" must COMPILE and FAIL, not just exit non-zero
  (#155).** A guardrail that accepts ANY non-zero `dotnet test` exit as the TDD "red" is
  gameable: a test file that does **not compile** exits non-zero identically to one that
  compiles and fails, so garbage passes — and the implementation task (whose `writeScope`
  excludes the test file) can't fix the compile error, dead-ending the run at `needsHuman`.
  True TDD red = the tests **compile and fail**. The guardrail form splits on the **type
  under test** (catalogue → "Stub-based TDD" is the SSOT; `stacks/dotnet.md §4.1`):

  - **Behavioral type (a class with methods/logic) → the test-author task ALSO writes the
    minimal STUBS.** The task produces two artifacts: the test file AND the minimal skeleton
    stubs the tests need to COMPILE (interface decls / classes whose members throw
    `NotImplementedException` or return `default`). Its guardrails are the TWO-guardrail pair,
    cheapest-first: **`build-passes`** (archetype #3 — with the stubs the test project compiles,
    so garbage fails HERE unambiguously) then **`tests-fail-on-stubs`** (the #8 form — the build
    being green means a non-zero `dotnet test` now unambiguously means the tests **ran and
    FAILED** against the throwing stubs = TDD red). The implementation task fills real logic over
    the stubs (its scope TARGETS them; see below).
  - **Data model (enum/record/value type — no behavioral stub possible) → COLLAPSE by default.**
    The type declaration IS the implementation, so there is no stub-vs-real distinction. Default
    to a single task (define the type + assert `tests-pass`) and **state the reason explicitly**:
    "data model — no behavioral stub possible". If you keep the split, note the anti-tautology is
    weaker, keep `tests-fail-on-current-code` (the test references the not-yet-existing type, so a
    compile failure IS the red — omit a separate `tests-build`, which would fail at the same
    moment), and **strengthen `covers-key-behaviors` STRUCTURALLY** — assert a real
    `[Fact]`/`[Theory]` attribute is present (`stacks/dotnet.md §17.1`), not just that the
    enum-value tokens appear (a comment satisfies a bare keyword grep).
  - **Mixed task (data + behavioral) → lean BEHAVIORAL.** Stub the behavioral parts so the whole
    test file compiles, and use the `build-passes` + `tests-fail-on-stubs` pair; the data-model
    members come along inside the same compiling file.

  **`writeScope` test-exclusion — the deterministic TDD test-protection (SSOT §3.4).**
  Tests are protected by (i) physical worktree isolation and (ii) the harness's
  **write-scope check**: a deterministic, read-only `git diff` membership test that runs
  after the action and before the task's own guardrails. It asserts every path the task's
  diff adds/modifies/deletes/renames is inside the task's declared `writeScope`; an
  out-of-scope edit is a guardrail-class failure (retry with feedback naming the offending
  paths, eventual `needs-human`). The check **never reverts** the in-scope work, and it
  **writes nothing** — it only inspects the diff. Set the two scopes so the implementation
  cannot author the tests:

  - **Test-author `task.json`: declare a `writeScope` covering the test file(s) AND, for a
    behavioral type, the STUB file(s) it authors (#155).** List each test file and each stub
    file (or their directories) workspace-relative — the surface this task is permitted to
    write. For a behavioral type the test-author task writes both the test and the minimal
    `NotImplementedException` stubs the tests compile against, so BOTH belong in scope:

    ```jsonc
    {
      "description": "Author failing tests + minimal stubs for <feature>",
      "dependsOn": ["..."],
      "stableId": "…",
      "writeScope": ["tests/MyProject/MyFeatureTests.cs", "src/MyProject/MyFeature.cs"]
    }
    ```

    (For a data-model task with no stub, the scope is just the test file, as before.)

  - **Implementation `task.json`: declare a `writeScope` that EXCLUDES the test file but
    TARGETS the stub file(s) (#155).** Scope it to the implementation surface (e.g.
    `src/MyProject/`, which COVERS the stub the test-author created) and do NOT list the test
    file. The implementation fills real logic over the skeleton stubs; the write-scope check
    then deterministically enforces "the implementation may not write the tests" — an edit to
    a test file falls outside the implementation's scope and fails the check. If a stub lives
    OUTSIDE the implementation's directory surface, list that stub file explicitly so the impl
    may overwrite it. This is the **replacement** for the removed
    `captureHashes`/`tests-untouched`/`restoreOnRetry` triad; no hashing, no restore, no
    downstream `tests-untouched` guardrail.

    ```jsonc
    {
      "description": "Implement <feature> so the tests pass (fill logic over the stubs)",
      "dependsOn": ["NN-author-tests-<feature>"],
      "stableId": "…",
      "writeScope": ["src/MyProject/"]
    }
    ```

  **OMIT `writeScope` for genuinely repo-wide tasks; NEVER emit a vacuous `**`.** `writeScope`
  is the off-switch when absent — a task you cannot confidently scope (a sweeping
  cross-cutting change, a terminal whole-suite gate) omits the field and is reported as a
  broad surface, never given a vacuous `**` or a bare top-level dir. `validate` rejects a
  scope that escapes the workspace (**GR2019**, error) and **warns** on a vacuous/over-broad
  scope (**GR2020**) — so emit a real surface or none.

  **Action prompt for both tasks.** The declared scope is injected into the action prompt as
  advisory context, but the harness ALSO enforces it mechanically — so every test-author prompt
  must carry a **Scope boundary (harness-enforced)** paragraph (#154; Step 6 has the authoring
  rule and exact shape). The test-author `## Task` section must tell the agent: (a) the exact
  test file path(s), and — for a behavioral type — the exact STUB file path(s) to create with
  `NotImplementedException` skeletons so the test project COMPILES (#155), plus any category/trait
  convention the repo uses; (b) the tests MUST COMPILE and FAIL against the stubs — failing is
  intentional, NOT compiling is a mistake to fix; (c) do NOT implement the behavior — write the
  tests and only the minimal throwing stubs. The implementation `## Task` must say plainly: **fill
  real logic over the stub file(s); do NOT edit the authored tests; make them pass by fixing the
  implementation; if the authored tests are genuinely wrong or incompatible, emit
  `{"needsHuman": "<why>"}` rather than changing them** — an out-of-scope edit to a test file fails
  the write-scope check and burns a retry. Neither task needs to compute or write any hash. See
  `references/example-breakdown.md` for the complete worked `action.prompt.md` (including the Scope
  boundary paragraph and the stub file).
- **A test-author behavior needs a production-code testability SEAM that doesn't exist yet →
  insert an upstream production-seam task (#84).** Distinct from the compile-coupled-DTO case
  above: there the missing symbol is a **type the test constructs**, so forcing the whole test
  file red via a compile failure is correct. The seam case is different — only **one behavior of
  several** needs an injection point (a DI constructor overload, a factory delegate, an injectable
  interface, a fixture source) for that behavior to be **expressible as a test that can eventually
  PASS**. The other behaviors are runtime-testable against the existing surface and must keep
  compiling and failing as their own clean red; folding the seam into the test file (or vaguely
  gesturing at it from the implementation task) leaves the test-author task unable to verify its own
  behavior will ever go green — so it correctly halts `needsHuman` mid-run and forces a human to
  hand-edit production code. The seam belongs in **its own small upstream task** the test-author
  task `dependsOn`, generated at breakdown time so the run stays autonomous.

  **Detection heuristic (apply while parsing each test-author behavior, Step 4 routing).** A behavior
  requires a seam when it injects a fake/double — `RecordingX`, `FakeX`, `InMemoryX`, a fixture
  source — into a type that is currently constructed **only** via a production constructor with **no
  injection point**. That is the signal. The action prompt's "if no seam exists, write `needsHuman`
  and stop" escape hatch must be the **last resort**, not the default: by run start the seam task
  should already exist.

  Insert **`NN-add-<component>-<seam>-seam`** — a **pure structural production change**: add the
  constructor overload / factory delegate / injectable interface + its DI registration. **No behavior,
  no endpoint** — the seam only opens an injection point. Edge direction: the **test-author task
  `dependsOn` this seam task** (the seam is upstream; the tests compile against it), never the reverse.
  - **Guardrails:** the stack build (`build-passes`, archetype #3 / `stacks/dotnet.md §4`) + a
    **structural check that the seam exists** — the stack file's *declaration* regex (the new
    constructor signature / factory delegate / interface), **never a bare name grep** (catalogue →
    structural-vs-keyword; the .NET seam realizations are `stacks/dotnet.md §11`). Scope the grep to
    the one production file the seam task owns.
  - **TDD-exempt:** a seam is a too-simple structural change with no meaningful unit-test behavior —
    state the exemption reason in the task description (rule (b) of the Step 2 TDD-collapse criteria).
  - **DAG:** the **test-author task `dependsOn` the seam task** (artifact dependency: the tests compile
    against the real seam). With the seam present, the test-author task authors **all** behaviors
    against the real injection point — every behavior fails at runtime (the endpoint/feature is still
    absent) as a clean red, with **no `needsHuman`**.

  Compose with the TDD pair above (the seam task is upstream of `NN-author-tests-<feature>`) and with
  the composition-root wiring bullet below when the same seam must later be **wired in production**
  (#120): the seam task only *opens* the injection point for tests; a wiring task still *constructs and
  injects* the real collaborator at the composition root. Two distinct deliverables — do not conflate
  "a seam exists so tests can inject a fake" with "production injects the real impl."
- **Test framework is not yet chosen** (`$testFramework = none` from Step 0 and no test
  project exists) → the framework is a real fork (xUnit / NUnit / MSTest; jest / vitest;
  pytest / unittest) that **no one has decided**. Never let the action agent guess it from
  its training prior — that is the silent-default failure. Resolve it once, at breakdown
  time, in this priority:
  1. **Detected in the repo** (`$testFramework` ≠ none) → use it; no decision needed.
  2. **Named in the plan** → use exactly what the plan names.
  3. **Absent, and this is an interactive breakdown** → ask the human with `AskUserQuestion`
     (options = the stack's common frameworks; mark the ecosystem's usual choice
     "(Recommended)"). Use the answer.
  4. **Absent, and this is an unattended breakdown** (CI, the golden round-trip meta-test,
     any non-interactive run) → do NOT block and do NOT silently default. Write the
     test-bootstrap / test-author action prompt with the **honest-halt instruction**
     (Step 6) so the choice surfaces to a human at run time, and flag the open choice in
     the breakdown report (Step 7).

  The same priority governs an **E2E driver** choice (Playwright / Cypress); the `$e2eStack`
  detection rule now lives in the two-level UI-verification section below (Step 0 second-dimension
  detection, #41/#78) and in `references/stacks/ui.md`. The browser-driver guardrails it gates are
  Level A (v1 liveness smoke, once the sibling unit lands the #7-generalization archetype) and
  Level B (v2 interaction-flow); until a driver is present, an absent driver is surfaced (report +
  honest-halt), never silently scaffolded.
- Guardrail "schema validates" and no schema exists → insert an author-schema task
  (guardrails: schema file exists + parses + a known-bad sample FAILS validation).
- Guardrail "port answers" → ensure an ancestor produces the launch script, or the
  guardrail owns start/stop itself with a timeout.
- **Component injected at a composition root (Step 4 composition-root-wiring signal fired) →
  insert a wiring task AND a composition-root guardrail (#120 — the recurring lesson).** A plan
  that adds an `IFoo`/`FooImpl` pair behind a constructor seam decomposes into component tasks
  (author tests → implement `FooImpl`) each green, yet no task constructs `FooImpl` and injects it
  into the production assembler — so the feature is dead from the CLI. Insert:
  1. **`NN-wire-<fooimpl>-into-<assembler>`** — the named integration deliverable: construct
     `FooImpl` and inject it into the production assembler (e.g. `SchedulerFactory.Create`
     constructs and passes the provider; `Program.cs` registers it in the DI container) so the
     production path branches into the new mode. Depends on the `FooImpl`-implementation task(s)
     (the collaborator must exist before it can be wired) and on any factory-scaffold task. **Make
     a DAG sink depend on this task** — the wiring is what makes the feature real, so no terminal
     gate should be reachable without it.
  2. **A composition-root guardrail on the wiring task** — the ONLY guardrail that proves the
     component is wired in production. Use the strongest feasible form: **(a)** a
     `specific-tests-pass` (#4) test that drives the REAL assembler (call
     `SchedulerFactory.Create(...)`, NEVER `new Scheduler(..., new FooImpl())` — injecting the seam
     in the test makes it pass even unwired and is FORBIDDEN) and asserts an observable output only
     the wired feature produces; or **(b)** a reflection assertion that the constructed object holds
     the non-null collaborator, WITH a contrast case proving the wiring is conditional (active mode →
     non-null, inactive mode → null) — the `Factory_Wires*` shape. The full .NET realizations
     (drive-the-real-factory test, reflection-on-factory test, and the weakest-acceptable source
     grep) are `stacks/dotnet.md §10`. Author the production-wiring TEST via the TDD pair (author it
     red against the unwired factory — `tests-fail-on-current-code` proves it fails before wiring —
     then the wiring task makes it green). Mark the guardrail `scope: "integration"` **only when it
     ALSO passes the #125 union-safe decision test** (catalogue → composition-root section) —
     evaluated against every union point anywhere in the plan, not just ones upstream of this wiring
     task, since a completely unrelated parallel sibling's merge re-verifies it too (SSOT §4.3). In
     practice this guardrail asserts "the collaborator IS wired," which typically can't be true until
     the wiring task's own attempt has run — so it usually belongs at `scope: "local"` (the default,
     no `scope` key) instead; getting this backwards is what caused #250 live. When the plan names no
     concrete observable to assert on, surface it in the breakdown report (Step 7) as a decision the
     human must confirm — do not invent one.
     (Compose with the server/executable bullet below when the plan is BOTH: wire the entry point to
     the launcher AND wire a collaborator into the factory — two distinct wiring deliverables.)
- **Server/executable plan (Step 4 entry-point-wiring signal fired) → insert a wiring task
  AND a live smoke-test task.** A plan that decomposes into component tasks (scaffold the
  exe project, implement the handler/launcher, implement the routes) verifies each component
  in isolation but never that the binary *starts and serves*. Insert TWO tasks, both after
  the components exist:
  1. **`NN-wire-entrypoint-to-<launcher>`** — connects the entry point to the main
     handler/launcher (e.g. `Program.cs` instantiates and starts `Launcher`). Guard it with
     the structural-grep on the ENTRY-POINT file (`stacks/dotnet.md §7`): the entry point must
     reference the launcher type — a build passes with a `Program.cs` that ignores the
     launcher entirely, so file-exists + build do NOT cover this. Depends on the
     entry-point-scaffold task and the launcher-implementation task (both artifacts must
     exist to wire them).
  2. **`NM-smoke-test-<service>`** — the only guardrail that proves the exe does what the
     plan says. Its guardrail (archetype #7, the script in `stacks/dotnet.md §8`) STARTS the
     built binary as a background process, POLLS a known route (`/health`,
     `/current-step`, whatever the plan names) until it answers or a timeout elapses, ASSERTS
     HTTP 200, and ALWAYS stops the process in a `finally`. Depends on the wiring task (and
     the route-implementation task). This is a `port/endpoint-answers` guardrail that owns
     its own start/stop — no separate launch-script ancestor is required, but the route it
     polls MUST be produced by an ancestor (artifact-ancestry: a smoke-test that polls
     `/current-step` needs the task that implements `/current-step` upstream).

  Place both AFTER the component tasks and BEFORE (or folded into) the terminal
  whole-solution build — the smoke-test verifies runtime behaviour the build never reaches.
  Authoring note for the plan: a server/executable plan should carry one explicit sentence —
  *"the entry point must be end-to-end smoke-testable: run it, hit a route, get a
  response"* — naming the route to poll and the expected status. When the plan is silent on
  the route, surface it in the breakdown report (Step 7) as a decision the human must confirm
  rather than guessing a route. (Scope: starts-and-serves ONLY — *generating the described
  UI itself* is the next insertion bullet, which composes with these two: the wiring+smoke
  tasks prove the exe serves; the UI tasks build and assert the UI that gets served.)
- **UI-facing plan (Step 4 UI-facing-deliverable check fired) → insert a UI-implementation
  task per described screen AND UI-presence guardrails.** A plan describing a browser-served
  screen decomposes into backend routes/handlers/DTOs (each unit-tested green) but produces
  no frontend — the most expensive false-green: a 100%-green run that ships a JSON API with
  no human-facing UI. For each distinct UI surface the Step 1 scratch table recorded, insert:
  1. **`NN-build-ui-<screen>`** — produces the HTML/JS/CSS (or framework component) that
     renders the screen and binds to the backend contract its sibling backend task serves.
     This is ALONGSIDE the backend task, never instead of it. Guard it with **(a) an
     asset-exists check** that the page/asset file is present on disk (e.g.
     `wwwroot/wizard.html`, or the declared embedded resource) — `file-exists` archetype #1,
     scoped to the one file this task owns (`stacks/dotnet.md §9`). It catches the green-build
     run where no frontend file was ever written. Depends on the backend-contract task it
     binds to (artifact-ancestry: the markup references routes an ancestor implements) — but
     keep the dependency as sparse as the DAG rule allows (a static page that only *names* a
     route it will call need not wait on that route's implementation; a page generated *from*
     the contract does).
  2. **A served-markup guardrail that EXTENDS the §8 smoke-test** — NOT a second process
     manager. The smoke-test already starts the binary, polls a route, asserts 200, and tears
     down in `finally`; the UI-presence version reuses that exact lifecycle and adds **one
     assertion**: the response body of the UI route (`/`, `/wizard`, whatever the plan serves)
     **contains a known UI element/string from the page** (a heading, a known `id`/`data-`
     attribute, a wizard step label) — proving the served root returns the real UI markup, not
     a placeholder, a 404 body, or JSON. Place this on the existing smoke-test task (fold the
     content assertion into its guardrail) when the plan has one, so the process is started
     once; only stand up a separate smoke-test task if no executable smoke-test already exists.
     The known string MUST come from the UI the `NN-build-ui-<screen>` task produces
     (artifact-ancestry). The full .NET realization — asset-exists grep plus the §8 lifecycle
     with the body-contains assertion — is `stacks/dotnet.md §9`.

  Place the UI-implementation task(s) alongside their backend siblings and the served-markup
  assertion after the wiring task (the entry point must serve before its body can be asserted).
  The guardrails are deterministic by mandate: an asset-exists grep and a body-contains string —
  **never** a prompt-judge on visual quality (out of scope). When the plan names no concrete UI
  element to assert on (no heading, id, or label to grep for), surface it in the breakdown report
  (Step 7) as a decision the human must confirm — do not invent a string.
- A downstream task reads a state key (`GUARDRAILS_STATE_IN`) → the producing ancestor
  must (a) actually write that key, and (b) carry the fragment-key-present guardrail
  (Step 4 state-output leaf) so a run can't silently feed the downstream task a null.
  The state key is an artifact under the artifact-ancestry rule, just like a file.

**The artifact-ancestry rule:** a guardrail may only reference artifacts (files **and
state keys**) produced by an ancestor task or pre-existing in the repo. Sweep all
guardrails against this rule before Step 6; every violation is a missing inserted task.

## Step 6 — Write the folder

Per `references/schemas.md`, exactly:

- Folder = plan filename minus `.md`, beside the plan. Tasks = `NN-verb-object`
  kebab-case; NN follows a valid topological order (human-scanning hint only).
- `guardrails.json`: version + sensible run config. **Any `.prompt.md` anywhere ⇒
  the `promptRunners` block with a resolvable default is REQUIRED** (else GR2008).
  Scope `allowedTools` to what the actions genuinely need.
- `task.json` per task: `description` (one actionable line), `dependsOn`, a **`stableId`**
  (see below), and overrides only when justified. One `action.*` file per task folder.
- **`stableId` — mint one per task by default.** It is an **internal regeneration-identity
  token** the regeneration merge (§11) uses to track a task across renumber/rename — it is
  **NEVER the state-out key** (the state key is the task FOLDER NAME; see the state-output rule
  in Step 4 and the harness-contract header below). The schema marks it OPTIONAL (a task without
  one falls back to its folder name for identity), but the breakdown mints one per task so
  regeneration can preserve human edits. Mint once; never reuse for a different task; duplicates
  fail validation (**GR2010**). **Format (GR2011):** a `stableId` must match
  `^[a-z0-9][a-z0-9._-]*$` — lowercase alphanumeric, may contain `. _ -`, no
  colon/slash/whitespace/uppercase. Mint short lowercase base36 tokens (e.g. `k3f9a1`, `q7m2zd`).
- **`writeScope` (optional, SSOT §3.4)** — a list of workspace-relative path prefixes/globs
  declaring the surface the task may add/modify/delete/rename; the harness verifies the task's
  diff stays inside it (a deterministic read-only check that never reverts). Emit it for the
  TDD pair (test-author owns the test files; implementation EXCLUDES them — Step 5) and for any
  task you can confidently scope. **Absent ⇒ no check** — OMIT it for a genuinely repo-wide task;
  **never** emit a vacuous `**` or bare top-level dir (escapes the workspace ⇒ **GR2019** error;
  vacuous/over-broad ⇒ **GR2020** warning).
- **`integrationGate` — RETIRED (SSOT §3.3; see the four-folder doctrine bullet above).** There is
  **NO terminal-sink task** and no `integrationGate` field. Do NOT add this key to any `task.json`:
  a plan still declaring `integrationGate: true` is a **hard validation error — GR2029** (no
  coexistence window). The terminal whole-repo integration gate now lives in the plan-root
  **`<plan>/guardrails/`** folder (the "Terminal Gate", run once on the merged plan-branch HEAD); a
  multi-leaf/fan-in plan carries ≥1 real integration-set re-run there (enforced as **GR2028**, the
  re-homed content teeth of the old GR2018). A single linear chain (one leaf, no fan-in) needs no
  terminal folder.
- Every **prompt action** opens with the harness-contract header block, verbatim:

  ```markdown
  ## Harness contract (do not remove)
  - Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
    the appended sections; write ONLY new/changed keys as a JSON object to
    GUARDRAILS_STATE_OUT.
  - Write everything you publish under your task's FOLDER NAME as the single top-level
    key — the name of the directory this task.json lives in (e.g.
    `04-author-tests-tcapi-local`), NOT the stableId. The harness REJECTS a fragment
    keyed by anything else (every attempt), so:
    `{ "04-author-tests-tcapi-local": { "someKey": "someValue" } }`.
  - If a previous-attempt feedback section is appended, this is a RETRY: fix those
    specific failures; do not start over.
  - If you cannot proceed without a human decision, write
    {"needsHuman": "<question>"} to the state-out path and stop.

  ## Task
  <the actual instruction: exact file paths, and completion criteria that MATCH this
  task's guardrails>
  ```

  When you emit this header into a real task, substitute that task's actual folder name
  into the example (`{ "<this-task-folder-name>": { … } }`) so the agent copies the
  right token. A task that publishes nothing to state keeps the line as-is — it is
  harmless and documents the rule for a later editor.

- **Every state-writing prompt must state the folder-name-as-key rule with a concrete
  example (#164).** When a task's action publishes any state (the Step 4 state-output leaf
  fired), the generated `## Task` body must, where it tells the agent to write the fragment,
  show the exact shape keyed by **this task's folder name** —
  `{ "<this-task-folder-name>": { "<key>": <value> } }` with the real folder name
  substituted — and the harness-contract header above already carries the folder-name rule.
  Do NOT key the example by the `stableId`: a `stableId`-shaped token
  (e.g. `j9hf6y`) as the top-level key is rejected by the harness as a foreign/unowned key on
  **every** attempt, dead-ending the task at `needsHuman` (the exact #164 failure loop). The
  state-output guardrail you add reads `GUARDRAILS_STATE_FRAGMENT` and indexes the value under
  that same folder name (`$fragment.'<this-task-folder-name>'.<key>`), so the prompt and the
  guardrail must agree on the folder name as the key.

- **Test-author prompts must carry a `Scope boundary (harness-enforced)` paragraph (#154).**
  Every generated test-author `action.prompt.md` includes — **immediately after the target
  file-path statement** — a paragraph that: (a) names the **exact allowed path(s)** (the test
  file AND, for a behavioral type, the stub file(s) the task's `writeScope` covers — #155);
  (b) states the harness runs a post-action `git diff` membership check and **REJECTS any edit
  outside those path(s)** — production files, neighbouring tests, the `.csproj`, anything; (c)
  states an out-of-scope edit **fails the task immediately and consumes a retry** (not a
  guardrail miss it can recover from inline); and (d) **redirects the "fix the upstream compile
  error" impulse** — a compile error from a missing symbol in **another** file must be surfaced
  as `{"needsHuman": "<what is missing>"}` to the state-out path, NOT fixed by editing that
  file. The last sentence is load-bearing: it sends the natural "just fix the neighbouring file"
  reflex to `needsHuman` rather than an out-of-scope edit that burns a retry. Verbatim shape (the
  allowed paths are the union of this task's `writeScope`):

  ```markdown
  **Scope boundary (harness-enforced):** Write only to
  `<tests/MyProject/MyFeatureTests.cs>` and `<src/MyProject/MyFeature.cs>` (the stub file).
  After this task completes, the harness runs a `git diff` check and rejects any edit outside
  these paths — including changes to other production files, neighbouring test files, or the
  `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a
  compile error caused by a missing symbol in another file, do NOT edit that file — write
  `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
  ```

  (For a data-model task with no stub, the paragraph names only the test file.) The harness
  injects `writeScope` as advisory context at run time, but that injection is information, not a
  constraint with teeth — this paragraph supplies the consequence (`/guardrails-review` flags its
  absence WEAK).
- **Test-bootstrap / test-author action prompts must name the framework or halt — never
  guess.** When the framework was resolved (Step 5 cases 1–3), name it concretely (e.g.
  "an xUnit.v3 test project") and tell the agent to mirror existing projects' package
  versions **only if such projects actually exist**. Never instruct an agent to "mirror
  the existing test projects" in a workspace that has none — that self-contradiction is
  the #40 failure (the agent resolves it by silently guessing). When the framework is
  unresolved in an unattended run (Step 5 case 4), the `## Task` instead says, verbatim in
  spirit:

  > No test project exists and no framework was specified. Do NOT assume one. Write
  > `{"needsHuman": "No test framework found — which should <TestProject> use: xUnit,
  > NUnit, or MSTest?"}` to the state-out path and stop.
- **Durable markers over line numbers; caveat any "here's how it currently works" claim about a
  not-yet-run sibling (#203).** This fires whenever a **later-wave task's** prompt references code
  an **earlier-wave task in the same plan** will create or modify before the later task actually
  executes. The authoring-time snapshot and the run-time reality are two different moments — by
  construction, the earlier task WILL touch that file before the later task runs, so anything the
  prompt says about "where" or "how" that code looks is a claim about a state that has not happened
  yet. Two coupled rules:
  1. **Never cite a line number for code an earlier-wave sibling will touch first.** A line number is
     a snapshot that shifts the instant the earlier task edits the file — the later task's prompt was
     necessarily authored before that edit landed, so the pointer is stale on arrival by construction,
     not by bad luck. Cite a **durable, structure-stable marker** instead: a distinctive comment string
     already in the code (or one the earlier task's own prompt is instructed to leave behind), a
     method/class/type name, or a symbol the agent can `grep` for regardless of how the surrounding
     lines drift. *Worked example (the motivating incident, issue #202):* a later task's prompt said
     "this REPLACES the terminal integration gate run (`Scheduler.cs` ~231-253)" — by the time it ran,
     an earlier-wave task had already landed and shifted every line number in the file. The fix is not
     a fresher line number (it will just go stale again on the NEXT earlier-wave edit) — cite the
     block's own marker instead: "the block marked `// --- C1 terminal whole-repo integration gate ---`
     in `Scheduler.cs` (grep for it; do not rely on a line number, which will have moved)."
  2. **Caveat any architectural claim about a sibling's not-yet-run implementation as authoring-time
     state, not settled fact.** Phrase "here's how deliverable N currently works" as a checkable
     hypothesis, never as a given the later task should build on unchecked: *"this reflects the
     plan-authoring-time state, before deliverable N had actually run — verify it's still accurate
     before assuming the same shape applies here."* The same incident is the worked example: task 08
     was described as "extending the same `Scheduler.cs` path," but it actually built a brand-new
     standalone class (`PlanPreflightPhase.cs`) invoked from `RunCommand.cs` — an unhedged claim would
     have sent the later task confidently re-discovering a `Scheduler.cs` extension that was never
     built.

  **These two rules are companions, not independent bullets — apply both together whenever the
  trigger fires.** A prompt that hedges the architecture claim but still cites a raw line number (or
  vice versa) only half-fixes the failure: the durable marker survives the line drift, but an
  unhedged claim can still send the agent confidently down the wrong structural path even once it
  finds the right code. When this trigger fires, it also usually earns the task a `maxTurns: 75`
  bump — see Step 4a's fourth archetype, "integrates with a sibling's not-yet-landed implementation."
  Author the prompt text AND set the budget together; they are the two halves of the same fix for the
  same underlying situation (Step 4a says more on when the pairing is required vs. one without the
  other).
- **Every explicit "do NOT …" prohibition in a generated action prompt needs a matching structural
  guardrail — or an explicit note that none exists (#221).** Before finalizing any action prompt that
  states a prohibition ("do NOT wrap this in a retry loop," "do NOT weaken this assertion," "do NOT use
  approach X"), ask: **is the forbidden behavior structurally checkable** — a regex, a count, or a
  shape/AST test on the file this task modifies? If **yes**, emit a guardrail enforcing it ALONGSIDE the
  prohibition (Step 4/5) — never rely on the prose alone; an adversarial or merely lazy implementation is
  free to ignore a prohibition no guardrail backs. Reach for the archetype that fits the shape: a
  **negative assertion** (fail-on-present, #176) for an excluded keyword/scenario; a **regex-lock**
  asserting the load-bearing text survives verbatim, or a **count + forbidden-construct scan** (e.g.
  "exactly one call to `X`, no `for`/`while`/`catch`") for a banned approach/shape. If **no** (a genuine
  judgment call with no mechanical proxy), **state that explicitly in the breakdown report** (Step 7)
  rather than silently leaving it unguarded — an unacknowledged, unguarded prohibition is invisible to
  the human reviewer. **Watch for the perverse case**: when the task's other guardrail is
  EMPIRICAL/statistical (a "run N times, assert it always passes" flake check), the forbidden shortcut
  can make that guardrail EASIER to pass, not harder (a weakened assertion tolerates the very race the
  guardrail exists to catch) — treat that combination as the highest-priority case to close. (Catalogue →
  "Prose-only prohibition, no structural backing.")
- `state/seed.json` only if the plan implies initial shared state (input paths,
  names, configuration the tasks read).
- Scripts: prefer the workspace's native platform; note any interpreter requirement
  beyond the defaults in `guardrails.json: interpreters`.

## Step 7 — Self-validate and report

0. **Exit-criteria self-review — a UI plan that built zero UI fails its own review.**
   Before validating, cross-check the plan's exit/acceptance criteria against what the
   tasks actually produce. The load-bearing case (issue #66): if an exit criterion is
   phrased as a **user action in a UI** — "the user can complete the wizard in the
   browser", "navigate the master/detail view", "see the dashboard" — then **some task
   must produce that UI** (a `NN-build-ui-<screen>` task with a UI-presence guardrail).
   A plan that promises a frontend but decomposed to **zero UI-implementation tasks** is
   the signal that the UI surface was silently dropped onto a backend route (the Step 1
   failure). Do NOT proceed to a clean report: either insert the missing UI task(s) and
   guardrails (loop back to Steps 4–5), or — if you cannot (the plan is too vague about
   the screen to build it) — **flag it loudly in the report as a self-review failure**:
   name the exit criterion, state that no task builds the UI it names, and present it as
   a blocking decision the human must resolve before `guardrails run`. The same shape
   applies to any exit criterion naming an observable a guardrail should but doesn't
   cover; the UI case is just the one #66 makes most expensive (a fully-green run with no
   frontend).
0a. **Task-size self-review — re-run the Step 2 split-trigger on every emitted task (#111).**
   Before validating, sweep the final task list back through the Step 2 over-size split-trigger.
   For each task, confirm NONE of the triggers fires: (a) it does not bundle multiple distinct
   deliverables ("X **and** Y **and** Z"); (b) its blast radius is bounded (not deleting ≥3 source
   files or touching ≳10 files / test references in one action); (c) it is not a design milestone
   sized 1:1; (d) a single failed-guardrail retry does not re-run an hour of work. Cross-check
   against the plan's own feasibility/self-critique signals: any milestone the plan flagged as
   heavy / over-packed / high-churn MUST have been split, not sized 1:1. A task that still trips a
   trigger is **mis-sized** — loop back to Step 2 and split it (scoping each piece's test
   re-baseline to that piece) before proceeding. If you cannot split it (the plan genuinely couples
   the work), **flag it in the report** as an over-scoped task and warn that its retry is expensive
   and it is the most likely `needs-human` — do not present it as well-sized.
0b. **Deliverable-coverage self-review — EVERY numbered design deliverable maps to a task (#110).**
   The UI exit-criteria check (7.0) is **one instance** of a general property: *every numbered design
   deliverable in the plan maps to at least one generated task.* A deliverable that lives in the plan's
   body without a milestone can be silently dropped, and the run drains fully green having built a
   **subset** of the plan — the deliverable-coverage analogue of the UI false-green, and just as
   expensive (a missed feature with a 100%-green run). Generalize the check:
   1. **Build the deliverable set.** Enumerate the plan's **numbered deliverables** from ALL of:
      placement-table rows, top-level `§`-sections, and "what's being asked / done when" items — not
      merely the milestone list. The Step 1 scratch table is the starting point; reconcile it against
      the plan's section structure so a body deliverable without a scratch-table row is not missed.
   2. **Cross-check each deliverable against the generated tasks.** For every deliverable, point to the
      task(s) that produce it. **Any design deliverable with NO producing task is a self-review
      finding.**
   3. **Specifically flag milestone-vs-body divergence.** The load-bearing miss (#110, plan-08 dogfood):
      a feature in the design **body** (a `§`-section, a placement-table row) that maps to **no
      milestone** — the breakdown leaned on the M1–Mn milestone list as its task source, so a
      `§`-deliverable without a milestone home had nowhere to map and was dropped. (The dropped feature
      in the motivating case was *§9 AI-triage-on-needs-human* — tagged "and a later milestone" but never
      given one.) When a feature appears in the body but in no milestone, **warn**: the breakdown must
      cover the *design*, not just the *milestone list*.
   Do NOT proceed to a clean report with an uncovered deliverable. For each finding, either insert the
   missing task(s) and guardrails (loop back to Steps 4–5), or — if it is genuinely deferred — present
   it as a **blocking decision the human must resolve**: name the deliverable, state that no task
   produces it, and ask the human to add the task or confirm it is intentionally deferred to a later
   version. Surface a **`guardrails-review` probe** in the report so the adversarial pass re-checks
   coverage: "every numbered design deliverable maps to a task; no body/`§`-deliverable was dropped for
   lacking a milestone." The UI-exit-criterion case (7.0) remains the most expensive instance; this is
   the general rule it specializes.
0c. **Positive-baseline self-review — a BROWNFIELD plan must have a baseline preflight per touched area
   (#181).** If Step 0 set `$baselineArea` ≠ none (the plan modifies project(s) with existing tests in the
   touched area) AND the worth-it gate passed, confirm the breakdown emitted one
   `<plan>/preflights/01-baseline-<area>-tests-green` check per touched area: a guardrail-shaped preflight
   FILE (no task, no `dependsOn`) that runs the EXISTING area tests **via `--filter`** (NEVER the whole
   suite/project — that hits the #165/#176 compile-coupling trap) and asserts they PASS (using the #179
   re-emit form). It runs before the DAG against the starting repo, so every task is implicitly gated on
   it. Cross-check three things: (a) the check targets only the PRE-EXISTING tests via `--filter` — NOT the
   about-to-be-authored red tests (it runs on the starting state, before any `author-tests` task adds its
   failing tests); (b) it is deduped one-per-area (no single whole-repo preflight); (c) the worth-it gate
   genuinely held (≥2 work tasks build on the area). If the plan is **greenfield** (`$baselineArea = none`)
   or the worth-it gate failed, confirm NO baseline preflight was emitted and the report will STATE that
   (nothing to baseline). A brownfield plan missing the baseline preflight, a greenfield plan carrying a
   vacuous baseline, a whole-suite-scoped baseline, or a lingering no-op ROOT baseline TASK (the retired
   model) is a self-review finding — loop back to Step 5. Surface a **`guardrails-review` probe** in the
   report: "brownfield plan has a `--filter`-scoped, deduped baseline preflight per area; greenfield states
   why none."
1. Run `guardrails validate <folder>`. Fix and re-run until exit 0 (or report that
   validation was skipped and why).
2. Optionally run `guardrails plan <folder>` and sanity-check the waves against your
   DAG intent.
3. Once validation passes, run `guardrails graph <folder>` to generate
   `<folder>/diagram.md` and its `<folder>/diagram.html` pan/zoom/fullscreen companion (Mermaid
   `flowchart TD` renders of the task/guardrail DAG — generated artifacts, never hand-edited; see
   `references/schemas.md`). Note the `Diagram (interactive): <link>` line this command prints —
   you relay it verbatim in Step 7.4 (issue #249). Then run
   `guardrails lock <folder>` to write the committed `guardrails.baseline` BASE manifest, so a
   future regeneration can preserve any guardrails the human edits in the meantime (§11).
4. Emit the **breakdown report**: task table (id, action kind, guardrails with
   archetype numbers, dependsOn), the inserted-task list with justifications, edge
   justifications, and any flagged non-executable plan content. **Surface every decision
   the human should confirm** — chief among them any test-framework or E2E-driver choice:
   state which was used and why (detected in repo / named in the plan / asked via
   `AskUserQuestion` / left as a needs-human halt). A wrong framework poisons every
   downstream test task, so it must never be buried. **If the plan was UI-facing**, state
   the outcome of the Step 7.0 exit-criteria self-review: each UI surface, the
   `NN-build-ui-<screen>` task that builds it, its UI-presence guardrail (asset-exists +
   served-markup string asserted), and the known UI string the served-markup check greps
   for — or, if a screen could not be built from the plan, the blocking self-review
   failure. Then **embed the generated Mermaid
   block inline** (paste the ```mermaid``` fence from `diagram.md`) so the human sees the
   DAG in chat, and **state the `<folder>/diagram.md` path** explicitly so they can render
   it in GitHub or VS Code. Finally, as the **last line of the report**, relay the
   `Diagram (interactive): <link>` line that `guardrails graph` itself printed when you ran
   it in step 3 — copy it **verbatim** from that command's own output so the reviewer can
   open the interactive viewer without hunting for it. As of issue #249 the CLI emits this
   as a ready-to-click **OSC 8 hyperlink** (the same `RunCommand.Hyperlink` escape shape
   `guardrails run` uses for its `Logs` link), built from its own absolute path via .NET's
   `Uri` — do **not** hand-assemble a `file://` URL yourself from a shell `pwd`: under Git
   Bash/MSYS on Windows, `pwd` returns the non-resolvable mount form (`/f/...`) instead of
   the native drive form (`F:/...`) a `file://` URI needs, which is exactly the bug #249
   fixed by moving link construction into the CLI.
5. Close with, verbatim in spirit:

   > **This is a draft.** Review the folder — especially the guardrails — edit,
   > delete, or add, then run `/guardrails-review <folder>` before executing with
   > `guardrails run <folder>`.

   Never present the output as execution-ready.

## Step 8 — Regeneration merge (only when the folder already exists, Step 0 → merge)

The plan is the source of truth, but a human may have edited or added guardrails since the last
generation. **Re-derive the tasks from the changed plan while preserving those edits** — never
hand-clobber the folder. The deterministic engine owns the per-guardrail decisions; you only
generate and orchestrate. See SSOT §11.5.

**Baseline-first check (do this before staging).** Confirm `<folder>/guardrails.baseline` exists.
If it does **not**, run `guardrails lock <folder>` first to adopt the current folder as BASE, and
tell the human the first merge will take REMOTE for every guardrail (there is no recorded baseline
to preserve edits against).

1. **Generate into staging, identity-aware.** Run Steps 1–6 but write the new folder to a
   temporary **staging** directory, a sibling `<plan-name>.staging/`. For each regenerated task,
   decide whether it is the **continuation** of an existing one. Use this priority:
   - (a) **same verifiable outcome / primary artifact** the task produces;
   - (b) **same/near description intent**;
   - (c) **same DAG position** (the upstream/downstream artifacts it connects).

   A renamed/renumbered/reworded task with the **same outcome IS the continuation** → **reuse its
   `stableId`** (read it from the current folder's `task.json`). A materially-changed or absent
   task is **not** the continuation → **mint a fresh `stableId`** (or let it drop). If genuinely
   ambiguous, **mint fresh and note it** — the merge then takes REMOTE rather than risk a wrong
   preserve. Be deliberate: this judgment is what the merge relies on.

   **1a. Re-align each `covers-key-behaviors` guardrail with its EDITED action prompt (#157).**
   Whenever this regeneration **edits a continuation task's action prompt** — removes a scenario
   (a mode/behavior dropped from scope), narrows the scope, or renames a behavior — you MUST, in
   the SAME pass, scan that task's `covers-key-behaviors` guardrail for any required token that
   matches the removed/renamed scenario and **remove or replace it** so the guardrail and prompt
   cannot drift apart. The drift this prevents: the merge preserves the human-edited (or prior)
   coverage guardrail while REMOTE rewrites the prompt, so the guardrail keeps requiring a token
   (e.g. `CommanderRest`) the prompt no longer asks the agent to encode — a correct
   implementation then fails the guardrail on **every** attempt and the task dead-ends at
   `needsHuman` (the #157 failure mode the GR2026 lint and the `guardrails-review` stale-coverage
   probe also catch, after the fact). Concretely, for each edited prompt: diff its scenario list
   against the matching guardrail's `if ($content -match "<token>")` / `-notmatch … exit 1` lines;
   for every token whose scenario the edit removed, delete that `if`-block (and decrement any
   `$hits -lt N` threshold) or replace the token with the renamed scenario's distinctive term. A
   token that survives in the guardrail must still be named in the rewritten prompt. Re-running the
   Step 4 covers-key-behaviors selection on the new prompt is the clean way to regenerate the
   guardrail from scratch when the scenario list changed substantially.
2. **Dry-run the merge:** `guardrails merge <folder> --remote <staging>`. Branch on the exit code:
   - **Exit `0`** — no conflicts. Proceed to apply (step 3).
   - **Exit `2`** — read the output to disambiguate (this code has two meanings):
     - If the message says `guardrails.baseline missing` → run `guardrails lock <folder>` to adopt
       the current folder as BASE, tell the human the first merge will take REMOTE for every
       guardrail (no recorded baseline), then **re-run the dry-run**.
     - Otherwise it is **conflicts** → surface each `CONFLICT <stableId>/<file> — <reason>` line to
       the human, then **STOP**. The human resolves (edit the guardrail or the plan), then you
       re-run the dry-run. **Never apply** with conflicts present.
   - **Exit `1`** — a genuine error (missing folder/remote, corrupt baseline, or an invalid plan on
     either side, incl. a duplicate `stableId` → **GR2010**). **STOP**, surface the message, fix the
     cause, and re-run. **Never apply on a non-zero, non-handled code.**
3. **Apply (only after exit 0):** `guardrails merge <folder> --remote <staging> --apply`. This
   replaces authored content with REMOTE's, overlays the preserved human guardrails, and
   **RE-WRITES THE BASELINE** (writes the new BASE `guardrails.baseline`). Then **delete the staging directory** and:
   - run `guardrails validate <folder>` — **fix until exit 0**; the merged folder is freshly
     assembled, do not assume it validates;
   - run `guardrails graph <folder>` to regenerate `diagram.md` (the merge deliberately leaves the
     old diagram stale).

   **Do NOT run `guardrails lock` again** — `--apply` already wrote the baseline, and `diagram.md` is
   excluded from the baseline, so regenerating the diagram does not invalidate it.
4. **Report.** Relay the command's own summary line verbatim
   (`N preserved, N dropped, N conflict(s), N from regeneration`) plus any `warning:` lines, then
   close with the Step 7 draft message.

**Staging cleanup.** The `<plan-name>.staging/` directory is temporary scaffolding — delete it on
**every** exit path (conflict-stop, error-stop, and success) and **never commit it**.

<!-- BEGIN ADDED SECTION #94 — maxTurns budgeting by archetype (auto-merge friendly; do not merge into prose above) -->
## Step 4a — Budget `maxTurns` by task archetype (#94)

`maxTurns` defaults to **50** in `guardrails.json`'s `promptRunners.<name>` (SSOT §2). That flat
cap is right for most tasks, but a few archetypes are **predictably turn-expensive** because the
agent must *discover an API before it can write code* — not because the task is mis-sized. A
legitimately-progressing agent reverse-engineering an unfamiliar SDK (grepping package XML docs to
wire an in-process client) can exhaust the 50-turn cap mid-progress; every retry then hits the same
wall and the run dead-ends on a task a larger budget would have completed (#94). This is **not** a
sizing failure — the one-session and guardrail-boundary rules (Step 2) correctly pass it; the cost
driver is *research overhead*, which those heuristics do not model. **Do NOT "split it further"** to
fix a turn-budget exhaustion — splitting an assertion-set that shares one expensive setup (an
in-process harness) only **duplicates** that setup and makes it worse.

**The rule — bump the turn-expensive archetypes to a single modest fixed value, deliberately.**
For every PROMPT task (`.prompt.md` action), check it against the turn-expensive archetypes below.
If ANY matches, set a per-task `maxTurns` override of **75** (a first-attempt cushion that clears
the common boundary case — empirically actuals were 54 and 32 against a flat 50). Leave every other
prompt task at the inherited default (omit the override). Set the override on the **task**, in
`task.json`'s `action.maxTurns` (prompt actions only — SSOT §3), or in the prompt-file frontmatter
`maxTurns:` (schemas.md "Prompt files"); precedence is `task.json action.*` → frontmatter → runner
config. Script tasks have no `maxTurns` — skip them.

**Turn-expensive archetypes (bump to `maxTurns: 75` — set if ANY holds):**
- **Integration / smoke / e2e tests** — especially an **in-process harness**, transport/transport-
  client wiring, or spawning a server (the §8 live smoke-test, the §10a drive-the-real-factory
  integration test, an in-process stdio/MCP client↔server harness).
- **Work against an unfamiliar third-party SDK** — the agent must discover the API surface (grep
  package XML docs, probe option/result types) before writing code. The tell: the plan names a
  third-party SDK/protocol library no ancestor task has already established a working call against.
- **Terminal aggregation / wiring tasks** that connect several pieces (the composition-root wiring
  task §120, the entry-point-wiring task §64) — they touch multiple unfamiliar seams at once.
- **Integrates with, extends, or describes a sibling task's not-yet-landed implementation (#203/#204)**
  — the task's action prompt must integrate with, extend, or describe an **earlier-wave deliverable in
  the same multi-wave plan** that did not exist yet at plan-authoring time. The root cause differs from
  the other three archetypes above — this is **temporal ordering within the plan**, not external
  unfamiliarity or aggregation complexity — but the re-discovery cost is the same shape: the agent must
  locate and understand code that may not match what the prompt described, because the prompt was
  necessarily written before that code existed. The tell: the prompt says "this extends/replaces
  deliverable N's implementation" or otherwise describes a same-plan sibling's code the DAG places in
  an earlier wave. **This is the companion trigger to the durable-marker/architecture-caveat authoring
  rule (SKILL.md Step 6, #203)** — when a task needs one, it usually needs the other: hedge the prompt
  text (durable markers, caveated architecture claims) AND bump `maxTurns` together, since both exist to
  absorb the SAME underlying re-discovery risk. Don't apply one without checking the other.

**Why a FIXED bump, not a guessed exact budget.** `/plan-breakdown` cannot guess an exact per-task
turn count — actuals are unguessable (54 and 32 in the motivating run, vs. a hand-guess of 120/100).
The fixed 75 is a **deliberate first-attempt cushion**, not a precise budget. The real safety net is
a harness-side auto-escalate-on-`max_turns` retry policy — a SEPARATE harness concern (see the
follow-up note below); the breakdown's job is only to stop the *common* boundary case from
dead-ending on attempt 1 and to make the heuristic **visible and reviewable** rather than discovered
by a failed run.

**Amortize unfamiliar-SDK discovery — insert a shared-harness task when ≥2 tasks need the same
setup (a Step 5 insertion).** When **two or more** downstream tasks need the same non-trivial setup
against an API **no ancestor has established** (e.g. an in-process MCP client harness needed by both
a smoke-test task and a parity-test task), insert an upstream task that builds that harness/helper
**once** (learn the API, write a reusable `<X>TestHost`), so each downstream task builds on it
instead of independently re-discovering the API. This is the test-harness analogue of the
production-seam / composition-root insertions (Step 5) — a generative insertion driven by a *shared
discovery cost*, not a missing artifact. Heuristic: *does expressing these tests require non-trivial
setup against an API no ancestor has yet established, needed by >1 task? → insert a harness task,*
make the downstream tasks `dependsOn` it, and give the harness task itself the `maxTurns: 75` bump
(it is the integration/unfamiliar-SDK task that pays the discovery cost). State the insertion and
its justification in the Step 7 report, like any other inserted task.

**Report it (Step 7).** List every task that got the `maxTurns: 75` bump and which archetype
triggered it, plus any inserted shared-harness task — so the human sees the budgeting was applied
deliberately, not by accident.

> **Harness follow-up (NOT a breakdown change — flag, do not implement here).** Issue #94 also asks
> the *harness* to surface `max_turns` terminations distinctly (today the composed retry feedback
> says only "claude exited 1", burying `terminal_reason: max_turns` in `claude-stream.jsonl`) and to
> **auto-escalate the budget** on a `max_turns` termination (e.g. ×1.5 for the next attempt) rather
> than retrying into the same wall. That is the real safety net for the unguessable-budget problem.
> It lives in `src/**` (runner feedback composition + retry policy), owned by
> `guardrails-harness-developer` — **out of scope for the skill**; the breakdown's fixed bump is
> only the first-attempt cushion that pairs with it.
<!-- END ADDED SECTION #94 -->

<!-- BEGIN ADDED SECTION #116 — Windows-safe shared git-repo test fixture (auto-merge friendly; do not merge into prose above) -->
## Step 5a — Emit a Windows-safe shared `TempGitRepo` fixture when author-tests build real git repos (#116)

When an author-tests task's tests create a **real git repository** on disk, the test-author agent
keeps re-discovering Git-for-Windows semantics that POSIX-only helpers miss — each a fresh
`needs-human` halt (#116): a `Directory.Delete(recursive)` throws `UnauthorizedAccessException`
(not `IOException`) because Git marks `.git/objects` loose objects **read-only** on Windows (#109);
`git rm`/`git mv` **prunes the now-empty parent directory**, so the next `File.WriteAllText` into it
throws `DirectoryNotFoundException` (task-14); `git merge --abort` fails rc=128 on a dirtied tracked
path (W3). Because the breakdown generates each author-tests task in isolation, every test-author
agent re-discovers (or misses) these independently. **Resolve it once, at generation time** — same
posture as the test-framework decision (Step 5): don't let each agent rediscover a known quirk.

**When this fires.** A code/author-tests task whose tests **construct a real git repo** (the plan
or the task description mentions `git init`, committing fixtures, merge/rename/lock behavior over a
real repo, or asserting on `.git` state). It does NOT fire for tests that merely run in the repo
without creating their own.

**Two ways to satisfy it (pick per task):**
1. **Emit a shared, Windows-safe `TempGitRepo` test fixture** — ONE file the generated git-touching
   tests reuse (insert it as its own deliverable, or as the first artifact of the first git-touching
   author-tests task; later git-touching tasks `dependsOn` it and reuse it, never re-author it). The
   fixture's required behaviors are non-negotiable (each is a logged Windows-git lesson):
   - **strip read-only attributes before `Directory.Delete`** (the #109 lesson — loose objects are
     read-only on Windows);
   - **recreate directories emptied by `git rm`/`git mv` before writing into them** (the task-14
     lesson — Git-for-Windows prunes the empty parent);
   - **roll back with `git reset --hard <preHead>`, NOT `git merge --abort`** (the W3 lesson —
     `--abort` fails rc=128 on a dirtied tracked path);
   - **normalize line endings (`core.autocrlf=false`)** so fixture content hashes are deterministic
     across platforms.
   The .NET realization (a complete, copy-pasteable `TempGitRepo` IDisposable) is `stacks/dotnet.md
   §11`; the universal doctrine is `references/guardrail-catalogue.md` → "Windows-safe git test
   fixture (#116)".
2. **Inject a "Windows-Git test portability" directive** into the git-touching author-tests action
   prompt — point the agent at the shared fixture (option 1) and name the four behaviors above, so a
   test that builds its repo inline still gets them right. Prefer option 1 (one reviewed fixture)
   over option 2 (a directive repeated per task) when ≥2 tasks build real git repos — same
   amortize-the-discovery logic as #94's shared-harness insertion.

**Guardrail.** The fixture itself is test infrastructure; guard the FIRST git-touching author-tests
task's `tests-fail-on-current-code` as usual. When you emit the fixture as a distinct artifact, add
a `file-exists` (#1) guardrail scoped to the fixture file and a `tests-build` (#3) guardrail so a
non-compiling fixture fails loudly rather than silently breaking every downstream git test. Report
the fixture (or the injected directive) and which tasks reuse it in Step 7.
<!-- END ADDED SECTION #116 -->

<!-- BEGIN ADDED SECTION #101 — new-.claude/-subdirectory deliverable detection + seeding (auto-merge friendly; do not merge into prose above) -->
## Step 5b — Detect a deliverable in a NEW `.claude/` subdirectory and seed it (#101)

Claude Code's `acceptEdits` permission mode (the breakdown's default runner profile, schemas.md)
auto-approves writes to **existing** paths but **blocks creating a new subdirectory under `.claude/`**
without explicit interactive confirmation. In headless harness execution there is no human to
confirm, so an agent writing `.claude/skills/<new>/SKILL.md` into a not-yet-existing directory
correctly self-blocks with `{"needsHuman": "..."}` and the run halts (#101). The breakdown KNEW the
task was creating a new `.claude/` deliverable and should have removed the barrier before the run —
this is the directory analogue of the artifact-ancestry rule (a guardrail referencing a file no
ancestor produces is a missing inserted task; here the missing prerequisite is a *directory*).

**When this fires.** A task's **primary deliverable is a file inside `.claude/`** — `.claude/skills/`,
`.claude/commands/`, `.claude/hooks/`, `.claude/agents/`, `.claude/contexts/` — **AND** the target
subdirectory does not already exist in the workspace. Check existence at breakdown time
(`Test-Path .claude/skills/<name>/`): an EXISTING subdirectory needs nothing (`acceptEdits` approves
writes into it); only a NEW subdirectory trips the barrier.

**The rule — seed the directory, or warn, before the write task.** Choose one:
1. **Insert a directory-seed task** immediately before the writing task: `NN-seed-<name>-dir` whose
   action writes a `.gitkeep` to the target path (e.g. `.claude/skills/survey-eval/.gitkeep`), making
   the directory "existing" so `acceptEdits` approves the subsequent `SKILL.md` write. The writing
   task `dependsOn` it. Prefer this for an unattended run — it removes the barrier deterministically
   rather than relying on a human pre-step. **The seed task MUST be a SCRIPT action**
   (`action.ps1`/`action.sh` running `New-Item -ItemType Directory` + a `.gitkeep` write), never a
   prompt action: a script the harness runs directly is NOT subject to Claude Code's `acceptEdits`
   tool-permission barrier, so it creates the new `.claude/` subdir headlessly — whereas a *prompt*
   seed task would hit the exact same barrier it is meant to remove. (It is a script, so it carries
   no `maxTurns`.)
2. **Add a `## Pre-conditions` note** to the writing task's action prompt, stating that the caller
   must pre-create the target directory (a committed `.gitkeep`) before the harness run. Use this only
   when a seed task is undesirable (e.g. the human explicitly owns the directory creation).

**Guardrail.** Give the writing task (or the seed task) a `01-dir-seeded.ps1` guardrail asserting the
target subdirectory exists **before** the write is attempted, so the barrier surfaces as a readable
guardrail failure rather than a cryptic mid-run `needsHuman`:

```powershell
# catches: a task that writes into a NEW .claude/ subdirectory the harness's acceptEdits mode
#          cannot create headlessly - the dir was never seeded, so the write self-blocks to
#          needsHuman. Assert the target subdir EXISTS before the SKILL.md write is attempted.
$dir = ".claude/skills/survey-eval"
if (-not (Test-Path $dir -PathType Container)) {
    Write-Output "$dir does not exist - seed it (a committed .gitkeep) before the harness run; acceptEdits cannot create a new .claude/ subdir headlessly"
    exit 1
}
exit 0
```

Scope the guardrail to the one target subdirectory the task owns. Report the seed task (or the
pre-condition note) and the affected `.claude/` path in Step 7.

> **Harness relation (NOT a breakdown change).** Issue #104 is the harness-side counterpart (whether
> the runner can be granted the write up front). The breakdown owns only the **detection + seeding**
> doctrine above; do not edit `src/**`.
<!-- END ADDED SECTION #101 -->

<!-- BEGIN ADDED SECTION #87 — one skill directory per task (auto-merge friendly; do not merge into prose above) -->
## Step 2c — One skill directory per task (#87)

A sizing rule that specializes the Step 2 over-size split-trigger for a common shape: a milestone
that **updates more than one `.claude/skills/<X>/` directory in one task**. It complements the #111
split-check — #111 splits by deliverable count and blast radius; this splits by *skill-directory
count*, because skill files are large and read-heavy in a way #111's file-count heuristic
under-weights.

**Why a skill directory is special.** A skill folder (`SKILL.md` + `references/`) is large and must
be **read in full before any write begins** — the agent loads the whole procedure to edit one rule
correctly. Bundling two skill-directory updates into one task **doubles the read budget before the
first write**, which is the cheapest way to exhaust a turn budget on a task that is otherwise
well-formed (the #94 turn-expensive shape, reached by read volume rather than research). It also
widens the `writeScope` to span unrelated directories, makes partial completion hard to recover (a
failure in skill Y re-runs the edits to skill X), and means a single permission issue on one
directory blocks the whole deliverable.

**When this fires.** A candidate task whose deliverable edits **two or more** distinct
`.claude/skills/<X>/` directories (counting `references/` under a skill as part of that skill's
directory). It does NOT fire for a task editing several files **within one** skill directory
(`SKILL.md` + two of its `references/`) — that is one skill, one read budget, one task.

**The rule — one skill directory per task; regeneration and verification are their own downstream
tasks.** When a milestone spans N skill directories:

1. **Emit one update task per skill directory** — `NN-update-<skill>-skill`, each with a
   `writeScope` narrowed to that one directory (e.g. `writeScope: [".claude/skills/plan-breakdown/"]`).
   Each is independently atomic, independently retryable, and trivially scoped. These are usually
   **parallel** (sparsest-DAG rule, Step 3) unless one skill's change consumes another's.
2. **Make golden-example regeneration its own downstream task** — `NN-regenerate-golden-example`
   (`writeScope: ["examples/<example>/"]`), depending on every skill-update task whose change it
   must reflect. Do NOT fold the regeneration into a skill-update task: its `writeScope` (the
   example folder) is disjoint from the skill directories, and it can only run once the skill edits
   it depends on are committed.
3. **Make round-trip verification its own terminal task** — the `guardrails validate` / golden
   round-trip check, depending on the regeneration task. On a parallel plan its whole-repo re-run
   belongs in the plan-root `<plan>/guardrails/` terminal folder (NOT a retired `integrationGate` sink
   task).

*Worked split (the #87 motivating case).* A task scoped to update
`.claude/skills/plan-breakdown/`, `.claude/skills/guardrails-review/`, and
`.claude/skills/guardrails-domain-knowledge/` plus three reference docs, regenerate the golden
example, and run the round-trip test, fires this rule (3 skill directories) AND the #111 trigger.
Split it into:

```
NNa-update-plan-breakdown-skill        writeScope: .claude/skills/plan-breakdown/
NNb-update-guardrails-review-skill     writeScope: .claude/skills/guardrails-review/
NNc-update-domain-knowledge-skill      writeScope: .claude/skills/guardrails-domain-knowledge/
NNd-regenerate-golden-example          writeScope: examples/hello-guardrails/   dependsOn: NNa,NNb,NNc
NNe-roundtrip-validate                 (terminal gate → <plan>/guardrails/)     dependsOn: NNd
```

**Self-review (folds into Step 7.0a).** When sweeping emitted tasks back through the split-trigger,
also count skill directories per task: any task whose `writeScope` (or, if omitted, its described
deliverable) spans ≥2 `.claude/skills/<X>/` directories is mis-sized — loop back and split it. The
related knowledge-skill SELF-UPDATING clauses mean a knowledge-skill body is updated by whoever
changes the underlying fact; sizing those updates one-directory-per-task keeps each such update
atomic.
<!-- END ADDED SECTION #87 -->

<!-- BEGIN ADDED SECTION #41/#78 — two-level UI verification: liveness smoke + behavioral E2E (auto-merge friendly; do not merge into prose above) -->
## Step 4b / 5c — Two-level UI verification (#41 v1 doctrine; #78 v2 interaction-flow)

This section governs verifying **browser-rendered UI** beyond "the binary serves *something*". Read
`references/stacks/ui.md` (the methodology, detection ladder, and the v2 boundary) alongside it.

**Read the boundary first.** Three existing checks already verify increasingly more of a UI plan,
none of which DRIVE the UI:
- **§64 entry-point wiring + smoke-test** — the exe *starts and serves* (HTTP 200 from a route).
- **§66 / dotnet.md §9 UI-presence** — the described UI is *built and served* (a single `GET` body
  contains a known UI marker). One request; not a flow.
- **This section** — two NEW levels that need a real **browser driver** (`$e2eStack`), which §64/§66
  do not: Level A asserts the page actually *mounts in a browser with no console errors*; Level B
  *drives a multi-step interaction* and asserts the terminal observable.

**The two levels — do NOT conflate them.**

### Level A — liveness smoke GUARDRAIL (v1 doctrine; default for any UI-producing task when a driver exists)

> **Do NOT author a headless-browser guardrail inline.** In v1 the deterministic UI check the skill
> actually emits is the dotnet.md §9 *served-markup* HTTP-body grep. Level A's browser-driver form is
> gated behind a catalogue archetype the sibling unit has not landed yet — until it exists, emit §9
> and **report the Level-A gap**; never hand-roll a Playwright/Cypress guardrail from this section.

The browser-driver generalization of archetype #7 ("probe the running artifact": service → curl;
web UI → headless-browser probe). It asserts **liveness only, never behavior**:
- the page mounts in a headless browser,
- no console errors / unhandled promise rejections on load,
- a **structural selector derived from the plan** (a heading/region/`data-testid` the plan names)
  is present.

Minimal tautology surface — you cannot make a broken page emit zero console errors — so it needs
**no anti-tautology scaffolding**. Be clear-eyed: **Level A does NOT catch behavior** (a Back button
that wipes the form, an unwired Next, a wrong computed total). That is Level B.

This is a **generalization of #7**, not a new tool-specific archetype. The catalogue note that
generalizes #7 to "probe the running artifact (service → curl; web UI → headless-browser probe)" and
the per-driver invocation idioms are **owned by the sibling unit** (catalogue + `stacks/dotnet.md` /
`references/e2e/`), NOT by this skill section. **FLAG-FOR-LEAD** (see the flag block below) — until
that archetype lands, the dotnet.md §9 *served-markup* HTTP-body grep is the strongest UI check the
skill can emit deterministically, and an absent browser driver is surfaced, never scaffolded.

### Level B — behavioral E2E spec (v2 — interaction-flow; inserted task chain, only when warranted)

"Back repopulates the form", "checkout total renders correctly", "complete the wizard" — these
assert **behavior reachable only through the artifact**, so the spec is a real authored test
carrying the **full TDD anti-tautology chain** (`tests-fail-on-current-code` + the `writeScope`
test-exclusion — Step 5's TDD pair). **This is the #78 interaction-flow archetype and is v2** (the
external browser-driver dependency and the flakiest guardrail archetype are out of v1 — roadmap v2
bet #5). Document it; do not emit a Level-B guardrail in v1 — surface it as a human decision /
honest-halt instead (see the v2 flag block).

**Trigger (the load-bearing decision rule).** Insert the author-spec + run-spec chain when the
deliverable carries **regression-bearing logic reachable only through the artifact** — NOT when the
plan prose happens to name an "E2E suite" (plans under-specify tests; that is the entire reason this
skill INSERTS unit-test tasks the plan never mentioned). Decide per exit criterion:
- **UI glue** — does it mount, does the button wire up, is the marker served → **Level A** (and
  §9 served-markup).
- **Logic behind the UI** — a computed total, a validation rule rendered in-page, state carried
  across steps, "complete the wizard" → **Level B** (v2 interaction-flow).

**E2E anti-tautology note (carry into the v2 spec).** A blank Playwright spec "fails on current
code" *trivially* because no server = no page — that satisfies `tests-fail-on-current-code` via
infrastructure, not behavior. The spec must fail **against a running app with the feature absent**
(assert the specific element/text), not against a dead port. The `writeScope` test-exclusion leg
ports unchanged; only the failure-cause leg needs this E2E-specific guidance.

**Durability.** A Level-B spec is a real file at the CI-globbed path (`tests/e2e/*.spec.ts`) that CI
re-runs forever; the guardrail is build-time-only, the spec is permanent coverage. Land it where CI
globs it — an authoring constraint, not a reason to avoid the chain.

### Step 0 second-dimension detection — `$e2eStack` (independent of `$stack`)

E2E tooling is independent of the build stack (a .NET repo can have Playwright). After the Step 0
build-stack table, record **one** probe value `$e2eStack` ∈ { `playwright` | `cypress` | `none` }:
- `Microsoft.Playwright` PackageReference, or `@playwright/test`/`playwright` in `package.json`
  devDependencies, or a `playwright.config.{ts,js}` → `playwright`;
- `cypress` in `package.json`, or a `cypress.config.{ts,js}` → `cypress`;
- otherwise `none`.

Resolve a needed driver with the **same priority ladder** as the test-framework choice (Step 5):
**detected in repo → named in plan → ask the human (interactive `AskUserQuestion`) → honest-halt +
report (unattended); never silently scaffold.** This **resolves the SKILL.md forward-reference** that
previously deferred `$e2eStack` mechanics to "the web-UI verification work" — the detection rule now
lives here. **No driver detected → NO guardrail:** emit a `needsHuman` placeholder and flag it in the
report ("Tasks NN produce browser-rendered output; no E2E driver detected (checked
playwright/cypress) — install one or accept the coverage gap here"). An honest gap beats a fake
green. **The LLM-prompt "does this look right" fallback is explicitly rejected** — it fails the
catalogue demotion gate (deterministic-property, never-alone, echo-judge) and is strictly worse than
no guardrail.

### Step 5 insertion — when each level fires

- **Level A (v1, when `$e2eStack ≠ none` and a task produces browser-rendered UI):** add the
  liveness smoke guardrail to the `build-ui-<screen>` task (the catalogue's #7-generalization
  archetype, once the sibling unit lands it). Until that archetype exists, emit the dotnet.md §9
  served-markup guardrail (the strongest deterministic UI check available) and **report the Level-A
  gap** — the served-markup grep proves the marker is in the body, not that the page mounts
  error-free in a browser.
- **Level B (v2, deferred):** when an exit criterion names a multi-step interaction with
  regression-bearing logic, do NOT emit a guardrail in v1. Insert nothing executable; instead
  surface it in the Step 7 report as a v2-interaction-flow decision (driver choice + the flow's
  steps/selectors) the human must resolve, exactly as an unnamed route/marker is surfaced. When v2
  lands, the inserted chain is `NN-author-e2e-<flow>` (TDD pair) + `NM-e2e-<flow>` carrying the
  interaction-flow guardrail, downstream of the §66 UI task(s) and §64 wiring.

### Step 7 self-review extension

Extend the Step 7.0 UI exit-criteria self-review with the interaction dimension:
- An exit criterion phrased as a **multi-step interaction** ("complete the wizard", "next/back
  navigation", "state carried across steps", "submit and see the confirmation") covered by **only**
  a served-markup guardrail (no Level-B task) is an **under-coverage flag** — §66 proves the first
  screen renders; "complete the wizard" needs the flow driven. Surface it the same way §66 surfaces
  "promised a frontend, built zero UI": name the criterion, state that no task drives the flow, and
  present the v2-interaction-flow decision (or the honest gap) as a blocking item the human resolves.
- An **unspecified flow** (the plan names the outcome but not the concrete steps/selectors) is a
  human decision, surfaced in the report — never an invented interaction script.

> **v2 / sibling-unit FLAG-FOR-LEAD (NOT a v1 skill change — flag, do not implement here).**
> Two pieces of this doctrine live OUTSIDE this skill section and are owned elsewhere:
> 1. **Catalogue + stack archetypes (sibling unit owns `guardrail-catalogue.md` and
>    `stacks/dotnet.md` this batch).** Level A needs a catalogue note **generalizing archetype #7**
>    to "probe the running artifact (service → curl; web UI → headless-browser probe)" and the
>    per-driver headless-probe idiom; Level B (v2) needs a new **interaction-flow** archetype
>    (headless driver, scripted steps on stable selectors, deterministic waits, `finally` teardown,
>    one actionable failure line, explicitly deterministic — no visual prompt-judge). **Flag both to
>    the lead** — do not edit those files from here.
> 2. **The `$e2eStack` harness/CI support and concrete driver invocation** (`references/e2e/<driver>.md`,
>    `playwright install`, the 3-OS CI matrix cost, the readiness-probe loop) is **v2 bet #5**
>    (`docs/plans/03-roadmap.md`) — designed, not built. `references/stacks/ui.md` documents the
>    methodology and the v2 boundary; the concrete Playwright/Cypress stack file is deferred until a
>    SECOND real web-UI plan exists (exactly one does today). Until v2 ships, an absent driver is
>    surfaced (report + honest-halt), never silently scaffolded.
<!-- END ADDED SECTION #41/#78 -->

## Quality bar (verify before declaring done)

- [ ] Stack detected in Step 0; its `stacks/<stack>.md` loaded (or fallback warned if none ships / mixed). `guardrails-patterns.md` read if present.
- [ ] Every emitted task passed the Step 2 over-size split-trigger (no task bundles multiple deliverables, has a wide blast radius, maps 1:1 to a design milestone, or has an expensive retry); any feasibility/self-critique "over-packed"/"~N test refs" signal was carried into sizing and split, not sized 1:1 (#111). Re-checked in the Step 7.0a task-size self-review; any unsplittable over-scoped task is flagged in the report.
- [ ] Every task has ≥ 1 deterministic guardrail; judges passed the demotion gate and are never alone.
- [ ] Every guardrail file opens with its `catches:` line.
- [ ] Every guardrail respects the artifact-ancestry rule (files AND state keys).
- [ ] Any task that writes a downstream-read state key carries the fragment-key-present guardrail.
- [ ] New module/project added to a build descriptor → registration guardrail on the descriptor itself.
- [ ] Abstraction consumed by a later task → cross-module reference guardrail on the consumer.
- [ ] Component injected at a composition root (`IFoo`/`FooImpl` pair → factory/DI/`Program.cs`) → a wiring task (construct + inject `FooImpl` into the production assembler) AND a composition-root guardrail that drives the REAL assembler (observable output, or reflection-on-the-constructed-object with a contrast case — the `Factory_Wires*` shape), NEVER injecting the seam in the guardrail and NEVER relying on terminal whole-suite green to cover wiring (#120).
- [ ] Server/executable plan (entry-point-wiring signal) → a wiring task (entry-point-references-launcher grep) AND a live smoke-test task (start → poll route → assert 200 → stop in `finally`) inserted; the polled route is produced by an ancestor.
- [ ] UI-facing plan (described screen/page/browser-served component) → a `build-ui-<screen>` task per surface (alongside its backend, not instead of it) AND UI-presence guardrails: asset-exists (scoped to the page/asset file) + a served-markup assertion EXTENDING the §8 smoke-test (body contains a known UI string, not just HTTP 200), both deterministic — no prompt-judge on visual quality. Exit-criterion naming a UI action ⇒ a task builds that UI, or the Step 7.0 self-review failure is reported.
- [ ] Implementation/inheritance checks use the stack file's structural regex, not a bare keyword grep.
- [ ] Every file-content guardrail is scoped to the one file the task owns (no project-tree greps).
- [ ] Inserted test-author tasks carry the right TDD "red" for the type under test (#155): a BEHAVIORAL type → the task also writes minimal `NotImplementedException` stubs, its `writeScope` covers test + stub file(s), and its guardrails are `build-passes` + `tests-fail-on-stubs`; a DATA MODEL → collapsed to one task (reason stated) or, if split, `tests-fail-on-current-code` + a STRUCTURAL `[Fact]`/`[Theory]` covers-key-behaviors check. Implementation tasks declare a `writeScope` that EXCLUDES the test file but TARGETS the stub file(s) (TDD test-exclusion — replaces the captureHashes/restoreOnRetry/tests-untouched triad).
- [ ] (#154) Every generated test-author `action.prompt.md` carries a **Scope boundary (harness-enforced)** paragraph after the target-file-path statement: it names the exact allowed path(s) (test + stub), states the harness's post-action `git diff` membership check rejects out-of-scope edits, states an out-of-scope edit fails the task and consumes a retry, and redirects an upstream missing-symbol compile error to `{"needsHuman": …}` rather than editing that file.
- [ ] A test-author behavior that needs a production injection seam (a fake/double injected into a type with no injection point) → an upstream `add-<component>-<seam>-seam` task (pure structural production change, build + a structural seam-exists check, TDD-exempt) the test-author task `dependsOn`; the seam was NOT left to the test task to invent or to its `needsHuman` escape (#84).
- [ ] A task that fans out over an external/unknown-size set (crawl, recursive glob, API listing) → modeled as a scripted-ETL `script` action (volume off the turn budget), NOT an agent-per-item loop; discover-size-first probe added where the count is unknown; bulk-capture split from bounded per-item curation (#100).
- [ ] Step 7.0b deliverable-coverage self-review ran: every numbered design deliverable (placement-table row, top-level `§`-section, "what's-asked" item) maps to ≥1 task; any body/`§`-deliverable lacking a milestone home was flagged, not silently dropped; uncovered deliverables are blocking decisions in the report; a `guardrails-review` coverage probe is surfaced (#110).
- [ ] A parallel plan (≥2 leaf tasks or any fan-in) carries a non-empty **`<plan>/guardrails/`** folder (the Terminal Gate) with **≥1 REAL integration-set re-run** — a whole-repo build / full suite / union-safe conditional invariant — NOT a tautological `exit 0` (`validate` enforces this content bar as **GR2028**). There is **NO terminal `integrationGate: true` sink task**; a lingering `integrationGate: true` in any `task.json` is the BLOCKER (a **GR2029** hard error), NOT its absence. The folder's `scope: "integration"` union-guardrail is a **union-safe CONDITIONAL invariant** (conflict-marker-free / "if contribution X present, verify it's real"), NOT the full build or whole suite: a full build (`01-solution-builds`) / whole-suite test (`02-all-tests-pass`) placed in the terminal folder stays **LOCAL** (no `scope` key) — marking it `scope: "integration"` is the #125 anti-pattern (it red-halts correct intermediate unions where downstream TDD tasks have not run yet, #165). (`scope: "integration"` itself is unchanged — the per-union re-verify tag, SSOT §4.3.)
- [ ] Every `dependsOn` edge has a stated justification; no prose-order-only edges.
- [ ] All prompt actions contain the harness-contract block.
- [ ] `promptRunners` present iff any `.prompt.md` exists.
- [ ] Every task has a unique minted `stableId` by default (matching `^[a-z0-9][a-z0-9._-]*$`); on a regeneration, continued tasks reuse their prior id.
- [ ] `guardrails validate` exits 0 (or its absence is loudly reported).
- [ ] `diagram.md` generated via `guardrails graph` and its path reported (block embedded inline); the `Diagram (interactive): <link>` line `guardrails graph` itself printed for `diagram.html` (a ready-to-click OSC 8 hyperlink, issue #249 — relayed verbatim, never hand-built from a shell `pwd`) printed as the report's last line.
- [ ] On fresh generation: `guardrails lock` written (a `guardrails.baseline`). On regeneration: a BASE baseline existed or was established first, and `guardrails merge --apply` succeeded with conflicts resolved beforehand.
- [ ] Output explicitly presented as a draft for human review.
<!-- BEGIN ADDED QUALITY-BAR ITEMS (auto-merge friendly) -->
- [ ] (#94/#204) Every turn-expensive prompt task (integration/smoke/e2e + in-process harness, unfamiliar-SDK discovery, terminal aggregation/wiring, OR integrates with/extends/describes a same-plan sibling's not-yet-landed implementation) carries a per-task `maxTurns: 75` override (`task.json action.maxTurns` or prompt frontmatter); other prompt tasks left at the default; a shared-harness task inserted when ≥2 tasks need the same unfamiliar-SDK setup; the bumps + insertion reported (Step 4a). (#203) A task referencing an earlier-wave sibling's code also gets durable-marker + architecture-caveat prompt text (Step 6) — the two are companion fixes for the same situation, not independent bullets.
- [ ] (#116) Every author-tests task that builds a real git repo reuses a Windows-safe shared `TempGitRepo` fixture (strips read-only before delete, recreates `git rm`/`git mv`-pruned dirs, rolls back via `git reset --hard`, normalizes `core.autocrlf`) OR carries the Windows-Git portability directive; the fixture is authored once and reused, not re-discovered per task (Step 5a; `stacks/dotnet.md §11`).
- [ ] (#101) Every task whose primary deliverable is a file in a NEW `.claude/` subdirectory has a directory-seed task (writes a `.gitkeep`) or a `## Pre-conditions` note before it, plus a `01-dir-seeded.ps1` guardrail asserting the subdir exists; the seed and affected path reported (Step 5b).
- [ ] (#87) No emitted task updates ≥2 `.claude/skills/<X>/` directories — multi-skill milestones split into one `NN-update-<skill>-skill` task per directory (each with a directory-narrowed `writeScope`), with golden-example regeneration and round-trip verification as their own downstream tasks `dependsOn` the skill updates (Step 2c).
- [ ] (#41/#78) `$e2eStack` recorded in Step 0 (playwright | cypress | none); for a UI-producing task, Level A (v1 liveness smoke) is added when a driver exists (else the §9 served-markup guardrail is emitted and the Level-A gap reported), an absent driver is surfaced/honest-halted (never scaffolded), Level B (v2 interaction-flow) is documented and surfaced as a v2 decision (never emitted in v1), and a multi-step-interaction exit criterion covered by only served-markup is flagged under-covered in Step 7 (Step 4b/5c; `references/stacks/ui.md`).
<!-- END ADDED QUALITY-BAR ITEMS -->
