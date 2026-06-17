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

## Step 2 — Size the tasks

A task is right-sized when ALL hold:

1. **One verifiable outcome.** One primary artifact/behavior a guardrail can check.
   If describing the outcome needs "and", consider splitting.
2. **Guardrail-boundary rule (load-bearing):** split exactly where verification
   changes character. "Implement parser + write its tests" splits because the
   test-task's guardrails (tests exist, tests-fail-on-stub) differ from the
   parser-task's (tests pass). Conversely "create the file AND register it in the
   index" stays one task if a single guardrail checks both.
3. **One-session rule:** a competent agent finishes it in one focused session
   (≈ ≤ 30–45 min of agent work).
4. **Retry-cheapness:** a failed guardrail re-runs the whole action. If a one-line
   fix would redo an hour of work, the task is too coarse.
5. **TDD default for code deliverables.** When the primary deliverable is code (a
   library, feature, service behavior, or algorithm), the guardrail-boundary rule (rule
   2) almost always fires: a test-author task's guardrail (`tests-fail-on-current-code`)
   and the implementation task's guardrail (`specific-tests-pass`) are different in
   character. Default to splitting into two consecutive tasks:

   1. `NN-author-tests-<feature>` — writes tests encoding the behavior BEFORE it exists
   2. `NM-implement-<feature>` — makes those tests pass without modifying them

   Collapse to a single task only when (a) tests for this behavior **already exist** in
   the repo, or (b) the behavior is too simple to have meaningful unit tests — state the
   reason explicitly in the task description or breakdown report. When in doubt, split: the test-author
   task is cheap and its `tests-fail-on-current-code` guardrail is the strongest
   anti-tautology check the skill has.

Heuristic: a typical feature plan yields **5–15 tasks**. TDD splitting doubles code
tasks (each code item becomes two tasks); this does not count against the threshold.
Under 3 or over 25 tasks after applying TDD → re-examine, and tell the user why if
it stands.

## Step 3 — Determine the DAG (`dependsOn`)

Edge sources, in priority order:
- **(a) artifact dependency** — task B consumes a file/state key task A produces;
- **(b) guardrail dependency** — B's guardrail executes A's artifact (tests, scripts);
- **(c) explicit plan ordering** ("after", "requires").

**Default to the sparsest correct DAG.** Plan prose order alone is NOT an edge —
parallelism is free, false edges serialize the run. Record a one-line justification
per edge (in the task description or the breakdown report). Verify acyclicity.

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
  retry feedback).
- "All tests pass" appears ONLY on a terminal integration task.

