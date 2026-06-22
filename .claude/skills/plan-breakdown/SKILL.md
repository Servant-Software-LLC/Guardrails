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
- "All tests pass" appears ONLY on a terminal integration task.
- Mark build / whole-suite test guardrails with `scope: "integration"` in their sidecar
  JSON (deterministic) or prompt frontmatter (SSOT §4.3). The run's integration-guardrail
  set = the union of all `scope: "integration"` guardrails (typically the whole-repo build
  + the full test suite); the harness re-runs that set at every union point and on the
  terminal gate's merged HEAD. Leave a task-local guardrail at its `"local"` default.
- A terminal integration task must declare `integrationGate: true` in its `task.json` — it
  marks the terminal whole-repo integration gate, the final soundness boundary run once on
  the fully merged plan-branch HEAD (SSOT §3.3). Validation enforces this: a plan with ≥2
  leaf tasks or any fan-in must declare **exactly one** `integrationGate: true` sink
  (**GR2017**), and that sink must carry **at least one** `scope: "integration"` guardrail
  (**GR2018**) — an empty gate verifies nothing.

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

  **`writeScope` test-exclusion — the deterministic TDD test-protection (SSOT §3.4).**
  Tests are protected by (i) physical worktree isolation and (ii) the harness's
  **write-scope check**: a deterministic, read-only `git diff` membership test that runs
  after the action and before the task's own guardrails. It asserts every path the task's
  diff adds/modifies/deletes/renames is inside the task's declared `writeScope`; an
  out-of-scope edit is a guardrail-class failure (retry with feedback naming the offending
  paths, eventual `needs-human`). The check **never reverts** the in-scope work, and it
  **writes nothing** — it only inspects the diff. Set the two scopes so the implementation
  cannot author the tests:

  - **Test-author `task.json`: declare a `writeScope` covering the test file(s) it authors.**
    List each test file (or its directory) workspace-relative — the surface this task is
    permitted to write.

    ```jsonc
    {
      "description": "Author failing tests for <feature>",
      "dependsOn": ["..."],
      "stableId": "…",
      "writeScope": ["tests/MyProject/MyFeatureTests.cs"]
    }
    ```

  - **Implementation `task.json`: declare a `writeScope` that EXCLUDES those test files.**
    Scope it to the implementation surface (e.g. `src/MyProject/`) and do NOT list the test
    files. The write-scope check then deterministically enforces "the implementation may not
    write the tests" — an edit to a test file falls outside the implementation's scope and
    fails the check. This is the **replacement** for the removed
    `captureHashes`/`tests-untouched`/`restoreOnRetry` triad; no hashing, no restore, no
    downstream `tests-untouched` guardrail.

    ```jsonc
    {
      "description": "Implement <feature> so the tests pass",
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
  advisory context (the deterministic check is the gate). The test-author `## Task` section
  must tell the agent: (a) the exact test file path(s) and any category/trait convention the
  repo uses; (b) the tests MUST fail against the current code — this is intentional, not a
  mistake; (c) do NOT implement the behavior, only the tests. The implementation `## Task`
  must say plainly: **do not edit the authored tests; make them pass by fixing the
  implementation; if the authored tests are genuinely wrong or incompatible, emit
  `{"needsHuman": "<why>"}` rather than changing them** — an out-of-scope edit to a test file
  fails the write-scope check and burns a retry. Neither task needs to compute or write any
  hash. See `references/example-breakdown.md` for the complete worked `action.prompt.md`.
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
  detection mechanics land with the web-UI verification work — until then, an absent driver
  is surfaced (report + honest-halt), never silently scaffolded.
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
     then the wiring task makes it green). Mark the guardrail `scope: "integration"` when it drives
     the whole assembled feature. When the plan names no concrete observable to assert on, surface
     it in the breakdown report (Step 7) as a decision the human must confirm — do not invent one.
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
- **`stableId` — mint one per task by default.** It is the identity key the regeneration merge
  (§11) uses to track a task across renumber/rename. The schema marks it OPTIONAL (a task without
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
- **`integrationGate` (optional, default `false`, SSOT §3.3)** — set `true` on the **terminal
  whole-repo integration gate**, the final soundness boundary run once on the fully merged
  plan-branch HEAD. A plan with ≥2 leaf tasks or any fan-in must declare **exactly one**
  `integrationGate: true` sink (**GR2017**), and that sink must carry **at least one**
  `scope: "integration"` guardrail (**GR2018**). A single linear chain with no fan-in may omit it.
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
   downstream test task, so it must never be buried. **If the plan was UI-facing**, state
   the outcome of the Step 7.0 exit-criteria self-review: each UI surface, the
   `NN-build-ui-<screen>` task that builds it, its UI-presence guardrail (asset-exists +
   served-markup string asserted), and the known UI string the served-markup check greps
   for — or, if a screen could not be built from the plan, the blocking self-review
   failure. Then **embed the generated Mermaid
   block inline** (paste the ```mermaid``` fence from `diagram.md`) so the human sees the
   DAG in chat, and **state the `<folder>/diagram.md` path** explicitly so they can render
   it in GitHub or VS Code. Finally, as the **last line of the report**, print a clickable
   link to the interactive viewer `<folder>/diagram.html` (the pan/zoom/fullscreen companion
   `guardrails graph` wrote in step 3) so the reviewer can open it without hunting for it.
   Emit it as an **OSC 8 hyperlink** whose visible text is the absolute `file://` URL — so a
   capable terminal (Windows Terminal, iTerm2, VS Code) renders it clickable and any other
   shows the raw, still-actionable URL. Use the same escape shape `guardrails run` uses for
   its `Logs` link (`RunCommand.Hyperlink`): `ESC]8;;<uri>ESC\<text>ESC]8;;ESC\`, with both
   `<uri>` and `<text>` set to `file://` + the absolute path to `diagram.html`. For example:
   `Diagram (interactive): file:///C:/path/to/<plan-folder>/diagram.html`. Build the URL from
   the absolute folder path so it resolves regardless of the shell's working directory.
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
- [ ] Inserted test-author tasks include tests-fail-on-current-code; implementation tasks declare a `writeScope` that EXCLUDES the test files (TDD test-exclusion — replaces the captureHashes/restoreOnRetry/tests-untouched triad).
- [ ] A test-author behavior that needs a production injection seam (a fake/double injected into a type with no injection point) → an upstream `add-<component>-<seam>-seam` task (pure structural production change, build + a structural seam-exists check, TDD-exempt) the test-author task `dependsOn`; the seam was NOT left to the test task to invent or to its `needsHuman` escape (#84).
- [ ] A task that fans out over an external/unknown-size set (crawl, recursive glob, API listing) → modeled as a scripted-ETL `script` action (volume off the turn budget), NOT an agent-per-item loop; discover-size-first probe added where the count is unknown; bulk-capture split from bounded per-item curation (#100).
- [ ] Step 7.0b deliverable-coverage self-review ran: every numbered design deliverable (placement-table row, top-level `§`-section, "what's-asked" item) maps to ≥1 task; any body/`§`-deliverable lacking a milestone home was flagged, not silently dropped; uncovered deliverables are blocking decisions in the report; a `guardrails-review` coverage probe is surfaced (#110).
- [ ] A parallel plan (`maxParallelism` > 1) declares exactly one `integrationGate: true` sink carrying a `scope: "integration"` guardrail; build / whole-suite guardrails are marked `scope: "integration"`.
- [ ] Every `dependsOn` edge has a stated justification; no prose-order-only edges.
- [ ] All prompt actions contain the harness-contract block.
- [ ] `promptRunners` present iff any `.prompt.md` exists.
- [ ] Every task has a unique minted `stableId` by default (matching `^[a-z0-9][a-z0-9._-]*$`); on a regeneration, continued tasks reuse their prior id.
- [ ] `guardrails validate` exits 0 (or its absence is loudly reported).
- [ ] `diagram.md` generated via `guardrails graph` and its path reported (block embedded inline); a clickable OSC 8 link to `diagram.html` (visible text = the absolute `file://` URL) printed as the report's last line.
- [ ] On fresh generation: `guardrails lock` written (a `guardrails.baseline`). On regeneration: a BASE baseline existed or was established first, and `guardrails merge --apply` succeeded with conflicts resolved beforehand.
- [ ] Output explicitly presented as a draft for human review.