**Route through these doctrine checks every task (the decision tree's newer leaves):**
- **State output** — does this task's action write a state key (to `GUARDRAILS_STATE_OUT`)
  that a downstream task reads (via `GUARDRAILS_STATE_IN`)? Add the fragment-key-present
  guardrail (catalogue → state-output leaf): read `GUARDRAILS_STATE_FRAGMENT`, parse JSON,
  assert the key non-null + non-empty (+ allowed-set if a downstream task branches on it).
  A task's action may write state ONLY under its own task id — single-writer-per-key is enforced
  (SSOT §6.2); writing under another task's id or a shared key rejects the fragment and fails the
  attempt.
- **Build-descriptor registration** — does the task add a module/project to a build
  descriptor (a `.csproj` to a `.slnx`)? Add the stack file's registration guardrail on the
  DESCRIPTOR, not just the new file (`stacks/dotnet.md §1`). A descriptor build passes with
  an unregistered project — file-exists + build-passes do NOT cover this.
- **Cross-module reference** — does this task create an abstraction a later task must
  consume? Add the stack file's reference-chain guardrail on the CONSUMER's project file
  (`stacks/dotnet.md §2`). Builds pass independently, so without this an agent can define a
  local copy of the interface and pass.
- **Structural impl / keyword match** — any "implements/extends/declares" check uses the
  stack file's declaration regex (`stacks/dotnet.md §3`), never a bare type-name grep.
- **Grep scope** — every file-content guardrail is scoped to the one file this task owns
  (catalogue → grep-scope contamination anti-pattern; `.NET` traps in `stacks/dotnet.md §5`).

## Step 5 — Insert guardrail-enabling tasks (the generative step)

For every selected guardrail whose precondition doesn't exist yet, generate the
upstream task that creates it:

- Code task and tests do not yet exist → insert `NN-author-tests-<feature>` BEFORE the
  implementation task (the TDD default in Step 2 means this fires for most code tasks).
  Three things follow automatically:

  **Test-author task guardrails.** `tests-fail-on-current-code` (archetype #8) is
  required. Keep `tests-build` unless the new tests reference not-yet-existing symbols (a
  new type, method, or constant) that make the project un-compilable against current code —
  in that case drop it, since a build guardrail that always fails due to missing symbols
  adds noise without signal. When tests exercise only existing API surface (a CLI flag, a
  file path, a response code), keep `tests-build`.

  **`tests-untouched` guardrail on every implementation task** that has an upstream
  test-author task. Prevents the agent from making tests pass by editing them instead of
  fixing the implementation. Uses **harness-computed content hashes** (issue #46): the
  test-author task DECLARES the files to hash; the harness records their SHA-256 into state
  **in code** — the agent never runs `git hash-object` or any shell command, so a scoped
  `allowedTools` (e.g. `Bash(dotnet *)` only) can never block the capture (the failure mode
  that made the old agent-computed pattern hang on `needsHuman`). Two parts:

  **Part 1 — test-author `task.json`: declare `captureHashes`.** List every test file the
  task authors, workspace-relative. After the action succeeds (and before guardrails run),
  the harness computes each file's SHA-256 (uppercase hex, over raw bytes) and merges it into
  state under `{ "<task-id>": { "fileHashes": { "<path>": "<hex>" } } }`. No prompt
  instruction and no shell are involved. If a declared file is missing after the action, the
  attempt fails with an actionable message naming it.

  ```jsonc
  {
    "description": "Author failing tests for <feature>",
    "dependsOn": ["..."],
    "stableId": "…",
    "captureHashes": ["tests/MyProject/MyFeatureTests.cs"]
  }
  ```

  Because the harness (not the agent) writes the hashes, **no `state-fragment-written`
  guardrail is needed** on the test-author task: a missing declared file already fails the
  attempt, and a present file is always recorded.

  **Part 2 — implementation task `guardrails/NN-tests-untouched.ps1`** reads the recorded
  hash from `GUARDRAILS_STATE_IN` and recomputes with `Get-FileHash` (a pwsh cmdlet — a
  guardrail script runs via the interpreter, NOT the agent sandbox, so it always works; no
  git dependency):

  ```powershell
  # catches: "making tests pass" by editing the tests instead of the implementation;
  #          reads the SHA-256 hashes the harness recorded for the upstream test-author task
  $state = Get-Content $env:GUARDRAILS_STATE_IN -Raw | ConvertFrom-Json
  $storedHashes = $state.'NN-author-tests-FEATURE'.fileHashes
  if (-not $storedHashes) {
    Write-Output "State key 'NN-author-tests-FEATURE.fileHashes' missing — was captureHashes declared on the test-author task?"
    exit 1
  }
  $failures = @()
  foreach ($file in $storedHashes.PSObject.Properties.Name) {
    $stored = $storedHashes.$file
    if (-not (Test-Path $file)) { $failures += "$file was deleted by the implementation task"; continue }
    $current = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash
    if ($current -ne $stored) { $failures += "$file was modified (expected $stored, got $current)" }
  }
  if ($failures) { Write-Output ($failures -join "; "); exit 1 }
  exit 0
  ```

  Replace `NN-author-tests-FEATURE` with the upstream test-author task's folder name. The
  `foreach` handles multiple test files. (`Get-FileHash` and the harness both emit uppercase
  SHA-256 hex; PowerShell `-ne` is case-insensitive regardless, so the comparison is exact.)

  **Restore-on-retry (harness behavior — issue #51).** Captured files are not just hashed; the
  harness snapshots their authored bytes and **restores them to baseline before each retry** of a
  downstream task. So if an implementation task edits a test file (to force a tests-pass guardrail
  green), `tests-untouched` catches it AND the next attempt starts from the pristine test file —
  no permanent dead-end. The implementation action prompt should therefore say plainly: **do not
  edit the authored tests; make them pass by fixing the implementation; if the authored tests are
  genuinely wrong or incompatible, emit `{"needsHuman": "<why>"}` rather than changing them.** The
  retry feedback the harness composes already says this on a `tests-untouched` failure.

  **Action prompt for test-author tasks.** The `## Task` section must tell the agent: (a) the
  exact test file path(s) and any category/trait convention the repo uses; (b) the tests MUST
  fail against the current code — this is intentional, not a mistake; (c) do NOT implement the
  behavior, only the tests. It does NOT need to compute or write any hash — `captureHashes`
  handles that in the harness. See `references/example-breakdown.md` for the complete worked
  `action.prompt.md`.
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
  detection mechanics land with the web-UI verification work — until then, an absent driver
  is surfaced (report + honest-halt), never silently scaffolded.
- Guardrail "schema validates" and no schema exists → insert an author-schema task
  (guardrails: schema file exists + parses + a known-bad sample FAILS validation).
- Guardrail "port answers" → ensure an ancestor produces the launch script, or the
  guardrail owns start/stop itself with a timeout.
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
- **`stableId` — mint one per task by default.** It is the identity key the regeneration merge
  (§11) uses to track a task across renumber/rename. The schema marks it OPTIONAL (a task without
  one falls back to its folder name for identity), but the breakdown mints one per task so
  regeneration can preserve human edits. Mint once; never reuse for a different task; duplicates
  fail validation (**GR2010**). **Format (GR2011):** a `stableId` must match
  `^[a-z0-9][a-z0-9._-]*$` — lowercase alphanumeric, may contain `. _ -`, no
  colon/slash/whitespace/uppercase. Mint short lowercase base36 tokens (e.g. `k3f9a1`, `q7m2zd`).
- Every **prompt action** opens with the harness-contract header block, verbatim:

  ```markdown
  ## Harness contract (do not remove)
  - Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
    the appended sections; write ONLY new/changed keys as a JSON object to
    GUARDRAILS_STATE_OUT.
  - If a previous-attempt feedback section is appended, this is a RETRY: fix those
    specific failures; do not start over.
  - If you cannot proceed without a human decision, write
    {"needsHuman": "<question>"} to the state-out path and stop.

  ## Task
  <the actual instruction: exact file paths, and completion criteria that MATCH this
  task's guardrails>
  ```

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
- `state/seed.json` only if the plan implies initial shared state (input paths,
  names, configuration the tasks read).
- Scripts: prefer the workspace's native platform; note any interpreter requirement
  beyond the defaults in `guardrails.json: interpreters`.

## Step 7 — Self-validate and report

1. Run `guardrails validate <folder>`. Fix and re-run until exit 0 (or report that
   validation was skipped and why).
2. Optionally run `guardrails plan <folder>` and sanity-check the waves against your
   DAG intent.
3. Once validation passes, run `guardrails graph <folder>` to generate
   `<folder>/diagram.md` (a Mermaid `flowchart TD` of the task/guardrail DAG — a
   generated artifact, never hand-edited; see `references/schemas.md`). Then run
   `guardrails lock <folder>` to write the committed `guardrails.baseline` BASE manifest, so a
   future regeneration can preserve any guardrails the human edits in the meantime (§11).
4. Emit the **breakdown report**: task table (id, action kind, guardrails with
   archetype numbers, dependsOn), the inserted-task list with justifications, edge
   justifications, and any flagged non-executable plan content. **Surface every decision
   the human should confirm** — chief among them any test-framework or E2E-driver choice:
   state which was used and why (detected in repo / named in the plan / asked via
   `AskUserQuestion` / left as a needs-human halt). A wrong framework poisons every
   downstream test task, so it must never be buried. Then **embed the generated Mermaid
   block inline** (paste the ```mermaid``` fence from `diagram.md`) so the human sees the
   DAG in chat, and **state the `<folder>/diagram.md` path** explicitly so they can render
   it in GitHub or VS Code.
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

## Quality bar (verify before declaring done)

- [ ] Stack detected in Step 0; its `stacks/<stack>.md` loaded (or fallback warned if none ships / mixed). `guardrails-patterns.md` read if present.
- [ ] Every task has ≥ 1 deterministic guardrail; judges passed the demotion gate and are never alone.
- [ ] Every guardrail file opens with its `catches:` line.
- [ ] Every guardrail respects the artifact-ancestry rule (files AND state keys).
- [ ] Any task that writes a downstream-read state key carries the fragment-key-present guardrail.
- [ ] New module/project added to a build descriptor → registration guardrail on the descriptor itself.
- [ ] Abstraction consumed by a later task → cross-module reference guardrail on the consumer.
- [ ] Implementation/inheritance checks use the stack file's structural regex, not a bare keyword grep.
- [ ] Every file-content guardrail is scoped to the one file the task owns (no project-tree greps).
- [ ] Inserted test-author tasks include tests-fail-on-current-code; implementation tasks guard tests-untouched.
- [ ] Every `dependsOn` edge has a stated justification; no prose-order-only edges.
- [ ] All prompt actions contain the harness-contract block.
- [ ] `promptRunners` present iff any `.prompt.md` exists.
- [ ] Every task has a unique minted `stableId` by default (matching `^[a-z0-9][a-z0-9._-]*$`); on a regeneration, continued tasks reuse their prior id.
- [ ] `guardrails validate` exits 0 (or its absence is loudly reported).
- [ ] `diagram.md` generated via `guardrails graph` and its path reported (block embedded inline).
- [ ] On fresh generation: `guardrails lock` written (a `guardrails.baseline`). On regeneration: a BASE baseline existed or was established first, and `guardrails merge --apply` succeeded with conflicts resolved beforehand.
- [ ] Output explicitly presented as a draft for human review.
