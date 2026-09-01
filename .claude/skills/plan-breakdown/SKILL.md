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
  `docs/plans/02-schemas-and-contracts.md`), including the **waved nested layout** (§14).
- `references/example-breakdown.md` — a complete worked breakdown including an
  inserted task, plus a negative example. Read when in doubt about output shape.
- `references/example-breakdown-waved.md` — the worked WAVED breakdown (2 ordered stages →
  nested `<plan>/<wave>/…` layout), the JIT staged-breakdown flow, and its closing report.
  **Read alongside Step 9 whenever the plan is authored as ordered stages.**

## Step 0 — Preconditions

> **Interactive Charter `.charter.md` input — check this FIRST (#390–393):** if the input filename ends
> **`.charter.md`** (or — only when ATTENDED — a `.md` whose **column-0 `:::` blocks you confirm are
> Charter**, not a Mermaid `classDef` or a fenced example), run **Step 0c** (discover `charter-format` → gate
> the format-version marker → interpret `:::` → fold a resolved `:::question` / surface an open one) BEFORE
> the preconditions below, then continue with the interpreted content. A plain `.md` (no confirmed `:::`)
> skips Step 0c (existing path, unchanged). Step 0c needs the Skill tool AND — for its prompts — an attended
> human; the headless/autonomous path consumes Charter's flattened `handoff` markdown and never triggers it.
>
> **That flattened path is Step 0d's (#500).** A plain `.md` — including every headless/autonomous run and
> every JIT between-wave breakdown — runs **Step 0d**: scan for Charter's delegated-decision markers and,
> if any are present, SETTLE and RECORD them instead of absorbing them as prose. It needs no Skill tool, no
> `charter-format` and no attended human. **The two are mutually exclusive: `$charter = true` ⇒ Step 0d
> does not run** (Step 0c rule 5 owns those decisions); every other input reaches Step 0d. A plan with no
> markers is untouched — no `decisions.md`, no preflight, no report line, byte-identical to a pre-#500
> breakdown.

1. Resolve the plan path. If the file doesn't exist, stop and say so. The `<plan-name>/` task
   folder is generated **beside the source `.md`** by default; a repo that prefers one consolidated
   footprint MAY instead keep plan folders under a `.guardrails/` home (the same optional home
   `guardrails-patterns.md` documents). Post-#266 the location no longer affects runnability, so this
   is aesthetic — the default stays beside the `.md` (issue #275).
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
     `dotnet test`** in the preflight — that hits the #165/#176 compile-coupling trap (Step 5). **The
     plan-wide trait you introduce here is for THIS `!=` exclusion and nowhere else (#455)** — it is not
     the filter for the task-level `tests-pass` / `tests-fail-on-stubs` guardrails, however natural it
     will look by the time you reach Step 4 (see Step 4's task-level-`--filter` rule). Record ONE
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
8. **Decide FLAT vs WAVED (the layout fork) — set `$waved` (#254).** Is the plan authored as
   **ordered STAGES**, each building on the *materialized artifacts* of the prior stage? The tells:
   explicit "Wave 0..N" / "Stage 1..N" / "Phase 1..N" headings whose later stages say **"builds on
   Stage N-1's output"**, reference **real file paths / signatures a prior stage produces**, or are
   **undesignable up front** because their evidence points at artifacts that don't exist until an
   upstream stage runs. If yes → `$waved = true`: the breakdown emits the **nested layout** (Step 9),
   not the flat `tasks/` layout. If the plan is a single stage / a plain feature (no staged milestones
   whose downstream tasks depend on upstream *materialization*) → `$waved = false`: the flat layout
   (Steps 1–8), **unchanged**. **Do NOT wave a flat plan** — fine-grained parallelism is a task DAG
   inside ONE wave, not multiple waves (waves are the COARSE ordering for stages whose downstream
   tasks can't be authored until the upstream is real; a wave barrier destroys cross-wave parallelism,
   SSOT §14 C5). When `$waved`, Steps 1–8 still run — **once per wave** — but Step 9 governs the
   layout, the wave gates, the wave-qualified identity, and the JIT staged-breakdown mode; read it
   before proceeding.
9. **Detect whether model tiering is CONFIGURED — set `$tiering`. Default: NOT configured (#225).**
   Model tiering is an **opt-in the CONFIG declares**; the breakdown never turns it on by itself. Set
   `$tiering = configured` only when the `guardrails.json` that will govern this plan **already**
   carries tiering metadata — either
   - a **`routing` block on any `promptRunners.<name>`** (the #224 provider-registry surface: the
     per-model guidance/tags saying what work that model should take on), or
   - an existing top-level **`tiering`** block.

   Read it from the config actually in play: the plan folder's own `guardrails.json` on a
   regeneration (substep 2 → Step 8), or a repo / `.guardrails/` config this plan will reuse. A
   **fresh** breakdown that authors `guardrails.json` from scratch has neither ⇒ `$tiering =
   not-configured` — which is the case for essentially every plan today, and is exactly the
   single-model default the gate protects.

   There is exactly ONE other way in: **the plan itself explicitly instructs the breakdown to author
   per-model routing** (it names the runners and what work each should take). That trigger is
   **explicit-only** — never infer it from a plan that merely sounds complex, mentions a model name,
   or configures a second runner with **no** `routing` block. **When in doubt, `not-configured`:** a
   missed tag is the status quo (nothing routes on a tier in this stage anyway), while a tag emitted
   against a single-model config breaks the byte-identical guarantee for a user who never asked for
   tiering. `$tiering` gates **Step 4c** (classification), the **Step 6** emission, and the **Step
   7.4** report lines — read Step 4c before emitting anything tier-shaped.

## Step 1 — Parse the plan into candidate work items

Read the whole plan. Extract numbered steps, deliverable-shaped headings, acceptance
criteria, "done when" language, and dependency words ("after", "requires", "once X
exists"). Build a scratch table:

| item | deliverable artifact(s) | completion evidence available in the plan | hinted deps |

**Charter input (`$charter`, Step 0c):** if the plan was a `.charter.md`, you already interpreted its
`:::` blocks and gated the format version in Step 0c — build this table from the *interpreted* content: a
resolved `:::question`'s folded `answer` is a **settled decision** (an input/constraint, not a work item),
an open one was already asked (`AskUserQuestion`) or became an agent-needs-human task, and the
`:::note/warn/comparison/diagram/diff/custom-html` blocks ride along as **context/rationale** for the rows
they inform. Everything else on this page is unchanged.

**Flattened Charter input (`$delegated`, Step 0d):** if the 0d.1 scan found delegated-decision markers,
each id you settled in 0d.3 is a **settled decision** — an input/constraint on this table, never a work
item and never a row of its own. Its blockquote's prose (the question, the options, the author's lean)
rides along as rationale for the rows it informs, exactly like a folded `:::question` `answer` above. When
`$delegated` is empty this paragraph does nothing.

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

   **The trigger is AUTHORSHIP, not how singular the outcome sounds.** Keyed on *"the primary
   deliverable is code"*, this rule walks straight past a **composition-root / wiring** task: "wire X
   into Y" reads as **one** outcome, so a breakdown authors ONE task that writes both the wiring tests
   and the wiring itself. Generalise it:

   > **If a task authors BOTH the tests and the implementation those tests exercise, it MUST split —
   > regardless of how singular the outcome sounds.**

   The reason is mechanical, and say it rather than re-arguing the case each time: with no upstream
   test-author task there is **no TDD RED half**, so the only census available is the **FORWARD** one
   (`-ne 'Passed'`) — and *a forward census cannot see a body that can never fail*. **Measured, on a
   plan whose entire purpose was catching exactly this class of defect:** such a task's guardrails could
   not distinguish a wired implementation from an unwired one. A test file carrying all five pinned
   method names — four with `Assert.True(true)` bodies and one making a real call — exited **0**; the
   one real call proved nothing, because the production method returns early for the fixture's shape,
   so it passed against a **completely unwired** implementation. The split also restores the gate the
   collapsed task cannot have at all: a `writeScope` that **EXCLUDES the test file** (Step 5's TDD
   test-exclusion is the deterministic test-protection, and it is meaningless when one task owns both).
   This closes the case that had no name; it does not disturb the three collapse criteria below — (a)
   and (b) because the task then authors no tests at all, (c) because a data model's collapse is
   already a NAMED exemption carrying its own weaker-anti-tautology note.

   **Companion rule for the split's downstream half — the DECLARED EXEMPTION.** Once split, the
   test-author task's red census demands each enumerated behaviour be observed `Failed`. When the census
   would demand a test be red that a **CORRECT implementation leaves green**, that test is a **declared
   exemption, not a dropped row**. In the measured case the discriminator — *"a sound input does NOT
   halt"* — legitimately passes against the unwired code, so demanding it be red would demand a correct
   implementation fail. It stays in the manifest carrying an **`Expect='Executed'`** marker (present in
   the runner's result file and not skipped) with the **structural reason stated in the guardrail
   header** — never silently omitted, because an undeclared omission is indistinguishable from an
   oversight. Most rows exempt means you have a forward census wearing the red one's name; that is the
   signal the split above was the thing actually needed. (Catalogue → "The declared exemption";
   `stacks/dotnet.md §4.4` for the manifest shape.)

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
  bounded. **Turn-budget lowers this threshold sharply (#378).** For a task already near the `maxTurns`
  ceiling, the blast-radius threshold DROPS — flag at **`writeScope` ≥ ~3–4 paths, not ≥10**; near-max
  `maxTurns` AND a multi-file surface is the exact thrash-and-timeout profile (the author's own max-budget
  bump is an admission the task is turn-heavy). `validate` emits **GR2042** (WARN) on precisely this
  co-occurrence — treat that warning as a fired trigger, not noise to wave through.
- **(c) Maps 1:1 to a design milestone.** A plan milestone / phase / numbered section is NOT a task —
  it is a *bundle* of deliverables. If a candidate task is "implement Milestone M4," decompose it into
  the deliverables inside M4; never size a milestone 1:1 to a task.
- **(d) Retry re-runs expensive work.** Estimate what a single failed guardrail forces to redo. If a
  retry re-runs an hour of refactoring (a multi-deletion, a 100+-ref re-baseline), the task is
  mis-sized by definition. Split so each task's retry is cheap.
- **(e) Fan-in-sink / composition-root wiring (#378).** "Wire the components together" is **NOT one
  deliverable** when it spans multiple collaborators or the composition root — each wire-up (factory
  registration, scheduler call-site, CLI exit-code plumbing) is a **separately-verifiable integration
  point**; **"it's just wiring" is a rationalization that dodges the split** (it reads as a single outcome
  precisely because it is described as one). Treat a task that **composes the outputs of ≥2 upstream
  producers** into a factory / `Program.cs` / dispatch site as **N tasks** (one collaborator wiring each),
  isolating the turn-expensive composition-root proof (drive the REAL factory, #120) to a **thin sink**.
  This is the archetype the older triggers missed: it is not "milestone-sized," creates/deletes nothing,
  and sits at half the ≳10-file line — yet each file is a distinct integration surface the retry re-runs.
  Its structural tell is the co-occurrence in (b) — a near-max `maxTurns` plus a multi-file `writeScope`
  plus a wide `dependsOn` fan-in — the same fingerprint `validate` flags **GR2042**. This trigger shares a
  root with the passing-but-blind check (#382, Step 4 analysis): the sink is over-scoped *because* it
  concentrates real-seam integration proof that should be distributed to each collaborator's own task.
  **(e) splits by COLLABORATOR — it does not split by TDD polarity, and running it is not enough.**
  Re-run each piece it produces through **rule 5's authorship test**: a per-collaborator wiring task
  that also writes its own wiring tests is *still* one task authoring both halves, and still splits.
  This is the exact pairing that was measured — the outcome sounded singular twice over ("wire X into
  Y", then "wire just this one collaborator"), and the resulting guardrails could not tell a wired
  implementation from an unwired one.

**Carry the plan's own feasibility signals into sizing (#111).** When the plan's
feasibility / self-critique / risk section flags a milestone as **heavy, over-packed, or
high-churn** ("~147 test refs", "over-packed", "large blast radius", "risky to do in one pass"),
treat that as a **fired trigger**: the breakdown MUST split that milestone rather than size it 1:1.
This signal already exists in the plan — do not let it die between the plan's risk section and your
task sizing.

**Every sized task declares its write surface (#389).** Sizing bounds a task's blast radius; the
`writeScope` MAKES that bound explicit and machine-checked. Once a task is sized, it MUST carry a
`writeScope` — real paths for a writing task, or **`[]` when it writes nothing to the repo** (a
configure-a-database task, a verification/read-only check, a state-only task whose only output is a
`GUARDRAILS_STATE_OUT` fragment). Omitting it is a validation ERROR (**GR2041**) that Step 7's
`guardrails validate` will catch — see the `writeScope` schema quick-ref in Step 6. **And before you
write the paths down, trace the datum** — the sibling-datum rule later in this step (#474).

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

### Before you write a `writeScope`, TRACE THE DATUM — follow the sibling that already works (#474)

A `writeScope` written from the plan's **type names** is a guess. For any task whose deliverable is
*"datum D reaches sink S"* — a value parsed here, carried there, recorded at the end — the scope is
correct only if it contains **every file on D's actual path**. Finding that path is mechanical, needs no
cleverness, and costs one grep:

1. **Find the nearest existing sibling datum that already makes the whole trip.** Something already
   travels this route: a cost, a duration, an id, a status, an error.
2. **Grep it end to end** and list **every** file it passes through — including the ones the plan never
   names.
3. **The new datum's `writeScope` must cover the same set.** If it does not, the scope is wrong. Widen
   it, or split the unreachable hop into its own task that owns the missing file and wire the edge.

**Trace on what the SINK actually READS, not on the type names** — that is the whole trap. In the
measured instance the names lined up perfectly. A hand-authored task had to carry a runner-reported
token count through three hops — parse (`ClaudeStreamParser`) → carry (`ClaudePromptRunner` →
`PromptResult`) → journal (`AttemptRecord.Usage`) — and its `writeScope` listed exactly those three
files, one per hop. It reads right, and it is impossible: `AttemptJournaler` does not build
`AttemptRecord` from a `PromptResult`, it builds it from an **`ActionRun`**, declared in
`ActionRunner.cs` — a **different, already-merged task's** file. Hop 3 was unreachable; the agent's only
in-scope moves were an honest halt or a token that journals nothing; it halted at `needsHuman` **after
`validate`, `graph --check` and a full `/guardrails-review` had all passed.** The author's own account:
*"I traced `PromptResult → AttemptRecord.Usage` on the type names without checking what the journaler
actually reads."* The sibling datum — **`CostUsd`** — was sitting in the same two files, and one grep
would have put `ActionRunner.cs` on the path.

**Enumerate every CONSTRUCTION SITE of the sink type, not just the one the plan mentions.** The same
measured plan had two more (`RunReport.PendingAttempt`, and a second `AttemptRecord` built in the
scheduler's worktree path) that no task's scope contemplated — a scope that covers one of three
construction sites delivers the datum on one path and silently drops it on the others.

**This is a REACHABILITY rule, not a size rule.** It can only ADD files to a scope, or MOVE a hop to the
task that owns the missing file; it never grades how big a task is and never reads `action.maxTurns`. If
the traced scope then trips the over-size split-trigger above, that is the **split-trigger's** verdict
from its own evidence ((a)–(e)), reported as its own finding. The boundary is about which **verdict** a
rule derives, not which field it reads (`docs/plans/18-integration-proof-proximity.md` §6 as corrected;
GR2042 owns *"this task is too big"*). No `validate` check sees an unreachable datum — deciding it needs
semantic analysis over a tree the run has not written yet — so this trace and `/guardrails-review` §2's
matching **Unreachable-outcome** probe are the only gates it has.

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
- **The source-shape demotion order comes FIRST — it decides what you reach for (#468).** The
  catalogue's archetype numbers are stable IDs, not a strength ranking; `file-contains` being row 1 is
  not permission to start there. Before selecting any guardrail, ask of each invariant: **is this a
  claim about what the code DOES at runtime, or a structural fact about the build/wiring graph?**
  1. **Behaviour → a test** (archetypes #2/#4/#7/#8). A test IS the property; a source regex is a proxy
     for it, and the two are only accidentally aligned.
  2. **"X must USE Y" → an AGREEMENT property test** — enumerate the input domain, assert the two sides
     agree for every input. An inlined copy that is equivalent today passes and fails the moment it
     drifts, which is the only moment the rule matters. No regex can express that.
  3. **A source-shape regex LAST** — only when the property is genuinely unobservable at runtime, and
     **Step 7.4 must state why no test could carry it.** Then it also ships its two-sided sample pair
     (below) and passes the taxonomy battery.

  Measured over three review rounds and five agents on one breakdown: the test layer was **never broken
  by any agent in any round**; **every blocker lived in the source-shape layer**, including 5 regressions
  introduced while fixing earlier rounds. Against a tree with the type declarations and no wiring at all,
  a 14-clause grep manifest went **10/14 green** — a grep manifest measures **vocabulary, not
  capability**. This is NOT a blanket ban: build-descriptor registration, cross-module reference chains,
  entry-point wiring, the #120 grep fallback and #176 negative assertions are structural facts with no
  runtime proxy and stay. (Catalogue → "The source-shape demotion gate"; the AGREEMENT property-test
  section; the 13-shape taxonomy table.)
- **Every source-shape guardrail over CODE ships its two-sided sample pair, committed beside it
  (#468/#302).** `tasks/<id>/guardrails/NN-check.ps1` ships with `tasks/<id>/samples/NN-check.valid.<ext>`
  (a **complete**, representative correct artifact → exit 0) and `NN-check.invalid.<ext>` (the one defect
  it exists to catch → non-zero), so a later edit can re-run them. **Put the samples in a `samples/`
  sibling, NEVER inside `guardrails/`/`preflights/`** — the loader enumerates every non-`.json` file in
  those folders as a guardrail with no extension allowlist, so a sample would load as a script guardrail,
  count toward GR2003, and be executed at run time (or fail GR2027 in the catches-enforced folders).
  `tasks/<id>/samples/` is not enumerated and is excluded from the task definition hash.
  **Re-run the WHOLE pair after ANY edit to the script**, not just the case
  you just fixed — a fix to one clause and a regression in its neighbour arrive in the same commit
  (measured five times; round 3 found more blockers than round 2). The VALID half is the one authors skip
  and the one that pays: it is the only half that exposes a clause that can **never** match, since under
  the invalid half everything is failing anyway. **DOCUMENTATION deliverables are EXEMPT** — you cannot
  synthesize a meaningful invalid sample of a design doc — but the exemption is **named in the report**
  and the **PRECEDENT check is the mandatory substitute** (point at a sibling precedent in that same
  document for every literal token you demand; accept both forms where both are legitimate). A CODE
  guardrail gets no such hatch: if you cannot write its invalid sample you do not yet know what it
  catches. (Catalogue → the two-sided sample pair + its documentation escape hatch.)
- **A required-present clause over a `.md` target STRIPS `<!-- … -->` before matching.** The
  comment-blind family (#97/#98) and the two-variable rule are written for **source**, where the failure
  is a false RED; over a **document** the same blindness runs the other way and yields a false **GREEN**,
  because a clause over a doc is almost always required-present. **Measured:** a guardrail requiring two
  tokens in `docs/plans/02-schemas-and-contracts.md` and in a `SKILL.md` went from exit **1** to exit
  **0** when a single `<!-- TODO: document … here -->` line was appended — its stated purpose, *the
  contract moves in the same change-set as the code*, discharged by a commented-out TODO. An HTML comment
  **renders as nothing**: invisible text, not thin prose. Emit
  `[regex]::Replace($doc,'(?s)<!--.*?-->','')` and **fail on a residual unterminated `<!--`** rather than
  stripping to EOF (which would delete the rest of a document over one stray token). **Do NOT strip
  fenced code blocks** — a fence RENDERS, so a verb documented in a usage fence is legitimate house
  style; measured on that same SSOT, **43,387 bytes of fenced content across 26 blocks** and **2 of its
  36 `PlanDefinition` occurrences live inside one**, so a fence-stripping clause would reject a correct
  document written in its own style. This is the compensating control for the documentation **exemption
  from the sample pair** in the bullet above — the doc target is precisely where no invalid sample runs.
  (Catalogue → "The DOCUMENTATION target has the same hole".)
- **MEASURE every required-present clause's baseline count and RECORD it in the script (#478).** The sample
  pair cannot catch this — both halves are synthetic files, and the defect lives in the **real tree**. Before
  pinning a token, run it against **the exact subject that clause scans**, with the clause's own case
  sensitivity, and write the number in a comment beside it. **Zero is the expected answer; a nonzero means
  you change the CLAUSE, not the comment.** Measured, three shipped in one wave: a required `.prompt.md`
  already appeared **twice in that same file** (appending one unused const then passed the whole task with
  zero capability), a required `Judge` already appeared 5× as `CriticalityJudge`, and a proximity window
  matched a pre-existing line. **A red exit code does not clear you** — a guardrail has many clauses and one
  exit code, so a clause green on arrival hides behind its siblings and the script still exits 1. Only
  **required-present** and **numeric-floor** clauses are measured this way: a **forbidden-present** clause is
  *supposed* to be green before its task. Nonzero is allowed **only with a named reason on the same line**
  (preflight / positive baseline, `tests-untouched` regression, the "if X is present" half of a union-safe
  conditional, or a ratcheting behaviour manifest **on a plan regenerated against a partially-landed tree**
  — the qualifier is load-bearing: on a FRESH plan a nonzero manifest clause is not a ratchet, it is a
  clause already satisfied). And a **multi-clause** guardrail **accumulates** — one
  distinguishable `$failures += …` per clause, dumped once at the end — never an `exit 1` chain that reports
  one gap per attempt; the only legitimate early exits are a **precondition** (the subject is missing or
  unparseable, so every clause below would crash — `Test-Path`, a failed `ConvertFrom-Json`, an empty
  state key) and an expensive
  behavioural stage placed after the dump. (Catalogue → "Every required-present clause records its MEASURED
  baseline count".)
- **Never assert an executed-test COUNT as an adequacy floor (#468).** `dotnet test` counts **theory data
  rows, not behaviours** — one `[Theory]` with six `[InlineData]` rows clears an "at least 6 executed"
  floor while proving one behaviour, and raising the number does not fix it. Use a **behaviour manifest**
  instead: one clause per required behaviour, which **ratchets** as later waves land the behaviour. **The
  predicate over the manifest is `observed FAILED on the stub tree, then observed PASSED after` — not
  `discovered by name` (#375).** A `--list-tests` listing asks only *"does a test with this name exist?"*,
  which a hollow body satisfies exactly as a comment satisfies a token floor; name-discovery is the
  fallback only where there is no stub tree to be red against, and must be worded as a lower bound. The
  #455 **zero-match guard** (`>= 1` test executed) is NOT an adequacy floor and stays. (Catalogue →
  count-floor section and the per-test-red-census section.)
- **A test-author task whose prompt ENUMERATES behaviours emits the red as a PER-TEST CENSUS, not a suite
  exit code (#375).** `dotnet test --filter … exits non-zero` fires if **any** selected test fails, so a
  hollow `Assert.True(true)` **passes** on the stub tree and hides behind its genuinely-failing siblings —
  measured: a `covers-*` floor exited 0 over a security test file whose five invariants were pinned by
  `Assert.NotNull` / `Assert.True(true)`. Same file (`02-tests-fail-on-stubs.ps1`), same `$filter`,
  stronger predicate: **every enumerated behaviour bound to a PINNED test name and observed `Failed` in
  the runner's own result file** (TRX for .NET — never stdout, #248), one accumulated message per unbound
  behaviour (#179). This makes the `covers-key-behaviors` naming floor worth having; it does **not**
  replace it. Two consequences you must honour while authoring: the `action.prompt.md` has to **pin the
  test METHOD name for each behaviour** (the same prompt↔guardrail agreement #455 already demands for the
  class name), and the report must state the census's boundary — an *invoking*-then-hollow test
  (`var r = sut.Consume(x); Assert.NotNull(r);`) is red on stubs, green after, and **passes** it. **Never**
  reach for a rejection-shaped source regex (`Assert\.Throws` / `Assert\.False`) instead: it false-**reds**
  a correct `Assert.Equal(RejectedStale, r.Outcome)` and is satisfied by one tautological
  `Assert.Throws<NotImplementedException>` line. (Catalogue → per-test red census; `stacks/dotnet.md §4.4`.)
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
  **Never carry `-v q` onto a `dotnet test` guardrail** — measured, it suppresses the entire
  `Error Message:` / `Expected:` / `Actual:` / `Stack Trace:` block and leaves only `[FAIL] <name>`,
  so the re-emit has nothing but test NAMES to re-emit and #179 is defeated by the flag alone.
  `-v q` is a **`dotnet build`** rule (`stacks/dotnet.md §4`); `--nologo` is fine on both.
- **A task-level test `--filter` MUST name THIS task pair's OWN test class (#455).** The class term is
  **mandatory**; the plan-wide trait is an **optional conjunct**. Emit it on both halves of every TDD
  pair (`tests-pass` AND `tests-fail-on-stubs`):

  ```
  --filter "Category=<PlanTrait>&FullyQualifiedName~<ThisTaskPairsTestClass>"
  ```

  **ALONE, the plan-wide trait belongs in exactly one place: the Step 5 baseline preflight's `!=`
  exclusion.** (Keep the word "alone" in it — the rule bans the *bare* trait, not the trait.) The trait
  is *introduced* for the preflight (`--filter "Category!=<PlanTrait>"` — "everything except the tests
  this plan is about to write") and, having introduced it, it is the most visible and most
  authoritative-looking selector in the plan at the exact moment you write the task guardrails.
  **That is the trap.** A task-level guardrail keyed on the bare trait asserts the state of *every* test
  in the plan instead of the ones its own pair owns, and it fails in **two opposite directions**:
  - **forward** — the task's `tests-pass` selects tests only a **downstream** task can make green, so the
    task cannot go green until a task that `dependsOn` it has run. `guardrails validate` and
    `graph --check` both PASS (the cycle is between a task and a **sibling's test corpus**, not between
    tasks — no DAG check models it); the run burns its whole retry budget and ends at `needs-human` with
    the task's deliverable complete and its own tests green;
  - **inverse** — `tests-fail-on-stubs` wants *some* matching test red, so it passes off **any sibling's**
    intended-red tests whether or not this pair's tests fail. The TDD-red proof (#155), the strongest
    anti-tautology check this skill has, silently degrades into **merge-order luck**. This is the worse
    half — the forward deadlock at least fails loudly.

  Two companions, neither optional: **(a)** the class-name substring must be **discriminating**
  (`~Dispatch` also selects `DispatchRouterTests` — check it against every other test class the plan
  authors and every existing class in the target project, and namespace-qualify when it is not);
  **(b)** narrowing reintroduces the **zero-match hole** — a `--filter` matching nothing (or malformed)
  **exits 0** — so every narrowed filter ships with the zero-match guard. Four measured details decide
  whether that guard actually works, and getting any of them wrong is worse than having no guard: key it
  on the **executed count (`Passed:` + `Failed:`)**, not `Total:` (which counts `[Skip]`ped tests, so a
  fully-skipped class passes it); pin **`$env:DOTNET_CLI_UI_LANGUAGE = 'en'`** first (the summary line is
  LOCALIZED — on a German-culture box it prints `gesamt:` and no `Total:`, inverting the guard into an
  unconditional failure); never key it on the "no tests matched" **string** (verbosity-dependent, so
  it never fires — the #248 failure); and where the guard counts records read out of a result FILE,
  **count what the runtime hands you when the answer IS zero** — with no tests executed the TRX carries
  no `<Results>` element, the dotted navigation yields `$null`, and `@($null).Count` is **1**, so
  `@($xml.TestRun.Results.UnitTestResult).Count -lt 1` evaluates `1 -lt 1` and never fires; write
  `@(… | Where-Object { $_ })`. **Then PROVE it fires** — run the guard against its own zero case (an
  empty result file, a deliberately typo'd filter) and watch the precondition line come out. All four
  traps read correctly on the page and are dead only in execution, so re-reading cannot find them;
  skipping that proof was measured at **11 misdirected findings** naming every pinned behaviour as
  unbound, aimed at the one artifact a retry agent may edit. The exact syntax, the measured output table, the guard expression,
  the polarity-dependent ordering, and the two canonical scripts are `stacks/dotnet.md §4.3` (universal
  rule: catalogue → "Its SCOPE decides whether it proves anything").
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
  NOT the build/suite. It asserts something true of any valid intermediate union — the
  GR2028-crediting core is **"every produced file present is non-empty and conflict-marker-free"**;
  "every contribution PRESENT in the union is intact" is the *additive* contribution-present tightening
  layered on top (not GR2028-satisfying on its own, #343 — see below) — so it passes trivially BEFORE a
  contributing task has run. Its content checks MUST be **UNION-SAFE = CONDITIONAL**: `IF contribution X
  is present, verify it's real`, never `REQUIRE X present`. The conditional pattern (the `parallel-hello`
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
  contributing task has run, and tightens once that task's hunk lands. **But a
  contribution-present check does NOT satisfy GR2028 on its own — it is ADDITIVE, layered on top
  of the union-soundness proof, never the sole content of the terminal gate (#343).** GR2028 is
  credited only by a **conflict-marker-freedom check** (the line-anchored `<<<<<<<`/`>>>>>>>` scan
  above) **or** a recognized whole-repo build/test/suite invocation — these are ungameable,
  whereas a content grep is vacuous exactly where the terminal gate matters most: the union-safe
  CONDITIONAL form can never FAIL when a merge DROPPED a contribution entirely (the gate goes
  false → pass), so it certifies nothing about union soundness by itself. The
  overlapping-writeScope union-guardrail (next bullet, #132) satisfies GR2028 **because it ALSO
  carries the conflict-marker-freedom check** — its contribution-present checks are the additive
  tightening layered on top, not the GR2028-satisfying content.
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
- **Faked-seam ⇒ paired real-seam proof (#382 — passing but blind)** — does an `author-tests-*` task
  **fake an in-process seam the real run drives** (an `IPromptRunner`, the executor, the scheduler, a
  factory) via DI? A unit test that injects a fake of the very seam the production path exercises can go
  **GREEN over a component that is broken through the real composition root** — a *green light over a
  broken wire*. Where #120 asks "is the component wired at all?", this asks "is the component **proven
  through the seam the run actually drives**, or only against a fake of it?". This check is an
  **analysis**, and its artifact is the **seam ledger**: build it here (rules 1–6), emit proofs from its
  `T*` column in Step 5, print it in the Step 7.4 report. Rule 7 draws the boundary against #378.
  (Catalogue → "drive-the-real-seam"; `stacks/dotnet.md §10e`.)

  **1. A seam is a `(component, declared dependency)` PAIR** — the component under test and **one**
  dependency it declares (a constructor parameter, a DI-resolved interface, an injected delegate, an
  overridable member). One row per pair — not one per component, not one per interface in the repo. A
  **process seam** — a child process, a CLI, a socket, an HTTP endpoint, a database, the filesystem — is
  **out of scope for this check**, and faking it stays expected, correct, and unchanged. The ledger records
  **substitutions the tests MAKE**, never a dependency inventory: a declared dependency the tests do not
  fake has no row. (An inventory becomes a wall of noise and stops being read — the same failure mode as a
  false-positive lint.)

  **2. Classify every faked in-process seam into EXACTLY ONE of four buckets. Only N is exempt.** This
  closed classification is what replaces the unfalsifiable phrase *"where feasible"*: an author can declare
  anything infeasible, but a bucket can be checked and a wrong one contradicted.

  - **N — a non-determinism primitive. EXEMPT.** N is a **CLOSED ENUMERATION OF FOUR ITEMS, NOT A
    CATEGORY** — a category is a hiding place, a closed list is checkable. A seam is N only if it is
    literally one of: **N1** a clock / time source; **N2** a randomness source (an RNG, a GUID factory);
    **N3** an ambient environment reader (env vars, machine name, current directory, an OS probe); **N4** a
    **wait primitive** — a sleep / delay / timer substituted so the test does not spend real time.
    **Anything not on that list is NOT N**, and `/guardrails-review` REJECTS an N classification for
    anything off it. Do not generalise the list: a seam that merely *feels* like N is E, C or U.

    > **The N4 trap — fake the WAIT, never the WAITER. If the substitute contains a DECISION, it is not
    > N4.** This is the likeliest place the taxonomy gets abused and it is written from a shipped bug.
    > Substituting the **sleep** so a backoff test finishes in milliseconds is N4 and exempt. Substituting
    > the **backoff policy component** — the thing that decides *whether* to retry and records the
    > `blocker-retried` decision — is **C**, and it owes proof. The exemption covers the primitive that
    > CONSUMES time, never the policy that DECIDES to consume it. In the motivating dogfood this exact
    > conflation shipped a silently-swallowed transient: `RetryLoop → IDelay` is **N4**;
    > `RetryLoop → ITransientBackoff` is **C**.

  - **E — an external-resource adapter. PROOF OWED, and ALWAYS FEASIBLE.** The production implementation
    crosses a process / network / disk boundary (`IPromptRunner` → `ClaudePromptRunner` → the `claude`
    child process). **Drive the real adapter and fake the boundary UNDERNEATH it** — a stub binary, a fake
    `HttpMessageHandler`, a temp directory. That lower boundary is a process seam, which rule 1 already
    permits you to fake. "Always feasible" is exactly what separates E from a seam you cannot construct.
  - **C — an in-repo collaborator with a contract the run depends on. PROOF OWED, and feasible.** The
    production implementation lives in this repo and does real work the component depends on — a
    scheduler, a factory, an executor's backoff, a policy object. **Construct the real implementation.**
    Its own dependencies are covered by their own rows at their own tasks (rule 3), so you never build the
    universe.
  - **U — an unbuilt collaborator. PROOF OWED, but RELOCATED.** The production implementation does not
    exist yet at this point in the DAG; a later task — or, under Step 9 waves, a later wave — builds it.
    The row is **NOT exempt**: it names the **receiving task** and the proof is owed there. A U row whose
    receiving task is the terminal sink is legitimate **only** when the production type genuinely first
    exists there; otherwise the row is mis-placed and the finding is the placement, not the bucket.

  **3. One real level down, and NO FURTHER.** This REPLACES the older rule of thumb *"fake the process,
  never the in-process seam"* — same rule, made precise about **how far** real goes:

  > The component under test is constructed with the **REAL implementation of the seam under test**. That
  > implementation's **own** declared dependencies MAY be substituted — because each of those
  > substitutions is its own ledger row, owed at its own task.

  One level buys composition **by induction**, which is why the rule is worth its cost; the argument, and
  the #120 degradation ladder for when constructing the real seam forces a **second** real level, are in
  the catalogue's drive-the-real-seam section. Two things are non-negotiable here: the only permitted
  degradation is the **#120(b) reflection-plus-contrast** form (there is **no source-grep rung** for this
  archetype), and **a degradation is NAMED in the Step 7.4 report with the constructor chain that forced
  it** — an unnamed degradation is a review finding.

  **4. Placement — T\*, the earliest-proving task, computed from the DAG.**

  > For each **E** and **C** row, the proof is owed at **T\*** — the **earliest task in the DAG at which
  > BOTH (i) the component's production type and (ii) the seam's production type exist**. For a **U** row,
  > T\* is the earliest task satisfying (ii), and the row names it.
  >
  > **A proof placed LATER than T\* is a finding.** The report must NAME T\* and state why the proof could
  > not live there.

  Both existence facts are readable off the graph you are emitting — a type exists at a task when that
  task's `writeScope` contains the file declaring it, or an ancestor's does — so **T\* is computable by a
  reviewer without running anything.** That is the whole point: *"where feasible"* asked for a judgement
  nobody could contradict; *"which task is T\*, and is the proof there?"* has an answer. In the common case
  the component's own implement task **is** T\*, and the rule reads as plain English: *prove each component
  through the real seam at the task that builds it.*

  **5. The terminal composition proof is a JOIN-CHECK, never the first exercise.** The terminal proof is
  whichever object carries the composition assertion — a #120 wiring **task**, and/or the plan-level
  `<plan>/guardrails/` **folder** (SSOT §3.3). One rule for both:

  > It may assert only what the union of the upstream real-seam proofs does **NOT**: that the collaborators
  > are **assembled** — constructed, injected, ordered — by the production assembler. Its `# catches:` must
  > name a defect that **SURVIVES every upstream real-seam proof passing**. If it cannot name one, it is
  > redundant. If the only defect it can name is *"this seam is exercised for the first time here"*, then a
  > ledger row is MIS-PLACED and the fix is upstream — **not** a wider `writeScope` here.

  That last clause is the anti-regression clause: without it an author satisfies this rule by writing a
  ledger and then leaving all the proof in the sink anyway. Such a sink IS the **#378 fingerprint**
  (over-scoped *because* it concentrates deferred integration risk, and unable to fix the cross-file bug it
  finds) — the two issues share one root.

  **6. The ledger — the artifact this analysis produces. Its FORMAT IS A CONTRACT: `/guardrails-review`
  reads it.** One markdown table, six columns, exactly these headers, printed in the Step 7.4 report under
  a bolded line reading `Seam ledger (#382)`:

  | seam (component → declared dependency) | bucket | production type | faked underneath | T* | proof |
  |---|---|---|---|---|---|
  | `CriticalityJudge` → `IPromptRunner` | E | `ClaudePromptRunner` | the `claude` CLI child process (stub binary) | `09-implement-criticality-judge` | `tasks/09-implement-criticality-judge/guardrails/03-real-seam-tests-pass.ps1` |
  | `RetryLoop` → `ITransientBackoff` | C | `TransientBackoff` | — | `11-implement-transient-recording` | `tasks/11-implement-transient-recording/guardrails/03-real-seam-tests-pass.ps1` |
  | `RetryLoop` → `IDelay` | N4 | — | — | exempt | — (the wait, not the waiter) |
  | `Scheduler` → `IOverwatcher` | U | `Overwatcher` (built in task 13) | — | `13-implement-overwatcher` | deferred to T*, named |

  The header row is fixed text, in that order, with `T*` written literally (no escaping):
  `| seam (component → declared dependency) | bucket | production type | faked underneath | T* | proof |`.
  Cell rules, so the table is machine-readable and self-checking:
  - **bucket** — exactly one of `N1` `N2` `N3` `N4` `E` `C` `U`. Never blank, never a bare `N`, never a
    value off that list.
  - **production type** — the concrete type the production run resolves; `—` on any `N*` row.
  - **faked underneath** — the PROCESS boundary stubbed below the real seam (a stub binary, a fake
    `HttpMessageHandler`, a temp directory), or `—` when nothing is faked below it.
  - **T\*** — the task **FOLDER NAME** (the same identity `dependsOn` and the state key use), never a
    `stableId` and never a prose description; `exempt` on any `N*` row. Under Step 9 waves, a U row whose
    receiving task is not yet broken down names the receiving **WAVE** folder instead, and that wave's own
    breakdown resolves it to a task.
  - **proof** — the guardrail file path **relative to the PLAN folder** (`tasks/<T*>/guardrails/NN-….ps1`),
    which makes the row self-checking: a proof path whose task segment differs from the `T*` cell is an
    inconsistent row and a finding in its own right. On an `N*` row it is an em dash, **optionally
    annotated** (`—` or `— (the wait, not the waiter)`); on a `U` row, `deferred to T*, named`.
  - **Rows the ledger does NOT carry:** process seams (rule 1), and any declared dependency the tests do
    not substitute.
  - **The heading is emitted EVEN WHEN THERE ARE NO ROWS** — print the bolded `Seam ledger (#382)` line
    followed by the single line `_No in-process seam is substituted by this breakdown's tests._`, and omit
    the table. `/guardrails-review` treats an **absent** ledger as evidence the analysis was never run; a
    ledger with **zero rows** is a claim, and a claim can be checked.

  **7. The #378 boundary — one root, two mechanisms, NO OVERLAP. Inherit this rule; do not renegotiate
  it.** #378 owns the **size and shape of a task**: it reads `writeScope` cardinality, `action.maxTurns`
  and `dependsOn` fan-in, its mechanism is **GR2042** (deterministic WARN) plus the Step 2 split-trigger
  (e), and its verdict is *"this task is too big."* #382 owns the **placement of proof**: it reads which
  seam a test substitutes and where the real-seam proof lives, its mechanism is this ledger plus the
  archetype (audited by `/guardrails-review`), and its verdict is *"this proof is in the wrong task."*
  **The boundary is VERDICT-BASED, not field-based.** Therefore — **#382 NEVER derives a SIZE verdict
  from `writeScope`, `action.maxTurns` or `dependsOn`**, and **#378 NEVER adds a rule about what a
  guardrail PROVES.** What GR2042 owns is `writeScope`'s **CARDINALITY and SHAPE as evidence about a
  task's SIZE** — not the field itself. `writeScope` is read all over the system for other purposes
  (GR2019 workspace escape, GR2020 vacuous/over-broad, GR2041 required-present, and the runtime
  `WriteScopeCheck` membership test), so **reading `writeScope` as a lookup or a coverage set — "which
  task owns this file?", "is the carrier of this datum in somebody's scope?" — is NOT a boundary
  crossing.** Only turning its count or breadth into a "this task is too big" judgement is.
  Where they meet: told *"this task is over-scoped"*, the reflex is to chop the `writeScope` — which for a
  fan-in sink yields N small tasks that still contain the first exercise of every real path, so the
  concentration survives the split. **Relocating the proof to T\* is the fix; narrowing `writeScope` alone
  is not.**
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
- **A forbidden token must not collide with what the task REQUIRES (#470)** — the safety rule for the
  negative assertion you just emitted. Run it on **every** fail-on-present clause, in two directions:
  - **Guardrail ↔ itself — UNSATISFIABLE, a BLOCKER.** Take each **required-present** clause's literal
    text (de-regexed) and match it against **every forbidden pattern in the same file**. A hit means no
    file can satisfy both: every attempt fails identically with coherent, actionable, **wrong** feedback
    and the task dead-ends at `needs-human` having never been achievable. Measured: a required
    `[Trait("Category", "TierResolution")]` whose own **string literal** carries the token a clause 40
    lines later forbids — each clause individually correct, so **reading did not reveal it**. Its blast
    radius was three downstream tasks. A mechanical `validate` lint now backstops the narrowest slice of
    this — **GR2057** fires when one subject variable carries both a required-present literal and a
    forbidden-present pattern that the literal trips. **It does not replace this check:** GR2057 is
    deliberately silent wherever it cannot PROVE the collision — clauses over DIFFERENT subjects (the
    two-variable `$code`/`$scan` fix below, which it must NOT flag), compound `-and`/`-or` conditions,
    interpolated or composed patterns, anchored forbidden patterns, `.sh` guardrails, and both the
    cross-file and prompt↔guardrail axes. A green `validate` means the provable case is clear, not that
    the pair agrees.
  - **Prompt ↔ guardrail — TRAP-SHAPED, fix it anyway.** Grep the task's **own `action.prompt.md`** for
    every banned token. A hit means the prompt invites the agent to write the very thing that reds it —
    measured: a guardrail banning `(?i)\bUnavailable\b` over raw content while the prompt used that word
    three times; the agent echoed it and lost a full attempt. Satisfiable, but the ban was on an ordinary
    English word rather than on the **enum member** actually forbidden.

  **The fix for both is one shape:** run the forbidden scan over **STRIPPED** source — comments **and**
  string literals, since #97/#98 covers only comments and the measured collision hid in an attribute's
  string literal — and **anchor the ban on a USE, not a mention (#76)**: the dotted call, the type
  position, the enum member, the declaration. Never the bare word. Re-measured over 8 cases, that keeps
  every tooth (a real call RED, the type position RED, a missing trait RED) and removes every false RED
  (the token in a comment, in a string, in a test name). Do **not** read this as a revert of #177: GR2026
  fires when the guardrail REQUIRES a token the prompt never mentions; this fires when the guardrail
  FORBIDS a token the prompt DOES use — opposite polarities, each silent in the other's healthy case.
  (Catalogue → "A forbidden token must not collide with what the task REQUIRES".)
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
- **`$delegated` non-empty (Step 0d.1 found Charter delegated-decision markers) → emit
  `<plan>/preflights/01-delegated-decisions-recorded.ps1` (#500).** The full shape, the embedded-vs-grep
  design decision and the smoke-test evidence are **Step 0d.6** — do not re-derive them here. Three things
  matter at this point in the flow: it is a plan-root Full Flight Check FILE (no task, no action, no
  `dependsOn`, so acyclicity is unaffected); it is emitted from the SAME scan that wrote `decisions.md` and
  the prompt constraints, never independently of them; and it carries **no worth-it gate** — unlike the
  #181 baseline, whose false-red risk comes from running someone else's tests, this check reads only
  artifacts THIS breakdown authored inside the plan folder, so "under-fire when unsure" does not apply.
  **`$delegated` empty ⇒ emit nothing** (Step 0d's gate).
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
    **If the prompt ENUMERATES behaviours, that second guardrail is the PER-TEST CENSUS (#375)** —
    every enumerated behaviour bound to a pinned test method name and observed `Failed` in the
    runner's own result file, because a suite-level non-zero exit lets a hollow `Assert.True(true)`
    pass on the stub tree behind its genuinely-failing siblings. Same file, same `$filter`, stronger
    predicate (catalogue → per-test red census; `stacks/dotnet.md §4.4`). Pin those method names in
    the `action.prompt.md`, or no census can be written.
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

  **`writeScope` is REQUIRED on EVERY task (#389); NEVER omit it, NEVER emit a vacuous `**`.**
  Every emitted `task.json` MUST declare a `writeScope` — omitting it is now a validation ERROR
  (**GR2041**), and Step 7's `guardrails validate` FAILS a breakdown that omits any writeScope
  (self-validation closure). The three forms:
  - **a task that writes to the repo** → list its real surface (paths/globs/dirs), e.g.
    `["src/MyProject/"]` or `["tests/Foo/Tests.cs", "src/Foo/Feature.cs"]`;
  - **a task that writes NOTHING to the repo** → emit `"writeScope": []` (the deliberate
    "writes nothing" declaration — VALID, never flagged). This is the correct form for:
    **configure-a-database** / provisioning task, a **verification / read-only check** task, and a
    **state-only** task whose only output is a `GUARDRAILS_STATE_OUT` fragment (a state fragment is
    NOT a repo write and never appears in the segment diff). DECLARE `[]` — do not omit;
  - a genuinely **broad / cross-cutting** change (a sweeping refactor, a terminal whole-suite gate)
    still declares its surface EXPLICITLY (name the directories), never a vacuous `**`.
  `validate` rejects a scope that escapes the workspace (**GR2019**, error) and **warns** on a
  vacuous/over-broad scope (**GR2020**) — so emit a real surface or `[]`, never omit and never `**`.

  **Action prompt for both tasks.** The declared scope is injected into the action prompt as
  advisory context, but the harness ALSO enforces it mechanically — so every test-author prompt
  must carry a **Scope boundary (harness-enforced)** paragraph (#154; Step 6 has the authoring
  rule and exact shape). The test-author `## Task` section must tell the agent: (a) the exact
  test file path(s) **AND the exact test CLASS NAME(s)** — not just the file — and, for a behavioral
  type, the exact STUB file path(s) to create with
  `NotImplementedException` skeletons so the test project COMPILES (#155), plus any category/trait
  convention the repo uses. **Pinning the class name is load-bearing, not tidiness (#455):** the pair's
  `tests-pass` / `tests-fail-on-stubs` filters are `FullyQualifiedName~<that class>`, so a prompt that
  leaves the class name to the agent makes a correct filter unwritable and pushes the author back onto
  the plan-wide trait — the defect's origin. The prompt's class name, the `writeScope` path, and both
  guardrail filters must agree; (b) the tests MUST COMPILE and FAIL against the stubs — failing is
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
- **Seam ledger carries E / C / U rows (Step 4 analysis) → emit ONE real-seam proof per owed row AT T\*,
  and thin the terminal sink (#382).** The ledger's `T*` column is an **emission instruction**, not a note.
  For each **E** and **C** row:
  1. **On T\*'s paired `author-tests-*` task** — the real-seam contract test is authored alongside the
     fake-based unit tests, listed in that task's `covers-key-behaviors` manifest (#75), and **INCLUDED in
     the `tests-fail-on-current-code` / `tests-fail-on-stubs` filter**, so it is proven **RED** and cannot
     be a tautology. #155 applies unchanged: the red must **COMPILE** and fail, so the test-author task
     also writes whatever stub the real-seam test needs to compile.
  2. **On T\* itself** — a `specific-tests-pass` (#4) guardrail whose `--filter` names **that pair's own
     test class** (#455, with the zero-match guard and the #179 failure-detail re-emit). **`scope`:
     LOCAL — omit the key.** A real-seam proof asserts *"this component works through the real seam"*,
     which cannot be true before T\*'s own action has run, so it **fails the #125 union-safe test** and
     must not be tagged `scope: "integration"` — that is the #250 mistake, and the #120 discussion above is
     where a reader most easily picks up the question without picking up its answer.
  3. **The test must assert an effect ONLY the production implementation emits** — the stream-log FILE
     appears on disk; the journal contains a `blocker-retried` DECISION; the verdict's `Source` is not the
     catch-and-safe-default. ***"The seam was called" is NOT an assertion*** — the fake satisfies it, which
     is precisely how the motivating bugs shipped green.

  For each **U** row emit **no guardrail here**: the row already names the receiving task (or wave) and the
  proof is owed there — carry the row forward so the report and the review pass both see the deferral.

  Then **re-scope the terminal proof**. With the real-seam proofs upstream, the #120 wiring task and the
  `<plan>/guardrails/` folder assert only **assembly**, and each such guardrail's `# catches:` must name a
  defect that survives every upstream real-seam proof passing (Step 4 rule 5). If you cannot name one,
  **delete the guardrail** rather than keep a redundant gate. And **never emit the same row's real-seam
  proof twice** — once at T\* and again in the sink is the concentration this rule exists to remove.
  (Catalogue → "drive-the-real-seam"; `stacks/dotnet.md §10e`.)
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

**A GATE has no "ancestor task" — sweep it anyway, with the right producer set (#474).** The sweep
above is task-shaped, and that is why gates fall out of it: `<plan>/guardrails/`, `<plan>/preflights/`
and each wave's pair are checks with dependencies, not infrastructure. Substitute the producer set for
the ancestor set — plan **terminal gate** → every task in the plan; **wave exit gate** → that wave's
tasks plus all earlier waves; **wave entry gate** → earlier waves only; **`<plan>/preflights/`** →
*nobody*, since it runs before the DAG against the starting bytes, so everything it requires must
already exist. A gate clause nothing on that list produces (and the repo does not already satisfy) is a
missing inserted task, exactly as at task level — and if the plan genuinely cannot produce it, the
requirement does not belong in this plan. Measured: a terminal gate required a literal in a doc no
task's `writeScope` covered; the run drained its whole DAG before finding out.
(`/guardrails-review` §4 holds the full producer-set table and is the authority — do not re-derive it.)

## Step 6 — Write the folder

Per `references/schemas.md`, exactly:

- Folder = plan filename minus `.md`, beside the plan. Tasks = `NN-verb-object`
  kebab-case; NN follows a valid topological order (human-scanning hint only).
- `guardrails.json`: version + sensible run config. **Any `.prompt.md` anywhere ⇒
  the `promptRunners` block with a resolvable default is REQUIRED** (else GR2008).
  Scope `allowedTools` to what the actions genuinely need.
- **Multi-task plans should default-include read-only git inspection in `allowedTools`
  (#252).** When the plan has **≥2 tasks joined by `dependsOn`** — i.e. a downstream
  task's action prompt runs in a workspace an ancestor task has already committed
  changes to — add a handful of **READ-ONLY** git commands to the default
  `allowedTools` alongside whatever stack-specific entries are already there
  (`Bash(dotnet *)` for a dotnet plan, etc.): `Bash(git log*)`, `Bash(git diff*)`,
  `Bash(git show*)`, `Bash(git status*)`. "What did the prior task actually change
  before I extend it further?" is a normal, common instinct for a later task in a
  sequential chain, not a plan smell — without these, the agent burns turns on
  rejected `git log`/`git diff` attempts (in whatever compound-vs-bare form it tries
  next — the rejection is identical either way) and falls back to broad `Grep`/`Glob`
  sweeps across dozens of files to reconstruct context a single `git diff` would have
  given directly. Keep the default READ-ONLY: do NOT add **state-mutating** git
  operations (`restore`, `reset`, `checkout` outside the task's own files, `push`,
  `commit`, `stash`) — this is about read-only inspection, never loosened write access.
  **But `allowedTools` is a FLOOR, not a ceiling.** Claude Code MERGES the harness's
  `--allowedTools` with the operator's own `~/.claude/settings.json`, so a plan's list can
  only GRANT a capability — it can never WITHHOLD one. Omitting `git checkout` does NOT
  make `git checkout` unavailable: on a box whose settings file already allows it, a task
  can and will run it. Author the read-only default anyway — on a clean box or in CI the
  plan's list IS the whole grant — but never write a task, prompt or guardrail that RELIES
  on a state-mutating verb being unavailable. A single-task plan (nothing yet for a task to
  inspect) has no need for this — omit it there, as the existing single-task templates
  correctly do.
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
- **`writeScope` (REQUIRED on every task, SSOT §3.4, #389)** — a list of workspace-relative path
  prefixes/globs declaring the surface the task may add/modify/delete/rename; the harness verifies the
  task's diff stays inside it (a deterministic read-only check that never reverts). Emit it for the
  TDD pair (test-author owns the test files; implementation EXCLUDES them — Step 5) and for EVERY other
  task. **Three states:** real paths for a writing task; **`[]` for a task that writes nothing to the
  repo** (a configure-a-database task, a verification/read-only check, a state-only task — DECLARE `[]`,
  never omit); **ABSENT is a validation ERROR (GR2041)** — omitting is forbidden ("lazy planning"). Never
  emit a vacuous `**` or bare top-level dir (escapes the workspace ⇒ **GR2019** error; vacuous/over-broad
  ⇒ **GR2020** warning). Step 7's `guardrails validate` FAILS a breakdown that omits any writeScope.
- **`integrationGate` — RETIRED (SSOT §3.3; see the four-folder doctrine bullet above).** There is
  **NO terminal-sink task** and no `integrationGate` field. Do NOT add this key to any `task.json`:
  a plan still declaring `integrationGate: true` is a **hard validation error — GR2029** (no
  coexistence window). The terminal whole-repo integration gate now lives in the plan-root
  **`<plan>/guardrails/`** folder (the "Terminal Gate", run once on the merged plan-branch HEAD); a
  multi-leaf/fan-in plan carries ≥1 real integration-set re-run there (enforced as **GR2028**, the
  re-homed content teeth of the old GR2018). A single linear chain (one leaf, no fan-in) needs no
  terminal folder.
- **`action.tier` and the `tiering` block — ONLY when Step 0.9 set `$tiering = configured` (#225).**
  When tiering IS configured, write each prompt task's classified tier as `action.tier` (`"easy"` |
  `"medium"` | `"hard"`, matched VERBATIM — a stray space or capital is a **GR2043** error) and emit
  the plan-wide top-level `"tiering": { "defaultTier": "medium" }` in `guardrails.json`; the rubric,
  the exact shapes and the hand-added-task rationale are Step 4c. When tiering is **NOT** configured
  — the single-model default, no `routing` block — write **neither**, and not even `"tier": null`:
  the folder must be **byte-identical** to what this skill emitted before #225 existed (DoR
  Invariant 7, Step 4c.1).
- **`decisions.md` + the folded-in prompt constraint — ONLY when `$delegated` is non-empty (#500).** When
  Step 0d.1 found delegated-decision markers, this step also writes **`<plan>/decisions.md`** (the format
  contract is Step 0d.4) and adds the `## Delegated decisions (settled at breakdown time — do NOT
  re-decide)` block, with its `` `<id>` = `<value>` `` lines, to each consuming `action.prompt.md`
  (Step 0d.5) — alongside, never instead of, the harness-contract header below. The matching
  `<plan>/preflights/01-delegated-decisions-recorded.ps1` is emitted in Step 5. **When `$delegated` is
  empty, none of these three bytes exist** and the folder is byte-identical to a pre-#500 breakdown.
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
- **Prefer the EXECUTABLE form over the enumerated one — ship the command that finds the sites, not
  the list you found (#578).** An action prompt makes **structural claims about the codebase**:
  *"there are N sites"*, *"`A` funnels through `B`"*, *"the only caller"*, *"at `File.cs:123`"*.
  **Nothing checks them.** The claim is prose, the code it describes is outside every guardrail's
  subject, and a task implemented **faithfully against a false map** passes every check in its plan and
  ships green. Across plans 30–32 this was the dominant failure mode — every halt and near-miss was a
  defect in the instructions, not in the checks.
  - **Write the command:**
    > Grep this file for `_journal.RecordAttempt(` and cover **every** hit.
  - **Not the gloss that pre-answers it:**
    > Grep for `new AttemptRecord` to find every construction site … `CompleteSucceededOrInvalidFragment`
    > (the serial success settle) and `FailedAttempt` (the shared failure recorder that the other outcome
    > methods funnel through).

  Both are from the SAME plan (30). The first (task 06) was correct. The second (task 12, with 12a
  inheriting the same map and naming no sites at all) was false — and note it **carried the right
  command**: the enumeration that follows pre-answers the grep, so the agent has no reason to actually
  run one. `grep -c 'new AttemptRecord'` returns **9**, in nine methods, seven of them called
  **directly** from `TaskExecutor` (`NeedsHuman`, `PermissionWall`, `StructuralWallHalt`,
  `RateLimitExhausted`, `NoRoute`, `TaskPreflightFailed`, `Cancelled`); nothing funnels. Implemented faithfully it
  would have recorded the new fields on **2 of 9** outcomes — null on exactly the failure outcomes a
  first-pass-rate comparison depends on — with every guardrail in the plan green. **The enumerating form
  is more helpful when right and silently wrong when stale**, and it goes stale as the code moves with
  nothing watching.
  - **If you state a count anyway, you MUST have run the command, the command ships beside the count, and
    the prompt names the GREP as the authority.** A measured enumeration genuinely beats a bare pointer;
    what is forbidden is an **unmeasured** one — and a measured one arriving without its command is
    unmeasured to everyone downstream. Three parts, all load-bearing. The compliant hedged form (the
    repaired plan-30 task 12, verbatim):
    > **Count the recorders yourself before you edit anything. Grep for `new AttemptRecord` in this
    > file.** At authoring time it returned **nine hits, in nine different methods** — there is no shared
    > recorder here, and nothing funnels. … If your grep returns a different number, **trust the grep**,
    > cover what it found, and say so in your summary.

    Drop the third part and a stale count silently outranks the tree, which is the whole defect.
  - **The rewrites, by claim shape:**

    | Claim shape | Enumerated (avoid) | Executable (write this) |
    |---|---|---|
    | Enumeration | "the sites are `A` and `B`" | "grep for `X`; cover **every** hit" |
    | Routing | "`A` funnels through `B`" | "`B` is one recorder — grep for `X` and establish the real caller set before assuming a single funnel" |
    | Exclusivity | "the only caller of `X`" | "grep for `X` to establish the caller set before you change its signature" |
    | Location | "`File.cs:123`", "around line N" | the durable marker — next bullet (#203) |

  - **The line-number rule below is this rule's LOCATION case; this rule is its generalization.** #203
    forbids a line number for code an earlier-wave sibling will touch first — stale on arrival **by
    construction**. A routing or an enumeration claim rots the same way and for the same reason (the code
    moves, the sentence does not), and it needs **no sibling task** to rot: the plan-30 claim was false on
    the day it was written, about code no task in the plan had touched. So apply **this** rule to every
    prompt in every plan, flat or waved, and layer #203's wave-placement trigger on top where a sibling is
    involved. **A location claim and a routing claim are the same defect in different coats.**
  - **No `validate` check backs this, deliberately (#578).** The claims are prose; their correct and
    incorrect forms are textually identical; nothing here is statically decidable. A mechanical gate would
    look rigorous and certify nothing — the exact defect this repo keeps filing issues about. The two
    gates are this authoring rule and `/guardrails-review`'s #578 probe, which EXECUTES every claim that
    survives. **Surface that probe in the Step 7.4 report.**
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
- **Every SCRIPT guardrail you write is subject to the Step 7.0d author-time smoke-test (#302)** — when
  a `.sh`/`.ps1`/`.py` guardrail is runnable at author time (idempotent, its input in-repo or
  hand-synthesizable, no live dependency), you will EXECUTE it against a VALID and an INVALID sample
  before finishing. Author scripts to BE smoke-testable: keep them idempotent (a temp dir cleaned via
  `trap`/`finally`, never a write into the plan folder), and for a guardrail that **renders or executes
  the task's own not-yet-authored output** (a throwaway workspace, a rendered fixture, an `--input-type`
  block) know the hand-written representative sample you will feed it. `bash -n`/`sh -n` (or a
  `pwsh -NoProfile` parse) is a cheap first pass, never the whole check — a bash quote-stripping bug is
  valid bash that does the wrong thing and only EXECUTION reveals it. (Doctrine:
  `guardrails-domain-knowledge` → author-time smoke-test gate.)

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
0d. **Script-guardrail author-time smoke-test — EXECUTE the runnable ones against a valid + invalid
   sample before finishing (#302).** For every `.sh`/`.ps1`/`.py` guardrail this breakdown GENERATED or
   CHANGED — in ANY of the four folders (`tasks/<id>/guardrails/`, `tasks/<id>/preflights/`,
   `<plan>/guardrails/`, `<plan>/preflights/`) — decide **runnable-at-author-time** = idempotent (no
   persistent workspace side effects; a temp dir torn down via `trap`/`finally` is fine) AND its input is
   in-repo or **hand-synthesizable** AND it needs no live external dependency (no server boot, no network,
   not the full merged HEAD). Then:
   - **Runnable → smoke-test it, two-sided.** Run `bash -n`/`sh -n` (or a `pwsh -NoProfile` parse) FIRST
     as a cheap syntax pass, then EXECUTE the guardrail against **(a) a hand-written representative VALID
     sample** of the checked artifact → assert **exit 0**, and **(b) a deliberately INVALID sample** (break
     the one thing the guardrail exists to catch) → assert **non-zero**. Syntax-lint is
     necessary-not-sufficient — a bash quote-stripping bug is valid bash that does the wrong thing, only
     EXECUTION reveals it (#302 gotcha 1). The **highest-value target** is a guardrail that RENDERS or
     EXECUTES the task's own **not-yet-authored output** (a throwaway workspace, a rendered fixture, an
     `--input-type` block): its real input does not exist until the task runs, so hand-synthesize the
     sample (gotcha 2) — this is precisely the guardrail whose first real execution is otherwise deferred
     to runtime. A guardrail that PASSES the invalid sample has no teeth (fix it); one that FAILS the valid
     sample is a false-red that would dead-end every attempt at `needsHuman` and block downstream (fix it
     before it ever runs). Do this in a scratch temp dir; never leave fixtures in the plan folder —
     **with one narrow, deliberate exception (#468): a SOURCE-SHAPE guardrail over CODE COMMITS its
     pair** to `tasks/<id>/samples/`, so the next edit to the script can re-run it instead of trusting
     that someone repeats this pass by hand. That folder is a `samples/` **sibling** of `guardrails/`,
     never inside it: the loader enumerates every non-`.json` file in a guardrail folder as a guardrail
     (no extension allowlist), so a sample placed there would load as a script guardrail, count toward
     GR2003, and be executed at run time. Everything else stays in the temp dir.
   - **Not runnable → syntax-pass + explicit deferral.** If it needs a live service / the built binary /
     the full merged HEAD, run the syntax pass only, reason explicitly about correctness, and **STATE in
     the report (step 4) that the guardrail could not be author-time-executed and why** — an honest
     deferral, never a silent one.
   Distinct from #248 (which runs the *underlying tool* once to confirm a guardrail's assumption about
   that tool's PRINTED output); here you EXECUTE the guardrail SCRIPT itself against synthesized samples to
   prove its OWN correctness. Report which script guardrails were author-time-executed (valid + invalid)
   and which were deferred with the reason. (Doctrine: `guardrails-domain-knowledge` → author-time
   smoke-test gate; `guardrails-review` re-checks it.)
0e. **Tiering-gate self-review — an unconfigured plan must carry ZERO tier bytes (#225).** Check Step
   0.9's `$tiering` against what the folder actually contains. **If `$tiering = not-configured`** (the
   single-model default — no `routing` block and no `tiering` block in the governing config), sweep
   the generated folder and confirm there is **no `"tier"` key in any `task.json`** (not even
   `"tier": null`), **no `tiering` block in `guardrails.json`**, and **no classification line staged
   for the report** — including a well-meant "tiering: not configured" note, which is itself a
   classification report line (Step 4c.1). The folder must be **byte-identical** to what this skill
   would have produced before #225 existed; one stray key fails DoR Invariant 7 and the committed
   no-`routing` golden that proves it. **If `$tiering = configured`**, confirm the inverse: every
   PROMPT task carries an `action.tier` of exactly `easy` / `medium` / `hard`, the plan-wide
   `tiering.defaultTier` is present, no script task or deterministic guardrail was tagged, each
   surviving prompt-judge guardrail was classified, and the report carries the Step 4c.7 lines.
   Either direction, a mismatch is a self-review finding — fix it HERE, before `guardrails validate`.
0a. **EXECUTE the pure-script guardrails you just emitted — expect RED (#479).** A guardrail that is
    broken, or already green, should never reach the human. This costs seconds and is not optional.

    From the plan's starting workspace, run each **pure-script** guardrail (skip the ones that invoke
    `dotnet build`/`dotnet test` — minutes each; the review's Probe A takes those) and record **exit
    code AND stderr**:

    | observation | meaning | action |
    |---|---|---|
    | exits **1** | correct — the task's work is genuinely not done yet | none |
    | exits **0** | it certifies nothing; the task is passable by doing nothing | **fix before reporting** |
    | **stderr non-empty** | it THREW. With `$ErrorActionPreference = 'Continue'` that is non-fatal, so a broken regex silently skips a comment/string strip and changes the guardrail's meaning | **fix before reporting** |
    | fails to **parse** | a dead-end no retry can fix (#473) | **fix before reporting** |
    | `--filter` matches nothing | a zero-match pass (#455) | **fix before reporting** |

    An exception: a **positive/assert-present** check is *supposed* to be green at the start — a wave
    ENTRY preflight, a `<plan>/preflights/` baseline asserting existing tests already pass (#181), or the
    `<plan>/preflights/01-delegated-decisions-recorded.ps1` check (#500), which asserts artifacts THIS
    breakdown just authored and is therefore green by construction the moment the breakdown did its job —
    it exists to go red on a found-but-unrecorded delegation and on a later hand-edit that breaks the
    `decisions.md`↔prompt pair (it cannot see a delegation that was never scanned — 0d.6). Those are the
    only legitimate green-on-arrival guardrails; everything else that exits 0 here is a finding.

    **Do not read a red baseline as a clean bill of health.** A script has many clauses and one exit
    code, so a clause satisfied on arrival hides behind its siblings' failures. That is the review's
    Probe B (the minimal-gaming mutation), not yours — but say in the report that you ran the baseline
    only, so the next pass knows what is still unchecked.

1. Run `guardrails validate <folder>`. Fix and re-run until exit 0 (or report that
   validation was skipped and why). **This now FAILS a breakdown that OMITS any `writeScope`**
   (**GR2041**, #389 — required on every task): a task that writes nothing to the repo must still
   declare `"writeScope": []`, and any task missing the field is an error to fix here before proceeding
   (self-validation closure).

   **Exit 0 is NOT the gate — exit 0 *plus every WARNING read and dispositioned* is.** `validate`'s
   warnings do not affect the exit code, so a breakdown that stops at the exit code is blind to every
   one of them. **Read the warning list. Treat each warning as a FIRED TRIGGER, not noise to wave
   through** (the phrasing GR2042 already carries at Step 2(b) — it was always the general rule, and it
   applies to the whole WARN class). For each warning, do one of exactly two things before moving on:
   - **FIX it** — the default. A `GR2059` inert wave-root `scope:"integration"` (drop the key, §9.2);
     a `GR2042` structural over-scope (split the task, Step 2(b)); a `GR2026` stale coverage token
     (reconcile guardrail and prompt); a `GR2020` vacuous/over-broad `writeScope` (name a real surface
     or `[]`); a `GR2049` inert tiering tag; a `GR2033` wave-numbering gap; a `GR2058` scan timeout.
   - **DOCUMENT it in the Step 7.4 report** — code, file, and the one-line reason it is correct here
     (e.g. a `GR2026` warning sitting on a #176 negative assertion is the #177 false positive, §4.4 —
     keep the guardrail, name the warning). A warning that is neither fixed nor documented is a
     self-review failure.

   Never silence a warning by weakening the thing it points at, and never re-run until it disappears by
   accident. Two of the WARN codes name a defect the exit code can NEVER express — `GR2059` (a
   protection that is inert) and `GR2042` (a task that will thrash) — and both were shipped green under
   an exit-0-only reading.
2. Optionally run `guardrails plan <folder>` and sanity-check the waves against your
   DAG intent.
3. Once validation passes, run `guardrails graph <folder>` to generate
   `<folder>/diagram.md` and its `<folder>/diagram.html` pan/zoom/fullscreen companion (Mermaid
   `flowchart TD` renders of the task/guardrail DAG — generated artifacts, never hand-edited; see
   `references/schemas.md`). Note the `Diagram (interactive): <file-uri>` line this command prints —
   in Step 7.4 you wrap its `file://` URI in a Markdown link (issues #249 + #256). Then run
   `guardrails lock <folder>` to write the committed `guardrails.baseline` BASE manifest, so a
   future regeneration can preserve any guardrails the human edits in the meantime (§11).
4. Emit the **breakdown report**: task table (id, action kind, guardrails with
   archetype numbers, dependsOn), the inserted-task list with justifications, edge
   justifications, any flagged non-executable plan content, and the **Step 7.0d author-time
   smoke-test outcome** (which script guardrails were EXECUTED against valid + invalid samples,
   and which were deferred as not-runnable-at-author-time with the reason, #302). **Then the
   structural-claim line (#578)** — for every action prompt that states a fact about the codebase (an
   enumeration, a routing or exclusivity claim, a location), name the claim and the **command you RAN** to
   establish it; a prompt that only ships a command and asserts nothing needs no line, and *"no prompt in
   this plan states a structural claim"* is a fine answer, stated rather than left silent. Then surface
   the matching probe: *"`/guardrails-review` #578 — every structural claim a prompt makes about the code
   is EXECUTED, not read."* **Then the source-shape ledger (#468) — one line per source-shape guardrail that survived the demotion gate:**
   the guardrail, the property it asserts, and **why no test could carry it** (why the property is
   unobservable at runtime). A source-shape check over implementation source with no such line is a
   self-review finding — loop back to Step 4 and demote it or justify it. Name the committed
   `.valid`/`.invalid` sample pair for each, and for any **documentation** deliverable state the
   exemption explicitly (*"prose target — no meaningful invalid sample exists; PRECEDENT check applied
   instead, sibling precedent: `<the form the document already uses>`"*), never silently. **Then the seam
   ledger (#382) — the Step 4 table, verbatim**, under a bolded `Seam ledger (#382)` line, in the exact
   six-column form Step 4 rule 6 specifies, **including the zero-row form** (`_No in-process seam is
   substituted by this breakdown's tests._`) when no in-process seam is faked. Add one line for each row
   whose proof landed **later than T\*** (name T\* and why it could not live there) and for each proof that
   **degraded to the #120(b) reflection-plus-contrast form** (name the constructor chain that forced it).
   An **absent** ledger is a self-review finding — loop back to Step 4 and run the analysis. **Then — and
   ONLY when Step 0d.1 set `$delegated` non-empty — the delegated-decision ledger (#500)**, under a bolded
   line reading `Delegated decisions (#500)`, one row per id, in this exact form:

   | id | question | options | chosen | vs `recommended` | consumed by |
   |---|---|---|---|---|---|
   | `cache` | which cache should front it? | `Redis`, `in-memory` | `Redis` | followed | `tasks/04-implement-cache-layer/action.prompt.md` |
   | `ttl` | what TTL should entries carry? | `5m`, `1h` | `1h` | **DEPARTED** — upstream refreshes hourly | `tasks/04-implement-cache-layer/action.prompt.md` |

   Cell rules: **chosen** is one of the options, verbatim; **vs `recommended`** is `followed`,
   **`DEPARTED` + the one-clause reason**, or `no lean` when Charter emitted none — a DEPARTED row with no
   reason is a self-review finding, because departing is allowed and departing silently is the bug;
   **consumed by** is the plan-folder-relative prompt path (the seam ledger's self-checking proof-column
   convention), `plan-shape`, or a JIT-deferred `wave-NN-slug/` — and a deferred row is called out as
   still owed, exactly like a seam ledger `U` row. Beneath the table state the scan result — *"declared N, found N, agree"* —
   and, if they disagreed, that the mechanical re-scan (0d.2) confirmed it and the Charter issue was
   filed. Then name the gate and **what it does not prove**:
   `<plan>/preflights/01-delegated-decisions-recorded.ps1` certifies that every delegated id was recorded
   and folded in — **never that the choice was good, and never that the scan ran at all** (0d.6: a
   breakdown that skimmed the plan emits no ids, so nothing exists to go red; that half is #500's
   `validate`-GR follow-on). State both limits, plus: **how many rows read `followed` on the strength of
   "nothing in the workspace discriminated"** — one such row is honest, a whole column of them is the
   silent default with better typography and the human should see it as one glance; **every `plan-shape`
   row**, which the gate exempts from the fold-in assertion entirely; **every JIT-deferred `wave-NN/`
   row**, still owed; and **the three-file rule for overriding a choice here** (`decisions.md`, every
   consuming prompt, and the preflight's `$expected`) — because this review is exactly where an override
   happens, and missing the third makes the gate red with a remedy that would re-decide it. Judging the
   choice is the human's job at this draft review, which is precisely why the rows
   are in the report and not only in `decisions.md`. **When `$delegated` is empty, none of this appears —
   not even a line saying the plan delegates nothing** (Step 0d's gate; a "no delegated decisions" note is
   itself Charter-shaped output, the same reasoning as the tiering gate's Step 4c.1). **Then, for
   every task carrying a per-test red census (#375), the census line:** the enumerated behaviours, the
   pinned test method name each is bound to, and — stated, never implied — **what the census does not
   prove**: it proves each test is *coupled to the code path* (it fails when the implementation is
   absent), not that its assertion is *correct*. An **invoking**-then-hollow test
   (`var r = sut.Consume(x); Assert.NotNull(r);`) is red on stubs, green after, and **passes**. Closing
   that needs mutation testing; until then the wrong-assertion residual is a human read, and on a
   security-load-bearing task say so plainly rather than letting a green census read as "the tests are
   right". **Surface every
   decision the human should confirm** — chief among them any test-framework or E2E-driver choice:
   state which was used and why (detected in repo / named in the plan / asked via
   `AskUserQuestion` / left as a needs-human halt). A wrong framework poisons every
   downstream test task, so it must never be buried. **When — and ONLY when — Step 0.9 set `$tiering
   = configured`**, add the Step 4c.7 tiering lines: the `tier` column on the task table, the `hard`
   tasks with their one-clause reasons, each surviving prompt-judge guardrail's tier (plus the honest
   note that Stage 1 has no field to write it to), the plan-wide `defaultTier` and that it covers any
   task left untagged **including one hand-added after this breakdown**, and that nothing routes on a
   tier yet. **When tiering is NOT configured, none of that appears — not even a note saying so**
   (Step 4c.1: such a note is itself a classification report line, and the breakdown must be
   byte-identical to a pre-#225 one). **If the plan was UI-facing**, state
   the outcome of the Step 7.0 exit-criteria self-review: each UI surface, the
   `NN-build-ui-<screen>` task that builds it, its UI-presence guardrail (asset-exists +
   served-markup string asserted), and the known UI string the served-markup check greps
   for — or, if a screen could not be built from the plan, the blocking self-review
   failure. Then **embed the generated Mermaid
   block inline** (paste the ```mermaid``` fence from `diagram.md`) so the human sees the
   DAG in chat, and **state the `<folder>/diagram.md` path** explicitly so they can render
   it in GitHub or VS Code. Finally, give the reviewer a one-click link to the interactive
   viewer as the **last line of the report**, formatted as a **Markdown link**:

       [Interactive diagram](<file-uri>)

   For `<file-uri>`, copy — **verbatim, without re-encoding** — the `file://` URI that
   `guardrails graph` printed on its `Diagram (interactive): <file-uri>` line in step 3, and
   wrap it in the `(...)`. Emit the **Markdown link**, never a bare `file://` path in a code
   span and never the raw CLI line: `/plan-breakdown`'s report is rendered as **Markdown** by
   the host (e.g. Claude Code's chat UI), which linkifies `[text](uri)` but not an OSC 8 escape
   or a bare path — so the Markdown form is the only one the reviewer can actually click (#256).
   URI correctness is the CLI's job, not yours: issue #249 made `guardrails graph` build the URI
   from .NET's `Uri` (native drive form, percent-encoded — a space becomes `%20`), so you never
   hand-assemble a `file://` URL from a shell `pwd` (which under Git Bash/MSYS on Windows yields
   the non-resolvable mount form `/f/...` instead of the native `F:/...` a `file://` URI needs).
   The two fixes compose: **#249 guarantees the URI is correct; #256 delivers it in a
   host-clickable form.** (A user who runs `guardrails graph` directly in a raw terminal still
   gets that command's own ready-to-click **OSC 8 hyperlink** — unchanged.)
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

   **1b. Re-run the tiering gate, and preserve a human's tier edits (#225).** Re-run Step 0.9's
   detection against the **CURRENT** config before staging — the gate is a property of the config
   today, not of the last generation. A config that has GAINED a `routing` block since then flips
   `$tiering` to configured, so this regeneration legitimately introduces `action.tier` and a
   `tiering` block where the previous folder had none: say so in the report rather than letting tiers
   appear unexplained. An `action.tier` a human hand-changed on a continuation task is a human edit
   like any other — carry it into the staged `task.json` instead of overwriting it with your fresh
   classification, and note the divergence so they can confirm it; likewise never strip a `tiering`
   block a human added to `guardrails.json`. The reverse case (a `routing` block REMOVED) does not
   license quietly deleting the tiers already in the folder — nothing routes on them and validation
   still accepts them, so surface it as a decision for the human instead of a silent data loss.
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

<!-- BEGIN ADDED SECTION #225 — gated difficulty tiering: classify, default, report (auto-merge friendly; do not merge into prose above) -->
## Step 4c — Difficulty tiering: classify, default, report — ALL of it GATED (#225)

**Read the gate (4c.1) before the rubric.** This entire section is a **no-op** unless Step 0.9 set
`$tiering = configured`. It is the sibling of Step 4a — both attach a per-task attribute to PROMPT
tasks by archetype and report it, never silently — but they answer different questions: `maxTurns`
asks *how much work will this take*, `tier` asks *how much model capability does this need*. Neither
derives from the other (4c.4).

### 4c.1 The GATE — when tiering is NOT configured, emit NOTHING (DoR Invariant 7)

When `$tiering = not-configured` (the single-model default — no `routing` block on any prompt
runner), the breakdown emits:

- **no `action.tier`** in any `task.json` — not `"tier": "medium"`, and **not `"tier": null`** either
  (a null key is still a byte that was not there before);
- **no `tiering` block** in `guardrails.json`;
- **no classification report lines** in the Step 7 report.

A single-model user's breakdown must be **byte-identical to today** — the same bytes this skill
produced before #225 existed. It is worth being blunt about why that is spelled out at this length:
it is the acceptance criterion **most likely to be asserted and least likely to be genuinely
tested**, because "I added the feature and gated it" reads as done long before anyone diffs a real
no-`routing` folder. The proof is external and committed — a golden no-`routing` task folder plus
negative assertions (`tests/Guardrails.Integration.Tests/ModelTiering/`) — so a stray byte here fails
a test, not merely a review.

**The trap, stated plainly: "tiering: not configured" is ITSELF a classification report line — do
NOT emit it.** The reflex this skill trains everywhere else — *surface every decision, never a silent
default* (the #42 test-framework precedent) — is **inverted here, deliberately**. There is no
decision to surface: a plan with no routing config never opted into tiering, so a note explaining
that tiering was skipped is both a diff against the byte-identical baseline and a false signal that a
user who never asked about models must now think about them. **Silence is the specification.** Do not
classify, do not tag, do not mention it, do not add an `(n/a)` tier column, and do not offer tiering
as a suggestion in the report. The only correct output is the output of a skill that never heard of
tiering.

**Nothing else changes across the gate, either.** It is not a switch on *how the plan is broken
down*: sizing (Step 2), the DAG (Step 3), guardrail selection (Step 4), the generative insertions
(Step 5) and `maxTurns` budgeting (Step 4a) are IDENTICAL on both sides of it. Tiering is metadata
about which model should take an already-decided task — see 4c.5.

### 4c.2 What gets classified (when the gate is open)

Two model-driven populations; everything else has no model to route and therefore no tier:

- **Every PROMPT task** (a `.prompt.md` action) → classify it and write `action.tier` into its
  `task.json` (4c.6).
- **Every SURVIVING prompt-judge guardrail** (a `NN-name.prompt.md` in any of the four
  guardrail/preflight folders that passed the Step 4 demotion gate) → classify it and **REPORT** it.
  There is **no schema field to write it to in Stage 1**: `action.tier` is defined on `task.json`'s
  action block only (SSOT §3). **Do NOT invent a `tier:` prompt-frontmatter key** — no loader binds
  one, so it would be authored schema, silently ignored, that a later reader mistakes for a live
  setting. Classify it, report it, and name the gap in the report rather than quietly dropping the
  judge population: the requirement is the classification, not a field that does not exist yet.
- **SCRIPT actions and deterministic guardrails are SKIPPED** — no prompt, no model, no tier (the
  same rule as Step 4a's "Script tasks have no `maxTurns` — skip them"). Never tag them.

### 4c.3 The rubric — `easy` | `medium` | `hard`

Classify on **what the task asks the model to do**, not on how important the deliverable is or how
long the file is. Take the HIGHEST tier whose description fits:

- **`easy` — mechanical and fully specified.** The deliverable's exact shape is already decided and
  the agent's job is to type it out: seed a directory, add a file whose content the plan dictates, a
  rename/move, a config or version edit at a named key, a prose edit whose target text is named. No
  API to discover, no design choice left open, no cross-file reasoning. Its guardrails are typically
  `file-exists` / regex checks.
- **`medium` — ordinary bounded work on a familiar surface (the DEFAULT).** Implement a named
  behaviour so already-authored tests pass; author tests for a behaviour the plan specifies; a
  bounded refactor inside one project. One session, the decisions already made by the plan, on a
  surface an ancestor task or the existing repo has already established. **When two tiers both seem
  to fit, choose `medium`** — it is the honest middle and the one that degrades least in either
  direction.
- **`hard` — discovery, judgement, or integration.** Any one of:
  - the four **turn-expensive archetypes of Step 4a** — integration/smoke/e2e with an in-process
    harness; work against an unfamiliar third-party SDK; terminal aggregation / composition-root or
    entry-point wiring; integrating with a same-plan sibling's not-yet-landed implementation;
  - the task must make a **design decision the plan left open** (it names an outcome, not a shape);
  - its guardrail is a **real-seam / composition-root proof** (#120/#382) — the task has to reason
    about the production assembly, not just about its own file;
  - the deliverable is a **cross-cutting output shape** (#193) whose bytes flow into goldens that
    other, pre-existing tests pin.

Record a **one-clause reason** as you classify — it is what the report prints for every `hard`, and
it is what makes a tier reviewable rather than a vibe.

### 4c.4 `tier` and `maxTurns` are correlated, not the same axis — cross-check, never derive

Step 4a's archetypes appear verbatim in the `hard` list, so most `maxTurns: 75` tasks are `hard`.
They remain different questions: a bulk scripted-ETL task (#100) can burn turns while asking little
of the model, and a small, subtle algorithm can be `hard` in twenty turns. So **do not compute one
from the other** — but DO cross-check, because a disagreement is usually a mistake in one of them:

- a task tiered **`easy` that carries a `maxTurns: 75` bump** is a contradiction — re-read both;
- a task tiered **`hard` with no bump** is fine when the difficulty is judgement rather than
  discovery, but re-check Step 4a's archetypes once before leaving it.

### 4c.5 A tier NEVER weakens verification (the adversarial reading)

A tier is routing metadata. It is **not** a licence to give an `easy` task fewer guardrails, skip its
TDD split, widen its `writeScope`, or soften its `# catches:` line — and **not** an excuse to hand a
`hard` task a prompt-judge where a deterministic check exists. The verification bar is identical at
every tier: the guardrails prove the deliverable, and a deliverable does not become easier to verify
because a cheaper model was pointed at it. If a task feels `easy` *because* its guardrails are thin,
the finding is a thin guardrail (Step 4), not an easy task.

### 4c.6 What to WRITE (Step 6), when the gate is open

1. **Per prompt task — `action.tier` in `task.json`**, alongside `action.maxTurns` / `action.model`:

   ```jsonc
   {
     "description": "Implement <feature> so the tests pass (fill logic over the stubs)",
     "dependsOn": ["NN-author-tests-<feature>"],
     "stableId": "q7m2zd",
     "writeScope": ["src/MyProject/"],
     "action": { "tier": "medium" }
   }
   ```

   The three tokens are matched **VERBATIM** — lowercase `easy` / `medium` / `hard`, no trimming and
   no case-folding — so `"Hard "` or `"Medium"` is a **GR2043 validation error**, not a near-miss the
   loader repairs (the same *preserve the malformed signal* doctrine `action.model` follows for
   GR2030). Step 7.1's `guardrails validate` catches it.

2. **Once per plan — the plan-wide default in `guardrails.json`:**

   ```jsonc
   {
     "version": 1,
     "tiering": { "defaultTier": "medium" }
   }
   ```

   It is a **top-level** block (a sibling of `promptRunners`), and its value is validated at its own
   declaration site — a typo'd default is ONE GR2043 error there, never one per untagged task.
   **Emit `"medium"`** unless the plan says otherwise: the default's whole job is to cover work
   **nobody classified**, and unclassified work is precisely what you must not assume is cheap.

3. **The default covers what the breakdown never saw — it does NOT excuse leaving your own tasks
   untagged.** Resolution is `task.json action.tier` **>** `tiering.defaultTier` **>** `null`,
   evaluated **at load** — which is exactly why it reaches a task **a human hand-adds to the folder
   after this breakdown ran** (and one an editor renamed, and one a later regeneration introduced).
   That hand-added task is the case the plan-wide default exists for. Tag **every** prompt task you
   emit explicitly anyway: a folder that leans on the default is a folder that classified nothing and
   reported nothing while looking configured.

### 4c.7 What to REPORT (Step 7.4), when the gate is open

- Add a **`tier` column** to the task table, with `—` for script tasks (no tier).
- List the **`hard`** tasks with their one-clause reasons — the set a human most wants to challenge,
  and the set whose misclassification costs the most.
- List each **surviving prompt-judge guardrail** with its classified tier, stating that Stage 1 has
  no field to write it to (4c.2) — an honest gap, named rather than hidden.
- State the **plan-wide default** and what it covers: any task left untagged, **including one added
  by hand after this breakdown**.
- State plainly that **nothing routes on a tier yet** in this stage — the plan only gets to *say*
  what it has, and `validate` holds it to that. A reviewer must not read a tier as a model assignment
  that has already happened.

And when the gate is CLOSED: **none of the above appears anywhere in the report** — see 4c.1.
<!-- END ADDED SECTION #225 -->

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

<!-- BEGIN ADDED SECTION #101 — .claude/-deliverable detection: inject needsHarnessWrite + seed a new subdirectory (auto-merge friendly; do not merge into prose above) -->
## Step 5b — Detect a `.claude/` deliverable: inject the `needsHarnessWrite` escape hatch, and seed a new subdirectory (#101 / #191)

A task's action runs as a Claude Code subprocess, whose tool-permission layer **refuses every write
under `.claude/` unconditionally** — a NEW *or* EXISTING file, `acceptEdits` notwithstanding — and the
refusal survives every workaround (PowerShell, `dangerouslyDisableSandbox`; a committed
`.claude/settings.json` grant no longer works either, per #273). A prompt action that writes its
`.claude/` deliverable directly therefore hits the wall on attempt 1 and dead-ends the task at
`needs-human` (SSOT §9.3). The harness ships the escape hatch — `needsHarnessWrite` (#191): the action
hands the file to the .NET harness process (which is NOT subject to that layer) to write on its behalf.
The breakdown KNOWS the task's deliverable is under `.claude/`, so it MUST tell the agent to go
STRAIGHT to that escape hatch — emit `needsHarnessWrite` FIRST, WITHOUT a direct-write probe — rather
than leaving it to discover the wall by writing directly. A direct-write probe wastes a turn and, per
#321, populates the permission-wall tracker that can pre-empt the hatch (the harness now drops the
probe's structural `.claude/` path from the wall tracker when the attempt also emits
`needsHarnessWrite`, but the wasted turn is avoidable by not probing at all).

(The older #101 framing was narrower — "a NEW `.claude/` subdirectory" only — because at the time the
only observed wall was the new-subdirectory barrier. #191 widened the reality: EXISTING `.claude/`
files are refused too. The trigger below is widened to match #191's actual scope.)

**When this fires.** A task whose **primary deliverable is a file inside `.claude/`** —
`.claude/skills/`, `.claude/commands/`, `.claude/hooks/`, `.claude/agents/`, `.claude/contexts/` —
**and whose action is a PROMPT** (a `.prompt.md`). NEW or EXISTING file, NEW or EXISTING subdirectory.
(A SCRIPT action writing `.claude/` is exempt — the harness runs a script directly, not through the
tool-permission layer, so it never hits the wall; that is exactly why the seed task in Rule 2 is a
script.)

**Rule 1 — ALWAYS: inject the `needsHarnessWrite` instruction into the task's `action.prompt.md`.**
Add it as an escape-hatch header, parallel to the `needsHuman` header, **verbatim**:

> Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write —
> the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
> `Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
> harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the
> state-out path. The harness (which is NOT subject to that layer) performs the write directly, then
> your guardrails still run normally against the result. There are two forms, and they are mutually
> exclusive — send exactly one:
>
> - **MODIFYING an existing file — use `edits` (prefer this):**
>   `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
>   [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
>   Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
>   rejected, so include enough surrounding context to make each anchor unique. `old` is matched
>   VERBATIM (exact indentation, punctuation and blank lines; only line endings are tolerated), so
>   copy the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if
>   any one fails, none are written and the file is unchanged. An empty `new` deletes the anchored
>   text. Use `edits` **however large the file is** — its cost scales with your change, not the file.
> - **CREATING a file — use `content`:**
>   `{"needsHarnessWrite": {"path": "<workspace-relative path>", "content": "<full file content>",
>   "reason": "<why>"}}`.
>   Do NOT use `content` to modify a large existing file: the harness refuses full-content mode for an
>   existing target over 64 KB, and re-emitting thousands of lines you did not mean to change risks
>   silently corrupting them.
>
> **If your deliverable spans SEVERAL files, send an ARRAY of those entries in ONE request** — one
> entry per file, mixing `edits` and `content` freely:
> `{"needsHarnessWrite": [{"path": "<file A>", "reason": "<why>", "edits": [...]}, {"path": "<file B>",
> "reason": "<why>", "content": "..."}]}`.
> Do NOT deliver them one per attempt: a failed attempt rolls the workspace back to a clean base, so
> an earlier attempt's write is DISCARDED and progress cannot accumulate. The array is applied
> ATOMICALLY — if any entry fails, nothing is written anywhere and every file is unchanged, so fix the
> entry the message names and re-emit the WHOLE array. One entry per file: two entries naming the same
> file are rejected as ambiguous (merge their changes into a single `edits` array).
>
> If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
> (PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

**Carve-out (#321) — permission files are NOT harness-writable.** `needsHarnessWrite` covers
command/skill/hook/agent (and `contexts/`) deliverables only. The harness will NOT write
`.claude/settings.json` or `.claude/settings.local.json` on an agent's behalf — a human must author
permission-granting settings files. If a task's PRIMARY deliverable IS one of those, do NOT inject
this header (it cannot complete via `needsHarnessWrite`); route that deliverable to a human instead.

**Sizing (#437) — the size wall is on the FILE, not the change.** Because `edits` exists, a task whose
deliverable is a LARGE `.claude/` file is no longer structurally impossible: a five-line correction in a
200 KB skill file is a two-anchor `edits` request. Do NOT split a task, or route it to a human, merely
because the target file is big — split only for the usual Step 2 / #87 / #111 reasons (deliverable
count, blast radius, skill-directory count). The one size rule that remains: a task that must CREATE a
very large `.claude/` file from scratch still pays full-content cost, so keep those genuinely small or
seed the file in a script task first and let the prompt task `edits` it.

**Cardinality (#445) — a multi-file `.claude/` deliverable is ONE fragment with N entries, never one
file per attempt or one file per task.** `needsHarnessWrite` accepts an ARRAY of entries, applied
atomically, so a task correcting the same passage in `SKILL.md`, `references/schemas.md` and
`references/example-breakdown.md` emits ONE request carrying three entries and converges in ONE
attempt. Two things this replaces:
- **Do NOT tell the agent to spread the files across attempts.** That advice was wrong even before the
  array existed: a guardrail failure rolls the segment back to a clean base, discarding the previous
  attempt's write, so the task burns its whole retry budget and lands on `needs-human` every time
  (observed live — attempt 2 reported *"Three files match in the clean base"*).
- **Do NOT split one deliverable into one task per FILE.** That shards by file rather than by
  deliverable — cutting against Step 2c/#87, which sizes by skill DIRECTORY precisely because a skill
  folder is one coherent unit — and buys three agent invocations, three worktrees and three merges plus
  a shared guardrail that fails until the last one lands. Split only for the usual Step 2 / #87 / #111
  reasons.
Unlike `needsHuman` it does NOT short-circuit — the guardrails still run against the harness-written
result — so the task's normal `guardrails/` (file-exists, content checks) stay exactly as they would
for any deliverable, and a guardrail that asserts across ALL of the files (the "still present in: …"
shape) is now satisfiable in a single attempt. (`stagingOutputs`, SSOT §3.5, is an alternative
mechanism the harness also honours; prefer `needsHarnessWrite` — it needs no extra `task.json` contract
and the guardrails verify the real `.claude/` path directly.)

**Rule 2 — ONLY for a brand-new subdirectory: also seed the directory (the #101 mechanism, kept).**
When the target subdirectory does not yet exist (`Test-Path .claude/skills/<name>/` is false at
breakdown time), insert a directory-seed task immediately before the writing task: `NN-seed-<name>-dir`
whose action writes a `.gitkeep` to the target path (e.g. `.claude/skills/survey-eval/.gitkeep`). The
writing task `dependsOn` it. **The seed task MUST be a SCRIPT action** (`action.ps1`/`action.sh`
running `New-Item -ItemType Directory` + a `.gitkeep` write), never a prompt action: a script is not
subject to the tool-permission layer, so it creates the new `.claude/` subdir headlessly — whereas a
prompt seed would hit the same wall it is meant to remove. (It is a script, so it carries no
`maxTurns`.) This deterministic seed is cheap insurance kept alongside `needsHarnessWrite`: it keeps
the directory present for the writing task's own tooling and gives the guardrail below a readable
precondition to assert. An EXISTING subdirectory needs no seed. (When a seed task is undesirable
because a human explicitly owns the directory creation, substitute a `## Pre-conditions` note on the
writing task's prompt instead.)

**Guardrail (seed task).** Give the seed task a `01-dir-seeded.ps1` guardrail asserting the target
subdirectory exists, so a missing seed surfaces as a readable guardrail failure rather than a cryptic
mid-run halt:

```powershell
# catches: a task that writes into a NEW .claude/ subdirectory that was never seeded - a prompt
#          action's tool-permission layer cannot create it headlessly. Assert the target subdir
#          EXISTS before the SKILL.md write is attempted.
$dir = ".claude/skills/survey-eval"
if (-not (Test-Path $dir -PathType Container)) {
    Write-Output "$dir does not exist - seed it (a committed .gitkeep) before the harness run; the tool-permission layer cannot create a new .claude/ subdir headlessly"
    exit 1
}
exit 0
```

Scope the guardrail to the one target subdirectory the task owns. Report the injected
`needsHarnessWrite` instruction, the seed task (or the pre-condition note), and the affected `.claude/`
path in Step 7.

> **Harness relation (NOT a breakdown change).** The harness side is the `needsHarnessWrite` mechanism
> (#191, SSOT §9) and the permission-wall detect-and-halt (#104, SSOT §9.3). The breakdown owns only
> the **detection + instruction-injection + seeding** doctrine above; do not edit `src/**`.
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

<!-- BEGIN ADDED SECTION #254 — waved plans: nested layout + JIT staged breakdown (auto-merge friendly; do not merge into prose above) -->
## Step 0c — Charter `.charter.md` living-document ingestion (INTERACTIVE only, #390–393)

**Where this runs — attended vs unattended.** Only a session with the **Skill tool** reaches here (the
headless/autonomous harness has none — it consumes Charter's *flattened* plain-markdown `charter handoff`
output, so it never sees a `.charter.md`, never parses `:::`, never needs `charter-format`; **no Charter
dependency in the headless path**). But **"has the Skill tool" ≠ "a human is present"**: the JIT
between-wave breakdown auto-fires `plan-breakdown` **unattended** (`autoBreakdown` default, SSOT §14). So the
steps that need a human — the **soft-detection confirm** below, and an **open question's `AskUserQuestion`**
(0c.5) — fire **only when ATTENDED** (a human at a TTY); an **unattended** breakdown never prompts and takes
the deferred branch.

**Detection — `.charter.md` is the trigger; a bare `:::` is only a hint (no-regression-critical).**
- **Primary, authoritative: the input filename ends `.charter.md`** → it is Charter input; run Step 0c and
  set **`$charter = true`**.
- **Secondary, a soft hint only: a `.md` NOT named `.charter.md`** whose body has a directive block **at
  column 0 (`^:::name`) that is NOT inside a fenced code block (```` ``` ````/`~~~`)**. A real plan
  legitimately carries `:::` in a Mermaid `classDef`, a fenced `charter-format` example, or prose — so this
  is a *hint, never a detection on its own*. **Attended:** CONFIRM — *"this looks like a Charter document;
  interpret its `:::` blocks as Charter, or break it down as plain markdown?"*; only a **yes** sets
  `$charter = true`. **Unattended:** never confirm → the unchanged plain path.
- Everything else — a `.md` with no column-0 `:::`, or `:::` only inside fences / as a Mermaid `classDef` —
  is **NOT** Charter input → skip this whole section; **Step 1 runs byte-for-byte as before (no regression).**

**Why interpret via a skill, not a parser (the decoupling).** Guardrails takes **no Charter binary
dependency** and never reimplements Charter's parse. The `:::` block catalog and the `:::question`
open/resolved schema are the **single source of truth in Charter's `charter-format` skill** — a
documentation contract Charter publishes and drift-tests against its own renderer. **Do NOT vendor or
fork that catalog into `references/`**; cite the installed `charter-format` skill.

1. **Discover `charter-format` (G3, #393).** The invoking session loads `charter-format` as a
   **top-level** skill (installed via `charter skills install`, which mirrors `guardrails skills install`
   and drops it into `~/.claude/skills/`). Do NOT reach into another skill's `references/` mid-run — that
   is not how the harness loads references. **If Charter input is detected and `charter-format` is NOT
   available in this session → STOP** with *"run `charter skills install` so plan-breakdown can interpret
   Charter blocks."* Do not guess directive semantics from the block names.

2. **Gate the format version (G1, #391) — a FILE-MARKER check, not CLI drift.** Read the plain-YAML
   frontmatter marker **`charter-format-version: F`** (readable without the skill). Against the loaded
   `charter-format` skill's frontmatter range `[format-min, format-version]`:
   - **A `.charter.md`-named file with the marker ABSENT ⇒ REJECT and stop** (#391): *"this plan has no
     `charter-format-version` marker; re-author it with a current `charter-format`, or hand a plain `.md`."*
     Never silently assume a version.
   - **A soft-detected `.md` (confirmed above) with no marker ⇒ the `:::` was most likely incidental →
     warn and fall through to the plain path.** Do NOT hard-stop a file the user handed as a `.md`.
   - **`F > format-version`** (file newer than the installed skill understands) ⇒ stop: *"run
     `charter skills install` to update `charter-format`."*
   - **`F < format-min`** (file older than the skill still reads) ⇒ stop: *"re-author this plan against a
     current `charter-format`."*
   - **`format-min ≤ F ≤ format-version`** ⇒ proceed.
   This is NOT a `guardrails --version` check — do not compare against the Guardrails CLI; the only
   staleness that matters is the file's format vs the installed `charter-format` range.

3. **Interpret the blocks (G1) — the LOADED catalog is authoritative.** Read each `:::` block's meaning from
   the loaded `charter-format` catalog. **For orientation only (as of `charter-format` v1)** the blocks are
   `:::note`/`:::warn`/`:::comparison`/`:::diagram`/`:::diff`/`:::custom-html`/`:::question` (no
   `:::file-tree`, no `:::annotated-code`) — but **the loaded `charter-format` skill is authoritative**: if it
   defines a block not shown here, trust IT (this inline list has no drift test; the loaded skill does).
   A directive the loaded catalog does NOT define is an **unknown directive**: **warn the human and parse its
   body through as prose context — never silently drop it, and never treat it as a known block.** CommonMark
   prose/headings/lists/tables/code extract exactly as in Step 1. The callout/comparison/diagram/diff/
   custom-html blocks **parse-through as context** — carry their content into the Step 1 work-item table as
   rationale/context, the same as the surrounding prose.

4. **A RESOLVED `:::question` is a settled decision (G1).** A `:::question` whose JSON body carries a
   **non-empty `answer`** array is resolved. **Fold its `answer` in as a decision the human already made** —
   treat it exactly like a choice stated in the plan prose, keeping `options` as rationale. Do NOT re-ask
   it. (Schema is normative in `charter-format`: `id`/`title`/`mode`/`options`/`target`/`answer`; `mode` ∈
   `single`/`multi`/`free-text`/`bool`/`number`; `answer` absent/empty ⇒ open, non-empty ⇒ resolved.)

5. **An OPEN `:::question` is surfaced, never defaulted (G2, #392) — routed by `target` AND attendance.** A
   `:::question` with **absent/empty `answer`** is open — resolve it with the "surface it, never default it"
   idiom this skill uses for a greenfield unmade decision (Step 5 / `references/stacks/dotnet.md`), routed by
   the question's `target`:
   - **`target: human` (or unsure), ATTENDED (a human at a TTY):** `AskUserQuestion` with the question's
     `title` + `mode` + `options`; fold the answer in exactly like a resolved `:::question`.
   - **`target: human`, UNATTENDED (auto-fired between-wave breakdown, no human):** emit a task whose action
     prompt writes `{"needsHuman": "<title + options>"}` to the state-out path and stops (the shipped runtime
     escape hatch). **NEVER call `AskUserQuestion` unattended.**
   - **`target: agent`:** the author routed the decision to an agent — the breakdown agent resolves it within
     its authoring judgment and **RECORDS the choice + rationale as a visible decision**. **Never synthesize a
     silent default** for any open question.
   - **No new gate type.** At run time that emitted `{"needsHuman": …}` is an *agent-emitted needs-human*,
     which the autonomous classifier ALREADY governs as a **dial-eligible judgment call**
     (`docs/plans/12-autonomous-mode.md` §4.1): criticality **<** the dial → **proceed with a recorded
     best-guess** (`decisions[]` `proceeded-best-guess` + `autonomy.jsonl`); criticality **≥** the dial →
     **escalate** (honest halt enriched with the question; firstmate answers async via an answer file; run
     exits **`EscalationsPending = 4`**, not `2`). It is not an unconditional halt.

6. **Trust asymmetry (load-bearing — never conflate the two "answers").** A `:::question` resolved **at
   breakdown time** — inline in the `.charter.md`, OR via `AskUserQuestion` — is **trusted authoring
   input**: it shapes the DAG the human then reviews. A `:::question` answered **at run time** through the
   escalation answer channel is **untrusted, delimited data** (`docs/plans/12-autonomous-mode.md` §7.4,
   Finding 4): it only composes the next attempt's prompt and can **never** reach the verdict surface, whose
   deterministic guardrails still gate the result. Fold a breakdown-time answer into the DAG; never treat a
   run-time answer as authoring-trust.

**Then** continue with Step 0's remaining preconditions and Steps 1–8 (or Step 9 if waved) using the
interpreted content: the resolved/asked decisions are now settled inputs to the work-item table, and the
`:::` context rides along as rationale. The produced folder must still pass `guardrails validate` (Step 7).

## Step 0d — Charter's FLATTENED handoff: delegated decisions on the UNATTENDED path (#500)

**This runs on the ordinary `.md` path — the one Step 0c disclaims.** Step 0c is triggered by the
`.charter.md` FILENAME (or an attended `:::` confirm) and says so in its own opening: *"the
headless/autonomous path consumes Charter's flattened `handoff` markdown and never triggers it."* Step 0c
rule 5 already carries the contract for a `target: agent` question — *"the breakdown agent resolves it
within its authoring judgment and RECORDS the choice + rationale as a visible decision. Never synthesize a
silent default."* — written before Charter ever asked for it. **The semantics were never missing; they
were unreachable on the one path that matters.** `charter handoff` flattens a `.charter.md` to plain
CommonMark, this skill reads a delegated question as ordinary prose, whatever the breakdown infers
silently becomes the decision, and **nothing fails**. That is #500 exactly. **This step is the missing
TRIGGER, not a new contract.** It needs no Skill tool, no `charter-format`, no attended human and no
Charter detection — only a grep for two ASCII literals Charter now emits and pins with its own tests
(Charter PR #220 / Charter#219; the exchange is `docs/asks/2026-08-27-charter-reply-marker-implemented.md`).

**PRECEDENCE — Step 0c and Step 0d can never double-handle one plan.**
- **`$charter = true`** (Step 0c interpreted a `.charter.md`, or an attended confirm) ⇒ **Step 0d does not
  run at all.** A `.charter.md` carries its questions as native `:::question` blocks; the marker lines
  below exist only in `charter handoff` OUTPUT, which by construction has not been through Step 0c's
  input. Step 0c rule 5 owns those decisions; this step owns the flattened ones. One plan, one owner.
- **A flattened handoff renamed `.charter.md` cannot slip through as a hybrid** — it carries no
  `charter-format-version` frontmatter, so Step 0c gate 2 REJECTS it and stops before interpreting
  anything. No new rule needed; the existing gate already covers the only way the two could collide.
- **A soft-detected `.md` the human confirmed as Charter but which carries no format marker** takes Step
  0c gate 2's *"warn and fall through to the plain path"* branch ⇒ `$charter` is NOT set ⇒ **Step 0d
  runs.** This is the one branch where a `:::`-looking file still reaches this step, and it is correct
  that it does: whatever those `:::` were, the delegated-decision markers are read on their own literals.
- Everything else — every plain `.md`, every headless/autonomous run, every JIT between-wave breakdown
  (Step 9.5) — reaches Step 0d.

**THE GATE — a plan that delegates nothing produces BYTE-IDENTICAL output to a pre-#500 breakdown.** No
`decisions.md`, no preflight, no ledger line, **and not even a note saying the scan found nothing** — the
same discipline the tiering gate enforces (Step 4c.1 / DoR Invariant 7), for the same reason: a "this plan
delegates nothing" line is itself Charter-shaped output on a plan that never met Charter. This departs
deliberately from the seam ledger (#382), which DOES print a zero-row heading, and the difference is not
an inconsistency to harmonize: the seam ledger's absence is unfalsifiable (nobody can re-derive whether
the analysis ran), whereas this step's absence is checkable in one second by anyone re-running the 0d.1
regex against the plan. Absence is evidence here; there it is not.

### 0d.1 SCAN — run a command, do NOT read for it

The composed breakdown prompt is ~283 KB, almost all inlined skill. **Skim is the real failure mode**, so
the scan is a regex over the plan file, executed, with its output pasted into your working notes. Two
literals, verified by Charter over EMITTED BYTES (not over their constants) and re-verified on our side in
PowerShell:

| Purpose | Pattern | Where |
|---|---|---|
| the ids, one pass | `` ^> \*\*DELEGATED DECISION `([^`]+)`\*\* `` | one per OPEN `target: agent` question |
| the expected total | `` DECISIONS DELEGATED TO YOU: (\d+)\*\* `` | leads the file, **only when non-zero** |

```powershell
$planPath = '<the plan .md>'      # ONE file, by explicit path - never a directory sweep
# -CaseSensitive on purpose: Select-String defaults to case-INSENSITIVE while every regex in the emitted
# gate is .NET-default case-SENSITIVE. Without it a differently-cased marker scans fine here and then
# never matches its own gate - a mismatch that is silent in the direction that looks fine.
$ids = Select-String -LiteralPath $planPath -CaseSensitive -AllMatches `
       -Pattern '^> \*\*DELEGATED DECISION `([^`]+)`\*\*' |
       ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }
$line = Select-String -LiteralPath $planPath -CaseSensitive -Pattern 'DECISIONS DELEGATED TO YOU: (\d+)\*\*' | Select-Object -First 1
$declared = if ($line) { [int]$line.Matches[0].Groups[1].Value } else { $null }   # $null = no count line
"ids: $($ids -join ', ') | found: $($ids.Count) | declared: $declared"
```

POSIX equivalent, portable to BSD `sed`/`grep` (no `grep -P`, which macOS lacks):

```sh
sed -n 's/^> \*\*DELEGATED DECISION `\([^`]*\)`\*\*.*/\1/p' "$planPath"                     # the ids
sed -n 's/.*DECISIONS DELEGATED TO YOU: \([0-9]\{1,\}\)\*\*.*/\1/p' "$planPath" | head -1   # the total
```

Set **`$delegated`** to the captured id list. Empty ⇒ the gate above closes and nothing else in this step
fires.

**`$planPath` is the PLAN OF RECORD, not this invocation's input — and an existing `<plan>/decisions.md`
keeps `$delegated` alive on its own.** Load-bearing for waved plans, where getting this wrong makes the
whole step evaporate. A JIT between-wave breakdown (§9.5) is auto-fired against the wave's seeded
`brief.md`, which is populated from **that wave's section** of the parent plan — but the count line
*"leads the file"*, so it is definitionally not in any wave's section, and a marker blockquote usually
is not either. Scanning the brief therefore returns empty, the gate closes, and a decision the parent
plan delegated is silently dropped at the very moment its consuming wave is being authored. So:
- **Resolve `$planPath` to the parent plan the folder was generated from** (the `.md` beside the plan
  folder), never to a `brief.md`; a brief is a section, not the plan.
- **If `<plan>/decisions.md` already exists, `$delegated` is at minimum its recorded ids** — the record
  already made is authority in its own right, even when this invocation cannot see the source.
- **Only file a Charter bug for a missing count line when you scanned the plan of record.** Markers
  without a count line in a *brief* is a seeding artifact of ours, not a Charter defect.

**Scan the ONE plan file by explicit path — never a directory sweep, and prefer `Select-String`/`grep`
over `rg`.** Measured, not theoretical: with the plan under a path a `.gitignore` covers, `rg -c
"DELEGATED DECISION" .` prints nothing and exits 1 — indistinguishable from "this plan delegates
nothing", which is the very bug this step exists to close, reproduced by the tool. The same `rg` given the
file as an explicit argument finds both markers. (`rg --no-ignore --hidden` also works.) This is the
false-clean that let the "these literals appear zero times in Guardrails" claim circulate between two
repos' docs unchecked — **several `guardrails-review` probes are greps too.**

**Three semantics the scan encodes, from Charter's own emit contract:**
- **The count is what is still OWED**, not how many `target: agent` questions the plan has. An ANSWERED
  agent question emits as `Answered:` and is neither counted nor marked; an OPEN `target: human` question
  gets `> **Open question (unresolved):**` and is not counted either. Do not hand-count questions.
- **A plan delegating nothing carries NO count line** — absence is unambiguous because the markers are
  absent too. But a **missing count line WITH markers present is a Charter bug worth filing**, not "0 or
  an old Charter": process the markers (0d.2) and name the defect in the report.
- **Every id you see carries `options` or a lean.** Charter's `--fail-if-needs-human` blocks unconstrained
  `free-text`/`bool`/`number` delegations on their side, so they never reach us. That carve-out is what
  makes a recorded one-of-N deterministically checkable — the whole gate in 0d.6 rests on it.

### 0d.2 RECONCILE — assert `declared == count(ids)` yourself, and suspect YOURSELF first

**Assert it explicitly. Do not let the gate depend on Charter's wording holding.** Every plural phrasing
of "delegated decision" contains the singular as a substring, so a count line reading
`**DELEGATED DECISIONS: 2**` would make a naive `grep -c "DELEGATED DECISION"` return **3 for two
decisions** — a gate wrong by exactly one, the hardest kind to notice. Charter reversed the words to
`DECISIONS DELEGATED TO YOU` precisely to avoid that and has a test whose only job is to fail if someone
"tidies" it later. Our side still asserts the equality rather than inheriting their care.

On a mismatch, in this order:
1. **Suspect the scan, not Charter.** Re-run the 0d.1 regex mechanically — not by eye, and not by
   re-reading the plan. We are the ones reading 283 KB; skim is overwhelmingly the likelier cause, and
   that is the entire reason Charter was asked for the count line.
2. **If the mechanical re-scan still disagrees, it is a CHARTER bug** — the count is a byproduct of the
   same emit pass, recorded downstream of the one merge that decided a question was open, so
   `declared == count(markers)` is an invariant on their side. File it at
   `Servant-Software-LLC/Charter`, do not work around it silently.
3. **Proceed on the MARKERS either way.** They are the authoritative item list — an id you cannot name is
   an id you cannot record, and a count you cannot resolve to ids certifies nothing. The report says what
   disagreed.

### 0d.3 SETTLE each id — deliberately, within your authoring judgment

This is Step 0c rule 5's contract, now reachable. For each id: choose **exactly one of the `options` on
the metadata line**, in the plan's own terms and the workspace's evidence (the same "trace the real
sibling" discipline as #474 — a decision justified by what the repo already does beats one justified by
taste). Charter's `recommended` is the plan author's lean, not a rule: **departing from it is allowed;
departing SILENTLY is not.** Never leave one open, never emit a `{"needsHuman": …}` task for it — the
author routed it to an agent on purpose, and converting a delegation back into a human halt is a different
kind of ignoring it. If the options genuinely cannot be told apart from the plan and the workspace, choose
the `recommended` one and say in the reason that nothing in the workspace discriminated.

**The metadata line's `id` appears twice by design** (marker line + `_Question — id: …_` line). `charter
verify` cross-checks the manifest against the metadata line, and `charter-format` documents that line as
the uniform shape under every status lead. Do not propose de-duplicating them; it breaks a consumer that
is not us.

### 0d.4 RECORD — `<plan>/decisions.md`, one section per id

Write `<plan>/decisions.md` (plan-folder root, sibling of `guardrails.json`). **Its FORMAT IS A CONTRACT:
the 0d.6 preflight reads it.**

```markdown
# Delegated decisions

Charter delegated 2 decisions to this breakdown (`target: agent`, #500). Each is settled here and folded
into the consuming task's prompt as a stated constraint. `## DECISION <id>` headings are RESERVED for
Charter-delegated ids and are read by `preflights/01-delegated-decisions-recorded.ps1`.

## DECISION `cache` — which cache should front it?

- **Options:** `Redis`, `in-memory`
- **Chosen:** `Redis`
- **Reason:** the workspace already runs Redis for the session store (`src/Api/Startup.cs:44`), so this adds no new dependency.
- **Recommended:** `Redis` — **followed**.
- **Consumed by:** `tasks/04-implement-cache-layer/action.prompt.md`

## DECISION `ttl` — what TTL should entries carry?

- **Options:** `5m`, `1h`
- **Chosen:** `1h`
- **Reason:** the upstream feed refreshes hourly, so a 5m TTL re-fetches identical bytes 12×.
- **Recommended:** `5m` — **DEPARTED**: see the reason above.
- **Consumed by:** `tasks/04-implement-cache-layer/action.prompt.md`
```

Cell rules, so the file is machine-readable and self-checking:
- **The heading is `` ## DECISION `<id>` `` — one line, one regex, sentinel and id together.** That is the
  same shape we asked Charter for, applied to our own file, and for the same reason: split across two
  lines the check becomes two-pass and order-coupled. The `## DECISION ` anchor **plus the backtick** is
  what keeps prose from matching — an unanchored `DECISION` would match this file's own preamble.
- **`` ## DECISION `<id>` `` is RESERVED for Charter-delegated ids.** A human recording a decision of their
  own uses any other heading (`## Decision (human, not delegated) — naming`); the preflight ignores it, so
  the file stays a useful human surface without turning every human edit into a false red.
- **Chosen** — one of the `options`, backticked, verbatim. **Reason** — a real sentence; `TBD`/`TODO`/`—`
  is treated as absent, because a recorded question with no answer is the silent default wearing a
  heading. **Recommended** — Charter's lean and whether this breakdown **followed** or **DEPARTED**.
- **Consumed by** — the **plan-folder-relative** path(s) of the prompt(s) the choice was folded into (the
  seam ledger's self-checking proof-column convention, #382), or the literal token **`plan-shape`** when
  the decision changed which tasks exist rather than what one prompt says. Prose is not a consumer.
  **`plan-shape` must be the WHOLE field** (`` - **Consumed by:** `plan-shape` ``) — it exempts the id
  from the only assertion that reaches outside `decisions.md`, so it may never ride along beside a real
  path as a parenthetical. Reach for it only when there is genuinely no prompt to fold into; **every
  `plan-shape` row is called out in the Step 7.4 ledger as unverified by the gate**, because an
  unfalsifiable token is weaker than the prose it replaced.
- **Values are emitted into the preflight as single-quoted PowerShell literals** — an id or chosen value
  containing an apostrophe must have it DOUBLED there (`'don''t cache'`), or the generated script is a
  parse error, which is a dead-end no retry can fix (#473).
- **A JIT-deferred consumer names the WAVE FOLDER with a trailing `/`** (`wave-02-implement/`) — the one
  case where the consuming task does not exist yet, because §9.5 leaves wave `K+1` a stub. This is the
  seam ledger's `U`-row deferral applied here: the check then asserts only that the wave folder EXISTS,
  which is deliberately weak, and **the JIT re-invocation that authors that wave MUST fold the constraint
  into the real prompt, rewrite this row to the prompt path, and re-emit the preflight.** Without this
  rule a correct waved plan false-reds before its first wave runs — a halt with no available remedy,
  which is the worst failure a pre-DAG gate can have. Do NOT use the trailing-slash form for a task that
  DOES exist; that trades a real assertion for a directory-exists one.
- **NO self-declared count line in this file.** Checking a file's contents against a total the same file
  declares is vacuous; the expected count lives in the preflight, embedded from the plan (0d.6).
- `decisions.md` is authored breakdown content: `guardrails lock` includes it in `guardrails.baseline`, so
  a regeneration merge (Step 8) tracks a human's edits to it like any other authored file. It is inert to
  the harness — not in `PlanDefinitionHash`, not a `validate` subject. **The file is the record; the
  preflight is the gate.** (A `guardrails validate` GR code doing this at breakdown time is the documented
  follow-on under #500, not this step.)

### 0d.5 FOLD IN — the chosen value becomes a stated constraint in the consuming prompt

Recording the decision and then leaving the prompt silent about it just moves the bug one level down: the
executing agent re-decides it at run time, and the run goes green on a different answer than the one on
record. So every consuming `action.prompt.md` gets, verbatim in shape:

```markdown
## Delegated decisions (settled at breakdown time — do NOT re-decide)

`cache` = `Redis`
`ttl` = `1h`

The reasons are in the plan folder's `decisions.md`. Build against these. If one is wrong, halt with
`{"needsHuman": …}` — never silently choose differently.
```

The `` `<id>` = `<value>` `` line is what the 0d.6 check greps for, so it is **anchored on a USE, not a
mention** (#470): a prompt that merely says the word "Redis" somewhere does not satisfy it. One task may
consume several ids; one id may be consumed by several tasks — list each path in `Consumed by:`.

**Write those lines at column 0, one per id, exactly as above** — the check anchors on `^`, so bulleting
them (`` - `cache` = `Redis` ``) or indenting them under the heading makes a correct prompt fail its own
gate. **The MATCHED token is the constraint line, not the heading** — the heading's em dash is prose and
free to change, the same split Charter emits (their ASCII sentinel ends before the em dash begins). Apart
from the id and the value themselves, every byte the check matches on is ASCII, deliberately: this is the
same encoding-round-trip class we asked Charter to avoid, and asking for it while not doing it ourselves
would be the easiest way to lose it.

### 0d.6 CERTIFY — the plan-root preflight, and the exact thing it does NOT catch

*"A prompt may propose, only a deterministic gate may certify."* An instruction with no gate behind it is
this repo's most-repeated defect shape, so 0d.3–0d.5 ship with one: emit
**`<plan>/preflights/01-delegated-decisions-recorded.ps1`**, a plan-root Full Flight Check (the four-folder
model, Step 4). It is a guardrail-shaped FILE — no `task.json`, no action, no `dependsOn` — evaluated ONCE
before the Scheduler builds any wave, so a plan whose delegated decisions were *found but not settled in
writing* **halts at the boundary** instead of shipping an invented decision into every task downstream. No
harness change is required; this ships as a skill edit alone.

> **READ THIS BEFORE TRUSTING IT — the gate is authored by the agent it polices, so it cannot catch a
> breakdown that never RAN 0d.1.** A skimming breakdown leaves `$delegated` empty; the emit-nothing gate
> then fires, no `decisions.md` and no preflight exist, and the run goes green on an invented decision —
> **which is #500, undetected.** What this check actually certifies is narrower and still worth having: a
> breakdown that FOUND the ids cannot then fail to record them, fold them in, or keep the three artifacts
> in agreement, and no later hand-edit can break that pair silently. The scan itself is enforced only by
> prose and the Quality-bar checkbox — i.e. by self-attestation, which is the mechanism #500 already
> proved insufficient. **Closing that needs a check outside the breakdown's own pass: the `guardrails
> validate` GR code that reads the PLAN (0d.4's documented #500 follow-on).** Until it exists, say so in
> the Step 7.4 report rather than letting a green preflight read as "nothing was skimmed."

**DESIGN DECISION — the expected ids and values are EMBEDDED in the check, which asserts only against
`decisions.md` and the prompts. It does NOT grep the source plan. Do not "fix" this back.**
1. **The flattened `plan.md` is a SIBLING of the plan folder, outside it.** A check reaching `..` past its
   own plan folder breaks the folder's self-containment, and the sibling relationship is not even
   invariant — Step 0.1 explicitly allows a repo to keep plan folders under a `.guardrails/` home (#275).
2. **The source plan is INPUT, not a run artifact, and nothing pins its bytes.** It can be re-flattened,
   edited or (on the unattended path, where it is often flattened into a temp dir) simply gone by run
   time. A gate grepping it either false-reds a folder that was correct when authored, or cannot read its
   subject at all — both are run-time halts whose only remedy is a re-breakdown.
3. **Both values are known at breakdown time** (0d.1 captured them), which turns a two-source comparison
   into a one-source assertion.
4. **A plan-root preflight's cwd is the integration WORKTREE, not the plan folder** — so a path-relative
   read of the source plan would resolve somewhere else again. The check resolves `decisions.md` from
   **`$PSScriptRoot`** (the script runs from its real plan-folder location, whatever the cwd), which is
   why it stays correct in serial mode, worktree mode and a `revalidate`.
5. **The acknowledged cost:** an embedded expectation cannot notice that the SOURCE plan changed after the
   breakdown (a re-flatten adding a third delegated decision). That is plan-DRIFT, a different surface —
   #496's plan-hash work — and out of scope here. Widening this check into a drift detector re-imports
   every problem in 1–4.

```powershell
# catches: a decision Charter DELEGATED to this breakdown that the breakdown FOUND and then failed to
#          settle in writing - the recordable half of #500. The flattened plan marked 2 ids
#          `target: agent`; each must appear in <plan>/decisions.md with a CHOSEN value, a REASON, and a
#          consuming prompt that carries the choice as a stated constraint. A missing id, an empty
#          Chosen/Reason, a drifted value or a consumer whose prompt never states it means an invented
#          default is about to be built by every task downstream.
#          NOT caught: a breakdown that never scanned the plan at all - it emits no ids, so this file
#          would not exist. That half needs a check outside the breakdown's own pass (SKILL.md 0d.6).
# The expected ids and chosen values are EMBEDDED here at breakdown time (the 0d.1 scan) and deliberately
# NOT re-grepped from the source plan: plan.md is a SIBLING of this folder, outside it, unpinned and
# possibly absent at run time. Read SKILL.md Step 0d.6 before "fixing" this to read the plan.
# Required-present baseline (#478): every clause below measures 1 against the artifact it scans at author
# time - EXPECTED, because this is a positive/assert-present preflight over breakdown-authored artifacts
# (the green-on-arrival class Step 7.0a exempts, same as the #181 baseline).
$ErrorActionPreference = 'Stop'

$expected = @(
    @{ Id = 'cache'; Chosen = 'Redis' },
    @{ Id = 'ttl';   Chosen = '1h'    }
)

# <plan>/decisions.md resolved from THIS SCRIPT'S OWN location, never from cwd: a plan-root preflight
# runs with cwd = the integration WORKTREE, not the plan folder.
$planRoot  = Split-Path -Parent $PSScriptRoot
$decisions = Join-Path $planRoot 'decisions.md'

if (-not (Test-Path -LiteralPath $decisions)) {
    Write-Output "PRECONDITION: $decisions is missing, but this plan carries $($expected.Count) delegated decision(s): $(($expected | ForEach-Object { $_.Id }) -join ', ')."
    Write-Output "Re-run /plan-breakdown (Step 0d) and record them - never run a plan whose delegated decisions were never settled (#500)."
    exit 1
}

$text     = Get-Content -LiteralPath $decisions -Raw
$problems = New-Object System.Collections.Generic.List[string]

foreach ($d in $expected) {
    $id  = $d.Id
    $esc = [regex]::Escape($id)

    # One line, one regex, sentinel + id together - the shape we asked Charter for, applied to our own
    # file. The '## DECISION ' anchor AND the backtick are what keep prose from matching.
    $head = [regex]::Match($text, '(?m)^## DECISION `' + $esc + '`')
    if (-not $head.Success) {
        $problems.Add("[$id] NOT RECORDED - no '## DECISION ``$id``' section in decisions.md. Charter delegated this decision to the breakdown; it was never settled in writing, so whatever a task infers at run time becomes the decision (#500).")
        continue
    }

    # Section body = this heading to the next '## ' (or EOF).
    $rest = $text.Substring($head.Index + $head.Length)
    $next = [regex]::Match($rest, '(?m)^## ')
    $body = if ($next.Success) { $rest.Substring(0, $next.Index) } else { $rest }

    $chosen = [regex]::Match($body, '(?m)^- \*\*Chosen:\*\* `([^`]+)`\s*$')
    if (-not $chosen.Success) {
        $problems.Add("[$id] NO CHOSEN VALUE - the section exists but carries no '- **Chosen:** ``<value>``' line. A recorded question with no answer is the silent default wearing a heading (#500).")
    }
    elseif ($chosen.Groups[1].Value -ne $d.Chosen) {
        $problems.Add("[$id] CHOSEN VALUE DRIFTED - decisions.md says ``$($chosen.Groups[1].Value)`` but this preflight was generated for ``$($d.Chosen)``. Re-run /plan-breakdown so the record, the prompts and this check agree.")
    }

    $reason = [regex]::Match($body, '(?m)^- \*\*Reason:\*\* (\S.*)$')
    if (-not $reason.Success -or $reason.Groups[1].Value.Trim() -match '^(TBD|TODO|N/?A|-{1,2}|\?+)\.?$') {
        $problems.Add("[$id] NO REASON - '- **Reason:** <why>' is missing or a placeholder. Departing from the plan author's recommendation is allowed; departing silently is not (#500).")
    }

    $consumed = [regex]::Match($body, '(?m)^- \*\*Consumed by:\*\* (\S.*)$')
    if (-not $consumed.Success) {
        $problems.Add("[$id] NO CONSUMER - '- **Consumed by:** ``<plan-relative path>``' is missing (or the literal 'plan-shape'). An unfolded decision is re-decided by the executing agent, which is the same bug one level down.")
        continue
    }

    # The WHOLE field must BE the sentinel - backticked or bare - not merely contain the word. A field
    # reading '`tasks/x/action.prompt.md` (also a plan-shape change)' names a real consumer and must still
    # be checked; matching the word anywhere would skip it, which is the #470 mention-vs-use bug inside the
    # clause that cites #470. Both spellings are accepted because 0d.4 renders the sentinel backticked.
    $consumers = $consumed.Groups[1].Value.Trim()
    if ($consumers -match '^`?plan-shape`?$') { continue }

    $paths = [regex]::Matches($consumers, '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value }
    if (-not $paths) {
        $problems.Add("[$id] CONSUMER NOT A PATH - '- **Consumed by:**' names no ``plan-relative/path`` and is not 'plan-shape'. The check cannot verify a prose consumer.")
        continue
    }

    # Anchored on a USE, not a mention (#470): the prompt must carry the constraint LINE, not the word.
    $constraint = '(?m)^`' + $esc + '` = `' + [regex]::Escape($d.Chosen) + '`'
    foreach ($rel in $paths) {
        $full = Join-Path $planRoot $rel
        if ($rel.EndsWith('/')) {
            # A JIT-deferred consumer: the wave exists but its tasks are not authored yet (SKILL.md
            # 0d.4 / Step 9.5). Assert only that the wave folder is there - deliberately weak, and the
            # wave's own breakdown rewrites this row to the real prompt path and re-emits this check.
            # RESTRICTED to a wave directory (Step 9.1's ^wave-NN-slug$ shape) on purpose: without this,
            # 'tasks/' - or the consuming task's own folder with ONE extra character - silently downgrades
            # "the prompt states the constraint" to "a directory exists", and the doc's "do not use the
            # trailing-slash form for a task that exists" would be an instruction with no gate behind it.
            if ($rel -notmatch '^wave-[0-9]+-[a-z0-9-]+/$') {
                $problems.Add("[$id] DEFERRED CONSUMER NOT A WAVE - '$rel' ends in '/' but is not a 'wave-NN-slug/' directory. Only a not-yet-authored WAVE may defer the fold-in; every other consumer names the consuming action.prompt.md.")
            }
            elseif (-not (Test-Path -LiteralPath $full -PathType Container)) {
                $problems.Add("[$id] DEFERRED CONSUMER MISSING - decisions.md defers this decision to wave '$rel', which is not a folder in this plan.")
            }
            continue
        }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $problems.Add("[$id] CONSUMER MISSING - decisions.md names '$rel' but that file does not exist in the plan folder.")
            continue
        }
        if (-not [regex]::IsMatch((Get-Content -LiteralPath $full -Raw), $constraint)) {
            $problems.Add("[$id] CONSTRAINT NOT FOLDED IN - '$rel' does not carry the line '``$id`` = ``$($d.Chosen)``'. The executing agent will re-decide it (#500).")
        }
    }
}

# Extra sections are DRIFT, not a human's notes: '## DECISION `<id>`' is RESERVED for Charter ids (a human
# recording their own decision uses any other heading, which this check ignores - see 0d.4).
$recorded = @([regex]::Matches($text, '(?m)^## DECISION `([^`]+)`') | ForEach-Object { $_.Groups[1].Value })
foreach ($r in ($recorded | Select-Object -Unique)) {
    if ($expected.Id -notcontains $r) {
        $problems.Add("[$r] RECORDED BUT NOT EXPECTED - decisions.md carries a '## DECISION' section this preflight was not generated for. Either the plan was re-flattened with a new delegated decision (re-run /plan-breakdown) or the heading is a human note that should not use the reserved '## DECISION ``id``' form.")
    }
    elseif (@($recorded | Where-Object { $_ -eq $r }).Count -gt 1) {
        # Only the FIRST section is read above, so a second, contradicting record would be silently
        # ignored - and a Step 8 regeneration merge or a hand-resolved conflict produces exactly that.
        $problems.Add("[$r] RECORDED TWICE - decisions.md carries more than one '## DECISION ``$r``' section. Only the first is read, so the others are invisible; merge them into one before running.")
    }
}

if ($problems.Count -gt 0) {
    Write-Output "=== Delegated decisions NOT settled in writing ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "Charter handed $($expected.Count) decision(s) to the breakdown agent ($(($expected | ForEach-Object { $_.Id }) -join ', ')). Fix <plan>/decisions.md and the consuming prompt(s), or re-run /plan-breakdown - do not start the DAG with a delegated decision unrecorded (#500)."
    exit 1
}

Write-Output "All $($expected.Count) delegated decision(s) recorded with a choice, a reason and a named consumer: $(($expected | ForEach-Object { $_.Id }) -join ', ')."
exit 0
```

Notes that keep the next author from weakening it:
- **It ACCUMULATES, one distinguishable message per clause, dumped once (#478)** — with a single early
  exit for the PRECONDITION (`decisions.md` absent, which would make every clause below crash) and a
  `continue` where a later clause is meaningless without an earlier one. A plan-root gate's failure text
  is the operator's ONLY signal (no retry, no `feedback.md` tail), so every message names the id and the
  remedy.
- **State plainly what it does NOT prove.** It certifies that every delegated id was **recorded and
  folded in** — never that the CHOICE was good. A breakdown that picks badly and writes an honest reason
  passes, exactly as it should: judging the choice is the human's job at the draft review, and the same
  boundary the #375 per-test census draws ("coupled to the code path, not the assertion is correct"). Say
  this in the report; do not let a green preflight read as "the decisions are right."
- **Emit it in the language the workspace can actually run — `.ps1` / `.sh` / `.py`, exactly like the
  #181 baseline.** The `.ps1` above is the realization, not the requirement. `InterpreterMap` maps `.ps1`
  off-Windows to `pwsh` ONLY, and an unresolvable interpreter is a **GR2005 ERROR**, so a `.ps1`-only
  emission on a Linux/macOS box with no PowerShell installed fails the breakdown's own Step 7 `validate`
  and halts the run before task one — a #500-shaped regression on a repo that has never had a delegated
  decision. Port the logic; keep the clauses, the accumulation and the messages.
- **It is green on arrival, by construction — and that is the point.** Step 7.0a's "a guardrail that
  exits 0 at author time certifies nothing" rule has a named exception for positive/assert-present
  preflights (the wave ENTRY gate, the #181 baseline); **this check joins that list.** It goes red when a
  breakdown records a found id badly, when a regeneration drops a section, or when a human edits
  `decisions.md` or a prompt and breaks the pair — **with one timing caveat worth knowing:** the pre-DAG
  phase SKIPS on resume when the journal's `planPreflights` marker is `passed` and its `planHash` still
  matches, and `PlanHash` covers only `guardrails.json` + every `task.json`. So on a FLAT plan a mid-run
  hand-edit to `decisions.md` or a prompt is not re-checked until `--fresh` or a `task.json`-level change.
  It is a draft-review and pre-run gate, not a live invariant.
- **Smoke-test it two-sided at author time (#302, Step 7.0d)** — it is fully runnable-at-author-time
  (idempotent, in-repo input, no live dependency). Run it green against the emitted folder, then against
  a scratch COPY with one `## DECISION` section deleted and expect non-zero. Measured on the reference
  implementation, 20 cases. **Exit 1** for: *decisions.md absent*, *id section deleted*, *Chosen line
  removed*, *Reason placeholder*, *chosen value drifted from the embedded one*, *`Consumed by:` removed*,
  *constraint line dropped from the prompt*, *constraint present with the wrong value*, *consumer file
  missing*, *an unexpected `## DECISION` section*, *the same id recorded TWICE*, *a deferred wave folder
  that does not exist*, *a trailing slash on `tasks/`*, *a trailing slash on the consuming task's OWN
  folder*, and *`plan-shape` appearing as a parenthetical beside a real path whose prompt lost the
  constraint*. **Exit 0** for: the valid folder, `plan-shape` as the whole field **backticked AND bare**,
  a human's non-reserved heading, and a JIT-deferred `wave-NN-slug/` whose folder is present.
  **Mutate in a scratch copy and assert the mutation actually changed the file**:
  the first pass here used `-notlike` with a backtick pattern, and because backtick is the ESCAPE
  character in a PowerShell wildcard the file was never mutated and the clause "passed" its own negative
  sample. A negative sample that does not bite is a green with no information in it. **Four of the cases
  above were found by an adversarial pass, not by the author** — the backticked `plan-shape` false red,
  the two trailing-slash downgrades, and the duplicate section — which is the argument for running that
  pass with someone who did not write the check.
- **Ordinal position is cosmetic.** Every file in `<plan>/preflights/` is evaluated — the phase collects
  failures rather than short-circuiting — so this sitting beside a `01-baseline-<area>-tests-green.ps1`
  needs no renumbering. Names must stay distinct (duplicate check names are a validation error).

### 0d.7 REPORT and REGENERATE

The Step 7.4 ledger is specified with the rest of the report. Two rules that belong here:
- **`decisions.md` and its preflight are ONE artifact in two files and must never drift apart.** A
  regeneration (Step 8) re-runs the 0d.1 scan **against the plan of record** and re-emits BOTH together.
  The `CHOSEN VALUE DRIFTED` clause exists because that pair is exactly what a partial hand-edit breaks.
- **The emit-nothing gate is not a licence to DELETE.** It governs a plan that never delegated anything.
  A regeneration or JIT invocation whose own input happens to yield an empty scan **must leave an existing
  `decisions.md` and its preflight in place** — read the ids from the file and carry them forward (0d.1).
  Deleting a recorded decision because this pass could not see its source is the silent-decision bug with
  extra steps.
- **A human who OVERRIDES a choice at the draft review must edit THREE things:** `decisions.md`, every
  consuming `action.prompt.md`, and the preflight's `$expected` block. Miss the third and the gate reds
  with `CHOSEN VALUE DRIFTED` and points at a remedy — *re-run `/plan-breakdown`* — that would re-decide
  the very question the human just overrode. Say this in the Step 7.4 report when the ledger is emitted,
  because the draft review is exactly where that edit happens. (Editing the preflight body moves
  `PlanDefinitionHash` and so re-arms the review nudge; that is correct, not a bug.)
- **A settled decision is an INPUT to Step 1, not a work item** — the same status a resolved
  `:::question`'s folded `answer` has on the Step 0c path. It shapes the work-item table, the DAG, and
  the guardrails; it never becomes a task of its own.

## Step 9 — Waved plans: nested layout + JIT staged breakdown (#254)

Fires when Step 0.8 set `$waved = true` (the plan is authored as ordered STAGES, each building on the
prior stage's *materialized* artifacts). This is **authoring doctrine** — the harness EXECUTION
contract (wave loop, hard barrier, `WaveDefinitionHash`, cross-wave resume, wave drift) is SSOT §14
and `guardrails-domain-knowledge`; do NOT restate it. Read `references/example-breakdown-waved.md`
alongside this step. The one-line mental model: **a wave is a mini-plan; a waved plan is a
strict-ordered stack of mini-plans sharing one run config, one plan branch, one journal, with a hard
barrier between each.**

### 9.1 The layout you emit — nested, not flat

Instead of a plan-root `tasks/`, emit ordered **wave subfolders**, each a self-contained mini-plan
(SSOT §14.1; the excerpt is `references/schemas.md`):

```
plan-name/
├── guardrails.json                 # ONE shared run config for the whole plan (no per-wave config)
├── state/seed.json                 # optional; ONE continuous state/journal for the whole run
└── wave-01-<slug>/                 # a wave = a mini-plan folder; NN drives strict order
    ├── preflights/                 #   wave ENTRY gate  ("the prior wave's outputs materialized")
    ├── guardrails/                 #   wave EXIT gate   ("this wave's terminal postconditions")
    └── tasks/<NN-verb-object>/…    #   the wave's own task DAG
    wave-02-<slug>/ …
```

- **Wave-dir name MUST match `^wave-([0-9]+)-[a-z0-9-]+$`** — `wave-01-scaffold`, `wave-02-provision`.
  The numeric `NN` is **load-bearing** (unlike the advisory `NN-` on task folders): it drives the
  strict total order. Number them **contiguously from `01`** (a gap is a `validate` **warning**
  GR2033; a duplicate `NN`, or any non-conforming sibling dir next to the wave dirs, is an **error**
  GR2033). There is **no root `tasks/`** in a waved plan (both a root `tasks/` AND wave dirs = **GR2032
  mixed-layout error**).
- Each wave gets its own `preflights/` (entry) and `guardrails/` (exit) folder — the four-folder model
  (SKILL.md Step 4/5) applied at **wave** granularity (§9.2). `guardrails.json`, `state/`, and any
  plan-root `preflights/`/`guardrails/` stay at the plan root and are shared across all waves.
- A plan-root `<plan>/guardrails/` (a whole-plan Terminal Gate) is **optional-additive** for a waved
  plan — the **last wave's exit gate runs on the fully-merged HEAD** and IS the whole-plan terminal
  soundness boundary (SSOT §14.3). Do NOT emit a plan-root terminal gate that merely duplicates the
  last wave's exit gate; add one only for a check meaningful *only once everything is done*.

### 9.2 Each wave is a mini-plan — run Steps 1–8 per wave, wire the two wave gates

For each stage, run Steps 1–8 **scoped to that stage's deliverables**, writing into that wave's
`tasks/`. Everything the flat path does — TDD splitting (Step 2), the sparsest intra-wave DAG (Step 3),
guardrail selection (Step 4), the generative insertions (Step 5), the folder authoring (Step 6) —
applies **inside each wave, unchanged**. What's *new* is the two wave-boundary folders:

- **Wave ENTRY gate = `<plan>/<wave>/preflights/` = "the prior wave's outputs MATERIALIZED".** This is
  the **#181 positive-baseline archetype applied at the wave boundary** (catalogue → "Baseline-green /
  start-from-green (preflight)"; the wave-boundary case). Author a **POSITIVE** check that the concrete
  artifacts this wave builds on (files, symbols, a built binary — the real paths the prior wave
  produced) are **present and real** on the branch before this wave's DAG spends a turn. It is
  **positive-monotone-safe** (assert-present, never "not yet present" — a segment only grows). For
  **wave 1** the entry gate is optional and, if present, is the ordinary plan-start baseline: a
  brownfield green-start (#181) and/or a NEGATIVE "not-yet-produced artifact is absent" fresh-start
  check (the assert-absent baseline = `tests-fail-on-current-code`/`tests-fail-on-stubs` family, not a
  new archetype). *`terminal-gate-of-wave-N == preflight-of-wave-(N+1)`* — the same boundary, two
  authored folders: wave N's exit gate certifies the merged HEAD; wave N+1's entry gate verifies that
  HEAD carries what it depends on.
- **Wave EXIT gate = `<plan>/<wave>/guardrails/` = "this wave's terminal postconditions".** This is the
  Terminal-Gate archetype (Step 4/5) applied per wave. **GR2028 applies PER WAVE**: a **multi-leaf or
  fan-in** wave's exit gate MUST carry **≥1 real integration re-run** — a genuine build/suite invocation
  or a **union-safe** invariant (NOT a tautological `exit 0`). A single-leaf linear wave needs no
  integration guardrail; a plain LOCAL terminal postcondition is fine.
  - **NEVER tag a wave-root gate `scope: "integration"` — it is INERT there (GR2059, #459).** The
    per-union re-verify set is built from the task `tasks/<id>/guardrails/` folders plus the **PLAN-root**
    `<plan>/guardrails/` folder, and nothing else (SSOT §4.3). A wave-root guardrail runs **exactly
    once, on the merged HEAD at the end of its own wave** (SSOT §14.3), tagged or not — so the tag buys
    nothing and the plan merely *looks* protected. Write every wave-exit gate **LOCAL** (no `scope`
    key). Do not relocate a wave-exit gate to the plan root to silence the warning (that changes WHEN it
    runs), and do not pre-empt the open #459 question by tagging one.
  - **A per-union invariant belongs at the PLAN ROOT — and #125/#165 apply THERE.** If a check must be
    re-verified on the merged bytes at every union (including the fan-ins *inside* a wave), author it in
    `<plan>/guardrails/` with `scope: "integration"`, as a **union-safe CONDITIONAL** invariant ("if
    contribution X is present, verify it"). A **terminal postcondition** ("all tests pass", "the full
    build is green") tagged `scope: "integration"` there red-halts a correct partial merge — keep
    whole-build / whole-suite checks **LOCAL** wherever they live. The LAST wave's exit gate is the one
    place a whole-suite `tests-pass` LOCAL check belongs (it runs on the fully-merged HEAD) — the exact
    role the flat plan's terminal `<plan>/guardrails/` folder plays.

### 9.2a Declare the decomposition BEFORE authoring — `state/breakdown-intent.json` (#385/#402)

**Waved plans only** — the harness reads this per WAVE, so a flat plan never gets one, and neither does a
one-ahead **stub** wave (§9.5): a stub has no decomposition yet, and its manifest is written by the
breakdown that later authors it. Inside each wave's Steps 1–8 pass, once that wave's task set is settled
(end of Step 5) and **before you author its first task folder** (Step 6), write the wave's intent manifest:

```jsonc
// <plan>/<wave>/state/breakdown-intent.json  — TRANSIENT: gitignored, in no definition hash
{
  "version": 1,
  "declaredAt": "2026-08-20T05:00:00Z",         // ISO-8601 UTC — NOW, not this literal
  "tasks": [
    { "folder": "01-author-tests-tiering-schema", "purpose": "failing tests for the tiering schema" },
    { "folder": "02-implement-tiering-schema",    "purpose": "make them pass" }
    // … EVERY task folder this wave intends to author, in order
  ]
}
```

**Why FIRST, not last.** Written before authoring, it is a statement of INTENT and the harness can
compare it against what exists on disk. Written afterwards it is a summary — and a summary can never
detect truncation, because a session that was cut off never reaches the line that writes it.

**What it buys.** An interrupted breakdown that HAS a manifest keeps its valid prefix and **resumes**:
11 of 14 authored folders survive and the next segment authors the other 3. With **no** manifest the
cut-off wave is quarantined wholesale — all 14 gone, re-authored from scratch. This file is the whole
difference between those two outcomes; it is not bookkeeping.

Getting it right (the reader is `Guardrails.Core/Loading/BreakdownIntent.cs`; schema SSOT §14.11):

- `folder` is a **bare folder name** under this wave's own `tasks/` — no `tasks/` prefix, no `/` or `\`.
  Entries with a separator, blank entries, and duplicates are **silently dropped**, and a manifest left
  with zero usable entries reads as ABSENT. A typo here costs the salvage without raising anything.
- Declare the names you will actually author. A declared folder counts as authored only once its
  `task.json` **and** its action file both exist, so it stays "owed" until Step 6 finishes it.
- If the decomposition changes while you author (a Step 2 split you only see once you're writing),
  update the manifest in the same step. Over-declaring raises **GR2063 `WaveBreakdownIncomplete`** — a
  WARNING at `validate`, but the harness routes on the code and will not call the wave complete;
  under-declaring silently drops the tail from the salvage.
- **On a RESUME invocation** (the prompt names the folders already complete and the folders still owed)
  the manifest is already this wave's declaration — do **not** rewrite it with just the remainder.
  Author the owed folders; touch the manifest only if the true task set changed.
- Do **not** commit it and never reference it from a guardrail: the harness gitignores
  `/wave-*/state/breakdown-intent.json`, keeps it out of every definition hash, and removes it when the
  wave settles complete. `--fresh` clears it, so a reset wave re-declares from scratch.

### 9.3 Wave-qualified identity, intra-wave `dependsOn`, and the state key

- **`dependsOn` is INTRA-WAVE ONLY.** A task references siblings **within its own wave** by plain
  folder name. Cross-wave ordering is the **barrier's** job — a `dependsOn` edge naming a task in
  another wave is a **hard error (GR2034)**. When a wave-2 task consumes a wave-1 artifact, express it
  as the wave-2 **entry gate** (§9.2, "materialized") + the action prompt reading the real path — never
  a cross-wave edge. Each wave's DAG is self-contained.
- **The canonical task id is the wave-qualified `<waveDir>/<taskFolder>`** (e.g.
  `wave-02-provision/01-author-tests`). This is what the harness uses for the journal key, the resume
  trailer, and — load-bearing for authoring — the **state single-writer key**. So in a waved plan a
  prompt action's state fragment must be keyed by the **wave-qualified id**, not the bare folder name:

  ```json
  { "wave-02-provision/01-author-tests": { "someKey": "someValue" } }
  ```

  A bare `01-author-tests` key is rejected as **foreign** on every attempt (the #164 failure loop, one
  level up). When you emit the Step 6 harness-contract header into a waved-plan prompt, substitute the
  **wave-qualified id** into the `{ "<this-task-folder-name>": { … } }` example and the state-output
  guardrail's index (`$fragment.'wave-02-provision/01-author-tests'.<key>`) — all three must agree.
- **Cross-wave state READ** uses the wave-qualified key of the producing task in an EARLIER wave (e.g.
  `$state['wave-01-scaffold/03-generate']`) and is satisfied by the barrier (GR2022's wave-aware
  branch) — no `dependsOn` edge, and none is possible (it would be GR2034). A **same-wave** read still
  needs the intra-wave `dependsOn` ancestor; a **later-wave** read is an error.

### 9.4 The rest of the doctrine, applied per wave (what shifts)

Everything else in this skill still holds — inside each wave. The ones that visibly shift:

- **Per-wave TDD + insertions (Steps 2/5).** Split code deliverables into author-tests + implement
  **within the wave**; insert wiring/seam/UI/smoke tasks **within the wave** that needs them.
- **Per-wave baseline (#181).** The brownfield green-start baseline is authored **per wave that touches
  a brownfield area** — and for wave ≥ 2 it typically merges into the wave ENTRY gate (§9.2), which is
  already "the prior wave materialized + the area is green". Don't emit a plan-root baseline that
  duplicates a wave entry gate.
- **Per-wave author-time smoke-test (#302, Step 7.0d).** EXECUTE every runnable script guardrail you
  generate in **any** wave's four folders — task-level AND the wave entry/exit gates — against a
  hand-written valid + invalid sample. A wave entry gate that "checks the prior wave materialized" is
  exactly the render/execute-the-not-yet-authored-output shape (§7.0d's highest-value target):
  hand-synthesize a materialized-workspace sample and a missing-artifact sample and run it both ways.
- **Later-wave task references earlier-wave code → durable markers + `maxTurns: 75` (#203, Step 6 /
  Step 4a).** A wave-2 prompt describing wave-1's not-yet-run output is the canonical case for the
  durable-marker + architecture-caveat rule and the fourth turn-expensive archetype — apply BOTH. (The
  JIT flow in §9.5 is the stronger fix: author wave 2 against the REAL materialized code, so there is
  nothing to guess.)
- **Tiering is a PLAN-level config; classification is per wave (#225).** `guardrails.json` is ONE
  shared run config at the plan root, so the `tiering.defaultTier` block is authored **once, at the
  plan root** — never per wave, never duplicated into a wave folder. Classification (Step 4c) then
  runs over each wave's own prompt tasks as that wave is authored — including a wave authored **JIT**
  against the materialized worktree (§9.5 step 3) — and each wave's task table carries its `tier`
  column. The GATE is unchanged and plan-wide: `$tiering = not-configured` ⇒ **no** wave emits a
  tier, no `tiering` block is written, no wave's report mentions tiering, and the whole nested folder
  stays **byte-identical** to a pre-#225 one (Step 4c.1).
- **`guardrails-patterns.md`, stack detection, `$testFramework`, `$e2eStack`, `$tiering`** are
  resolved ONCE for the plan (Step 0), not per wave.

### 9.5 JIT staged-breakdown mode — break down wave N+1 AFTER wave N runs

The KEY multi-wave capability. A downstream wave often **cannot be fully broken down up front**: its
tasks reference artifacts (real file paths, signatures, generated types) that **don't exist until the
prior wave runs**. Guessing them produces stale line-number pointers and unhedged architecture claims
(#203) — the exact failure the durable-marker rule patches over. The clean fix is to author the wave
**against the materialized workspace**.

**Two authoring modes — pick per plan:**
1. **Whole-plan up front (the pre-authored path).** When every downstream wave IS designable up front
   (the artifacts are named/stable in the plan), break down all waves now. This is the
   `examples/waved-hello` shape — validate the whole nested folder, review it, run it straight through.
2. **JIT staged (the incremental path).** When a downstream wave references not-yet-existing artifacts,
   break down **only the ready waves** now and leave the **immediate next** wave as a single **stub folder**
   (the `wave-NN-<slug>/` dir with an **empty `tasks/`**) so the strict order and the numbering are declared
   but its contents are authored later — **one wave visible ahead** (the invariant below, #365). The harness
   **halts honestly** at an empty/unauthored next wave (`RunReport.WaveHalt`, `NextWaveUnauthored`, exit 2),
   pointing at the integration worktree.

**The one-ahead invariant (#365).** At every JIT step **until the final wave**, **exactly one** un-authored
stub wave exists — the immediate next one. That single stub is what makes the wave-aware diagram show a
future-wave node and what makes `guardrails run` reach the JIT checkpoint for the next breakdown. **By default
the stub is auto-seeded with a `brief.md`** (its intent, drawn from that wave's section of the parent plan —
step 1), so the checkpoint **auto-fires the next breakdown** (`autoBreakdown` default-on, SSOT §14.4/§14.10)
and then halts for the human review gate; a stub left brief-less instead **honest-halts**
(`NextWaveUnauthored`, exit 2) for a manual re-invocation. Either way the single stub must be **maintained
one-at-a-time, not stubbed all up front**: author a wave and you MUST re-create (and re-seed) the next stub in
the same step (step 3) if any planned wave remains. Authoring a wave WITHOUT re-creating the next stub
silently drops the forward signal — the diagram stops showing future waves and the run drains to the terminal
gate **as if the plan were complete** (the #365 regression). The **final** wave is the sole exception: nothing
is stubbed after it, and the run then completes at its terminal gate.

**The documented JIT workflow (state it in the Step 7 report when you leave a wave stubbed):**

1. **Break down + review the ready waves.** Author waves `01..K`, `guardrails validate`, then
   `/guardrails-review` them (§9.6). Leave **only wave `K+1`** as a single JIT stub — the declared
   `wave-NN-<slug>/` dir with an **empty `tasks/`** plus an **auto-seeded `brief.md`** at the wave root.
   **Seed `brief.md` from that wave's section of the parent plan `.md`** (the reviewed plan-breakdown input):
   the stage/wave heading and prose describing what `wave-K+1` must accomplish and the upstream artifacts it
   builds on — the wave's **INTENT**, not its tasks (those are authored JIT in step 3 against the materialized
   workspace). If the parent plan has **no identifiable section** for that wave, seed a **minimal template**
   `brief.md` — the wave's title + a `> TODO: describe this wave's intent (what it must accomplish; the
   upstream artifacts it builds on)` placeholder — and **flag it in the report** so the human fleshes it out
   before the checkpoint is reached. **Never leave the stub brief-less by default.** The seeded `brief.md` is
   what makes the JIT checkpoint **auto-fire the next breakdown** (`autoBreakdown` default-on, SSOT
   §14.4/§14.10) and then halt for the human review gate. **Do NOT stub `K+2..N` up front**: each subsequent
   stub is (re-)created — and re-seeded — one at a time as its predecessor is JIT-authored (step 3), which
   keeps the numbering contiguous (GR2033) and the diagram uncluttered (one stub, not N).
2. **Run → the checkpoint auto-fires the next breakdown.** `guardrails run <plan>` executes wave 1 … wave K
   behind the barrier. When it reaches the wave-`K+1` checkpoint, the stub's **seeded `brief.md`** makes the
   harness **AUTO-FIRE `plan-breakdown`** against that brief plus the **materialized integration worktree**
   (`autoBreakdown` default-on, SSOT §14.4/§14.10; the plan's materialized upstream —
   `<worktreeRoot>/<runId>/_integration`, the `#197` "materialized workspace" location; the user's own
   checkout stays read-only for the whole run, SSOT §14 Decision D). The breakdown authors the wave (step 3),
   re-runs `guardrails validate`, and the run then **halts at `BreakdownComplete`** for the human review gate —
   it never auto-satisfies review. *(Opt-out: a stub whose `brief.md` was removed instead **honest-halts**
   (exit 2, `NextWaveUnauthored`) at the same integration-worktree path, for a manual `/plan-breakdown`
   re-invocation.)*
3. **Author wave K+1 against the MATERIALIZED workspace — then re-stub + re-seed K+2 (#365).** The breakdown
   that fires at the checkpoint — **auto-fired by the harness by default**, or your manual `/plan-breakdown`
   re-invocation on the opt-out path — breaks down stage `K+1` **reading the integration worktree**: inspect the
   real files/signatures wave K produced there, so the wave's tasks and guardrails reference bytes that
   ACTUALLY exist. This removes the guesswork (no stale markers, no hedged claims) that the whole-plan-up-front
   path can't avoid. **Declare the decomposition first (§9.2a):** write
   `wave-K+1-<slug>/state/breakdown-intent.json` before the first task folder — this is the invocation where
   truncation actually bites, and that manifest is what makes a cut-off breakdown resumable instead of a lost
   wave. Write the result into `wave-K+1-<slug>/tasks/` (+ its entry/exit gates). **Then restore
   the one-ahead invariant:** if any planned stage beyond `K+1` remains in the plan of record, **create the
   next stub `wave-(K+2)-<slug>/`** (declared dir, empty `tasks/`, contiguous NN) so exactly one wave stays
   visible ahead — and **auto-seed its `brief.md`** exactly as step 1 does: populate it from **`wave-(K+2)`'s
   section of the parent plan `.md`** (its intent + the upstream artifacts it builds on), or, if the plan has
   no identifiable section for it, a **minimal template** (`wave-(K+2)`'s title + a `> TODO: describe this
   wave's intent` placeholder) flagged in the report. **Never leave the re-stub brief-less by default** — the
   seeded `brief.md` is what makes the checkpoint **auto-fire** the `wave-(K+2)` breakdown by default
   (`autoBreakdown` default-on, SSOT §14.4/§14.10), still halting for the human review gate; a human MAY edit
   the seeded brief before the run reaches that checkpoint (or remove it to force a manual re-invocation). If
   `K+1` is the **final** planned wave, create **no** stub (the run then drains to the terminal gate and
   completes). **Regenerate the diagram** (`guardrails graph <plan>`) as part of this step so it shows the
   freshly-authored wave plus the new one-ahead stub node (the `graph`/`plan`/`validate` refresh of §9.6).
4. **Review the freshly-authored wave.** Run `/guardrails-review <plan>/wave-K+1-<slug>` on JUST that
   wave (§9.6 supports a single-wave review) — the same adversarial pass, keyed on that wave's own
   review marker. (Review the authored wave, not the fresh empty `K+2` stub — an empty wave has nothing to
   attack.)
5. **Resume.** `guardrails run <plan>` again — cross-wave resume skips the completed waves and drains the
   newly-authored wave `K+1`, then reaches the `wave-(K+2)` checkpoint step 3 seeded and **auto-fires the next
   breakdown** against the further-materialized integration worktree (halting again at `BreakdownComplete` for
   review). Repeat **step 3 → step 5** for each remaining stub. The loop ends when the **final** wave is
   authored (step 3 created no stub after it) and the run drains to the terminal gate.

**Reading the integration worktree is READ-ONLY input to breakdown** — you inspect it to author
correct evidence; you never write into it (the harness owns it). The materialized upstream is the
authority for every path/signature the new wave references.

### 9.6 Validate + report (waved specifics)

- `guardrails validate <plan>` validates the whole waved plan (all authored waves); `guardrails plan
  <plan>` prints the **wave-aware** preview (waves in strict order, each with its own tiers). Fix to
  exit 0 as in Step 7.1. `guardrails graph <plan>` renders the whole waved DAG (per-wave sub-diagrams
  are a deferred v1 nicety — `graph <plan>/<wave>` is follow-up).
- **`/guardrails-review` reviews wave-by-wave** (each wave is a mini-plan with its own review marker).
  In the JIT flow you review a **single freshly-authored wave**; in the pre-authored flow you review
  the whole plan wave-by-wave. Record it with `guardrails mark-reviewed` (whole plan or per wave).
- **Report additions (Step 7.4):** state that the plan is WAVED and why (the ordered-stages signal);
  the wave list with each wave's entry gate (what materialized artifacts it asserts) and exit gate
  (the terminal check — **always LOCAL**: a wave-root `scope:"integration"` tag is INERT, GR2059/#459,
  because the per-union set is the task folders plus the PLAN root only; state where any genuine
  union invariant was placed instead, i.e. `<plan>/guardrails/`); per wave, the ordinary task
  table; **which waves were authored up front vs left as JIT stubs**, and for each stub the documented
  JIT workflow (§9.5) so the human knows what happens at that checkpoint. On a JIT
  re-invocation (step 3), also state that the freshly-authored wave's **one-ahead stub `wave-(K+2)` was
  (re-)created and its `brief.md` auto-seeded** — name the content source (that wave's parent-plan section,
  or the minimal template + a flag when no section was identifiable) — or that `K+1` was the final wave, so
  no stub was created. **State the new default plainly:** each JIT stub carries a seeded `brief.md`, so the
  run will **auto-break-down** that wave at its checkpoint and then **halt for `/guardrails-review`** (the
  human review gate is never auto-satisfied at any policy — #360/§14.10); a human MAY edit the seeded brief
  (or remove it to force a manual re-invocation) before that checkpoint is reached. Keep the draft-not-done
  closing (Step 7.5) unchanged.

<!-- END ADDED SECTION #254 -->

## Quality bar (verify before declaring done)

- [ ] Stack detected in Step 0; its `stacks/<stack>.md` loaded (or fallback warned if none ships / mixed). `guardrails-patterns.md` read if present.
- [ ] Every emitted task passed the Step 2 over-size split-trigger (no task bundles multiple deliverables, has a wide blast radius, maps 1:1 to a design milestone, or has an expensive retry); any feasibility/self-critique "over-packed"/"~N test refs" signal was carried into sizing and split, not sized 1:1 (#111). Re-checked in the Step 7.0a task-size self-review; any unsplittable over-scoped task is flagged in the report.
- [ ] **(#474) Every task whose deliverable is *"datum D reaches sink S"* had D's path traced BEFORE its `writeScope` was written**: the nearest sibling datum that already makes the whole trip was grepped end to end, every file on that path is in the scope (or the unreachable hop is its own task with the edge wired), and **every construction site of the sink type** was enumerated — not just the one the plan names. Tracing on TYPE NAMES instead of on what the sink actually READS is the measured defect: it passes `validate`, `graph --check` and a full review, then dead-ends the agent at `needsHuman` with a choice between an honest halt and a false green. Reachability only — the rule widens or relocates a scope, and never grades a task's size.
- [ ] Every task has ≥ 1 deterministic guardrail; judges passed the demotion gate and are never alone.
- [ ] **Every guardrail passed the SOURCE-SHAPE demotion order (#468)**: a claim about runtime behaviour is carried by a test (or an AGREEMENT property test for "X must USE Y"), and a source-shape regex survives ONLY for a structural fact with no runtime proxy — with a Step 7.4 report line saying why no test could carry it. No executed-test COUNT is used as an adequacy floor (the #455 zero-match guard is not one). Every surviving source-shape check over CODE ships its committed `.valid`/`.invalid` sample pair in `tasks/<id>/samples/` (a sibling — NEVER inside `guardrails/`, where the loader would load the fixture as a guardrail and execute it), and the WHOLE pair was re-run after every edit to the script; a DOCUMENTATION target is exempt from the pair but not from the PRECEDENT check, and the exemption is named in the report.
- [ ] **Every required-present clause records its MEASURED baseline count (#478)**: the token was run against the exact subject that clause scans, with the clause's own case sensitivity, and the number is written beside it — **0**, or a nonzero with a named reason (preflight / positive baseline, `tests-untouched` regression, the "if X is present" half of a union-safe conditional, a ratcheting behaviour manifest **on a plan regenerated against a partially-landed tree**). No unmeasured "appears nowhere else" claim survives; a nonzero count fixes the CLAUSE, not the comment. Forbidden-present clauses are exempt (a ban green on arrival is a correct ban). Every **multi-clause** guardrail ACCUMULATES — one distinguishable message per clause, dumped once — with early exits only for a PRECONDITION (subject missing or unparseable, so every clause below would crash) or a post-dump behavioural cost stage, both named in the header comment.
- [ ] **No forbidden token collides with what the task REQUIRES (#470)**: for every fail-on-present clause, its literal was reconciled against the same file's required-present clauses (a collision is unsatisfiable-by-construction and dead-ends every attempt) and against the task's own `action.prompt.md`; every forbidden scan runs over STRIPPED source (comments AND string literals) and is anchored on a USE, not a mention.
- [ ] Every guardrail file opens with its `catches:` line.
- [ ] Every guardrail respects the artifact-ancestry rule (files AND state keys) — **including every GATE** (#474): `<plan>/guardrails/` swept against the whole plan's producers, each wave exit gate against its own + earlier waves, each wave entry gate against earlier waves only, and `<plan>/preflights/` against **nobody** (it runs before the DAG, so everything it requires must already exist in the starting bytes).
- [ ] Any task that writes a downstream-read state key carries the fragment-key-present guardrail.
- [ ] New module/project added to a build descriptor → registration guardrail on the descriptor itself.
- [ ] Abstraction consumed by a later task → cross-module reference guardrail on the consumer.
- [ ] Component injected at a composition root (`IFoo`/`FooImpl` pair → factory/DI/`Program.cs`) → a wiring task (construct + inject `FooImpl` into the production assembler) AND a composition-root guardrail that drives the REAL assembler (observable output, or reflection-on-the-constructed-object with a contrast case — the `Factory_Wires*` shape), NEVER injecting the seam in the guardrail and NEVER relying on terminal whole-suite green to cover wiring (#120).
- [ ] Server/executable plan (entry-point-wiring signal) → a wiring task (entry-point-references-launcher grep) AND a live smoke-test task (start → poll route → assert 200 → stop in `finally`) inserted; the polled route is produced by an ancestor.
- [ ] UI-facing plan (described screen/page/browser-served component) → a `build-ui-<screen>` task per surface (alongside its backend, not instead of it) AND UI-presence guardrails: asset-exists (scoped to the page/asset file) + a served-markup assertion EXTENDING the §8 smoke-test (body contains a known UI string, not just HTTP 200), both deterministic — no prompt-judge on visual quality. Exit-criterion naming a UI action ⇒ a task builds that UI, or the Step 7.0 self-review failure is reported.
- [ ] Implementation/inheritance checks use the stack file's structural regex, not a bare keyword grep.
- [ ] Every file-content guardrail is scoped to the one file the task owns (no project-tree greps).
- [ ] Inserted test-author tasks carry the right TDD "red" for the type under test (#155): a BEHAVIORAL type → the task also writes minimal `NotImplementedException` stubs, its `writeScope` covers test + stub file(s), and its guardrails are `build-passes` + `tests-fail-on-stubs`; a DATA MODEL → collapsed to one task (reason stated) or, if split, `tests-fail-on-current-code` + a STRUCTURAL `[Fact]`/`[Theory]` covers-key-behaviors check. Implementation tasks declare a `writeScope` that EXCLUDES the test file but TARGETS the stub file(s) (TDD test-exclusion — replaces the captureHashes/restoreOnRetry/tests-untouched triad).
- [ ] (#455) Every TASK-LEVEL test filter (`tests-pass` AND `tests-fail-on-stubs`, both halves of every TDD pair) names **that pair's OWN test class** — `--filter "Category=<PlanTrait>&FullyQualifiedName~<ThisTaskPairsTestClass>"` — and NO task-level guardrail carries a bare plan-wide trait. The plan-wide trait appears in exactly ONE place: the baseline preflight's `!=` exclusion. The class substring is DISCRIMINATING (checked against every other test class the plan authors — `~Dispatch` also selects `DispatchRouterTests`; namespace-qualify when not), and every narrowed filter carries a **zero-match guard that can actually fire**: keyed on the EXECUTED count (`Passed:` + `Failed:`, NOT `Total:` — which counts `[Skip]`ped tests), with `$env:DOTNET_CLI_UI_LANGUAGE = 'en'` pinned first (the summary line is LOCALIZED — `gesamt:` on a German box), never on the "no tests matched" string (verbosity-dependent, so it never fires — #248), never on a bare `@(<navigation>).Count` where the navigation yields `$null` when nothing ran (`@($null).Count` is **1**, so the guard evaluates `1 -lt 1` — filter with `| Where-Object { $_ }`), and ORDERED by polarity (forward: exit-code check first, so a never-ran test host is not misreported as a bad filter; inverse: guard first, so a crash is not certified as TDD red). The test-author task's `action.prompt.md` **PINS the exact test file + class name** the filter uses — a prompt that leaves the class name to the agent makes a correct filter unwritable and pushes the author back onto the plan-wide trait. `stacks/dotnet.md §4.3` (two classes → parenthesised `|` alternation; no trait → the FQN term alone; a collapsed data-model task still names its class). **And every zero-match guard was PROVEN to fire** — executed against its own zero case (an empty result file, a typo'd filter), not merely authored: all of these traps read correctly on the page and are dead only in execution, and skipping the proof was measured at **11 misdirected findings** naming every pinned behaviour as unbound.
- [ ] **No task authors BOTH the tests and the implementation those tests exercise** (Step 2 rule 5's authorship test) — a composition-root / "wire X into Y" task splits even though its outcome sounds singular, because with no upstream test-author task there is no TDD RED half, only a FORWARD census, and a forward census cannot see a body that can never fail (measured: five pinned method names, four `Assert.True(true)` bodies and one real call that returned early, exit **0** against a completely unwired implementation). Split-trigger (e) splits by COLLABORATOR and does not discharge this. Where the split's red census would demand a test be red that a CORRECT implementation leaves GREEN (the discriminator — "a sound input does NOT halt"), that row is a **declared exemption** carrying `Expect='Executed'` with the structural reason in the guardrail header — never a silently dropped row, and never so many rows that the red census is a forward one wearing its name.
- [ ] **Every required-present clause over a `.md` target strips `<!-- … -->` before matching** and fails on a residual unterminated `<!--`; fenced code blocks are NOT stripped (a fence renders — measured on the SSOT: 43,387 bytes across 26 blocks, 2 of 36 `PlanDefinition` occurrences inside one). Measured hole it closes: a two-token contract check flipping exit 1 → exit **0** on one appended `<!-- TODO: … -->` line. Doc targets are exempt from the `.valid`/`.invalid` sample pair, so this strip is the compensating control, not a nicety.
- [ ] (#375) Every test-author task whose action prompt **enumerates behaviours** and has a **stub tree** emits its red as the **per-test census**, not a suite exit code: every enumerated behaviour bound to a **pinned test METHOD name** (pinned in the prompt, not left to the agent — **`validate` does NOT check this**: measured, GR2026 is blind to the census's name table, so read the prompt and the manifest side by side yourself) and observed **`Failed`** in the **runner's own result file** (TRX; never stdout, never `--list-tests` name discovery — a hollow body satisfies both), with **one accumulated message per unbound behaviour** and a **precondition early exit** that diagnoses a missing result file as *"the run did not happen"* rather than as unbound behaviours. `covers-key-behaviors` is emitted **as well** (naming floor), never instead. No rejection-shaped source regex (`Assert\.Throws` / `Assert\.False`) anywhere — it false-reds a correct `Assert.Equal(RejectedStale, r.Outcome)` and one tautological line satisfies it. The report states the boundary: an **invoking**-then-hollow test passes the census. `stacks/dotnet.md §4.4`.
- [ ] (#154) Every generated test-author `action.prompt.md` carries a **Scope boundary (harness-enforced)** paragraph after the target-file-path statement: it names the exact allowed path(s) (test + stub), states the harness's post-action `git diff` membership check rejects out-of-scope edits, states an out-of-scope edit fails the task and consumes a retry, and redirects an upstream missing-symbol compile error to `{"needsHuman": …}` rather than editing that file.
- [ ] A test-author behavior that needs a production injection seam (a fake/double injected into a type with no injection point) → an upstream `add-<component>-<seam>-seam` task (pure structural production change, build + a structural seam-exists check, TDD-exempt) the test-author task `dependsOn`; the seam was NOT left to the test task to invent or to its `needsHuman` escape (#84).
- [ ] A task that fans out over an external/unknown-size set (crawl, recursive glob, API listing) → modeled as a scripted-ETL `script` action (volume off the turn budget), NOT an agent-per-item loop; discover-size-first probe added where the count is unknown; bulk-capture split from bounded per-item curation (#100).
- [ ] Step 7.0b deliverable-coverage self-review ran: every numbered design deliverable (placement-table row, top-level `§`-section, "what's-asked" item) maps to ≥1 task; any body/`§`-deliverable lacking a milestone home was flagged, not silently dropped; uncovered deliverables are blocking decisions in the report; a `guardrails-review` coverage probe is surfaced (#110).
- [ ] A parallel plan (≥2 leaf tasks or any fan-in) carries a non-empty **`<plan>/guardrails/`** folder (the Terminal Gate) with **≥1 REAL integration-set re-run** — a whole-repo build / full suite / union-safe conditional invariant — NOT a tautological `exit 0` (`validate` enforces this content bar as **GR2028**). There is **NO terminal `integrationGate: true` sink task**; a lingering `integrationGate: true` in any `task.json` is the BLOCKER (a **GR2029** hard error), NOT its absence. The folder's `scope: "integration"` union-guardrail is a **union-safe CONDITIONAL invariant** (conflict-marker-free / "if contribution X present, verify it's real"), NOT the full build or whole suite: a full build (`01-solution-builds`) / whole-suite test (`02-all-tests-pass`) placed in the terminal folder stays **LOCAL** (no `scope` key) — marking it `scope: "integration"` is the #125 anti-pattern (it red-halts correct intermediate unions where downstream TDD tasks have not run yet, #165). (`scope: "integration"` itself is unchanged — the per-union re-verify tag, SSOT §4.3.)
- [ ] Every `dependsOn` edge has a stated justification; no prose-order-only edges.
- [ ] All prompt actions contain the harness-contract block.
- [ ] `promptRunners` present iff any `.prompt.md` exists.
- [ ] Every task has a unique minted `stableId` by default (matching `^[a-z0-9][a-z0-9._-]*$`); on a regeneration, continued tasks reuse their prior id.
- [ ] `guardrails validate` exits 0 (or its absence is loudly reported) **AND every WARNING it printed was read and dispositioned — fixed, or documented in the report with a one-line reason it is correct here.** Warnings do not move the exit code, so exit 0 alone is blind to GR2059 (an inert wave-root `scope:"integration"` — a protection that does nothing), GR2042 (structural over-scope), GR2026, GR2020, GR2049, GR2033 and GR2058. Treat each as a fired trigger, never as noise; a warning neither fixed nor documented is a self-review failure.
- [ ] (#302) Step 7.0d ran: every GENERATED/CHANGED `.sh`/`.ps1`/`.py` guardrail (any of the four folders) that is runnable-at-author-time (idempotent, input in-repo or hand-synthesizable, no live dependency) was EXECUTED against a hand-written VALID sample (exit 0) AND a deliberately INVALID one (non-zero) — `bash -n`/`sh -n` treated as a cheap first pass only, never the whole check; a guardrail that renders/executes the task's own not-yet-authored output was smoke-tested against a synthesized sample; any not-runnable-at-author-time guardrail got the syntax pass + an explicit report deferral (which executed / which deferred and why is in the Step 4 report). Distinct from #248 (which runs the underlying TOOL, not the guardrail script).
- [ ] `diagram.md` generated via `guardrails graph` and its path reported (block embedded inline); the report's **last line** is a **Markdown link** `[Interactive diagram](<file-uri>)` whose `<file-uri>` is copied verbatim from the `file://` URI on `guardrails graph`'s `Diagram (interactive):` line — #249 makes that URI correct (native drive form, percent-encoded, built by the CLI, never hand-assembled from a shell `pwd`); #256 delivers it host-clickable as a Markdown link, not a raw OSC 8 escape or a bare `file://` path in a code span.
- [ ] On fresh generation: `guardrails lock` written (a `guardrails.baseline`). On regeneration: a BASE baseline existed or was established first, and `guardrails merge --apply` succeeded with conflicts resolved beforehand.
- [ ] Output explicitly presented as a draft for human review.
<!-- BEGIN ADDED QUALITY-BAR ITEMS (auto-merge friendly) -->
- [ ] (#578) No action prompt states an **unmeasured** structural claim about the codebase. Where a prompt points at a set of code sites it ships the **command** ("grep this file for `X` and cover every hit") rather than the list — and where it states a count, a routing/exclusivity claim or a location as fact, this pass RAN the command, the command sits in the prompt beside the claim, and the prompt names **the grep, not the number**, as the authority when they disagree. A command followed by a gloss that pre-answers it is the enumerated form wearing a command (the measured plan-30 defect), not a compliance. Line-number pointers take the #203 durable-marker fix — that is this rule's location case, and this rule applies to flat plans and to code no task touches, where #203's wave trigger never fires. The claims and the commands that established them are in the Step 7.4 report, with the `/guardrails-review` #578 probe surfaced. **No `validate` check backs this, deliberately** — the claims are prose and nothing about them is statically decidable.
- [ ] (#94/#204) Every turn-expensive prompt task (integration/smoke/e2e + in-process harness, unfamiliar-SDK discovery, terminal aggregation/wiring, OR integrates with/extends/describes a same-plan sibling's not-yet-landed implementation) carries a per-task `maxTurns: 75` override (`task.json action.maxTurns` or prompt frontmatter); other prompt tasks left at the default; a shared-harness task inserted when ≥2 tasks need the same unfamiliar-SDK setup; the bumps + insertion reported (Step 4a). (#203) A task referencing an earlier-wave sibling's code also gets durable-marker + architecture-caveat prompt text (Step 6) — the two are companion fixes for the same situation, not independent bullets.
- [ ] (#116) Every author-tests task that builds a real git repo reuses a Windows-safe shared `TempGitRepo` fixture (strips read-only before delete, recreates `git rm`/`git mv`-pruned dirs, rolls back via `git reset --hard`, normalizes `core.autocrlf`) OR carries the Windows-Git portability directive; the fixture is authored once and reused, not re-discovered per task (Step 5a; `stacks/dotnet.md §11`).
- [ ] (#101 / #191) Every PROMPT task whose primary deliverable is a file under `.claude/` (NEW or EXISTING file) carries the verbatim `needsHarnessWrite` escape-hatch instruction in its `action.prompt.md`; AND when the target subdirectory is NEW, it also has a directory-seed SCRIPT task (writes a `.gitkeep`) or a `## Pre-conditions` note before it, plus a `01-dir-seeded.ps1` guardrail asserting the subdir exists; the injected instruction, seed, and affected path reported (Step 5b). (SCRIPT actions writing `.claude/` are exempt.)
- [ ] (#87) No emitted task updates ≥2 `.claude/skills/<X>/` directories — multi-skill milestones split into one `NN-update-<skill>-skill` task per directory (each with a directory-narrowed `writeScope`), with golden-example regeneration and round-trip verification as their own downstream tasks `dependsOn` the skill updates (Step 2c).
- [ ] (#41/#78) `$e2eStack` recorded in Step 0 (playwright | cypress | none); for a UI-producing task, Level A (v1 liveness smoke) is added when a driver exists (else the §9 served-markup guardrail is emitted and the Level-A gap reported), an absent driver is surfaced/honest-halted (never scaffolded), Level B (v2 interaction-flow) is documented and surfaced as a v2 decision (never emitted in v1), and a multi-step-interaction exit criterion covered by only served-markup is flagged under-covered in Step 7 (Step 4b/5c; `references/stacks/ui.md`).
- [ ] (#254) FLAT vs WAVED decided in Step 0.8: a plan of ordered STAGES whose later stages build on the prior stage's *materialized* artifacts is emitted as the nested `<plan>/<wave-NN-slug>/{preflights,guardrails,tasks}/` layout (wave dirs match `^wave-([0-9]+)-[a-z0-9-]+$`, contiguous NN, no root `tasks/`); a single-stage/flat plan is NOT waved (fine-grained parallelism is a task DAG inside ONE wave). Steps 1–8 ran per wave (Step 9).
- [ ] (#254) Each wave carries an ENTRY gate (`<plan>/<wave>/preflights/` — a POSITIVE "prior wave's outputs materialized" check, the #181 archetype at the wave boundary, positive-monotone-safe) and, where multi-leaf/fan-in, an EXIT gate (`<plan>/<wave>/guardrails/`) with ≥1 real integration re-run — a whole-repo build/suite invocation or a git-conflict-marker union invariant (GR2028 per wave). **Every wave-root gate is LOCAL — no `scope` key on ANY of them:** a wave-root `scope:"integration"` tag is INERT (**GR2059**, #459) because the per-union re-verify set is the task `<task>/guardrails/` folders plus the **PLAN-root** `<plan>/guardrails/` folder only (SSOT §4.3), so the tag buys nothing and the plan merely *looks* protected. A wave-root gate runs exactly once, on the merged HEAD at its wave's exit (SSOT §14.3). A check that must be re-verified at EVERY union — including fan-ins inside a wave — goes at the **plan root** with `scope:"integration"`, and must be union-safe/conditional there (#125/#165). Do NOT relocate a wave-exit gate to the plan root to silence the warning (that changes WHEN it runs) and do NOT pre-empt the open #459 contract question by tagging. The last wave's exit gate is the whole-plan terminal boundary (no duplicate plan-root gate).
- [ ] (#254) `dependsOn` is INTRA-WAVE only — no cross-wave edge (GR2034); a wave-2 dependency on a wave-1 artifact is expressed as the wave-2 entry gate + the action reading the real path. Every waved-plan prompt action's state fragment is keyed by the WAVE-QUALIFIED id `<waveDir>/<taskFolder>` (not the bare folder name — a bare key is rejected as foreign every attempt); the harness-contract header, the example, and the state-output guardrail's index all use that wave-qualified id.
- [ ] (#254/#360) JIT staged breakdown: a downstream wave whose tasks reference not-yet-existing artifacts is left as a declared stub (empty `tasks/` + an **auto-seeded `brief.md`** — never brief-less by default, §14.4/§14.10), and the Step 7 report documents the workflow (run → the seeded stub **auto-breaks-down at its checkpoint** against the MATERIALIZED integration worktree → **halt for review** → `/guardrails-review` that wave → resume; a brief-less/opt-out stub honest-halts for a manual `/plan-breakdown` re-invocation instead). A wave that IS designable up front is authored up front. Every generated waved script guardrail (task-level AND wave entry/exit gates) got the #302 author-time smoke-test (Step 7.0d).
- [ ] (#365/#360) One-ahead invariant held: the initial JIT breakdown left **only wave `K+1`** stubbed (not `K+1..N`), and every JIT re-invocation (§9.5 step 3) that authored a wave **re-created AND auto-seeded the next `wave-(K+2)` stub** (dir + empty `tasks/` + a `brief.md` populated from that wave's parent-plan section — or a minimal template flagged in the report when no section was identifiable; NEVER brief-less by default, §14.4/§14.10 auto-breakdown-default) whenever a planned wave remained, then **regenerated the diagram** (`guardrails graph`); the FINAL wave got no stub after it. The forward signal is thereby preserved across every JIT step (not just the first), and each seeded stub auto-breaks-down at its checkpoint — still halting for the human review gate.
- [ ] (#225) **The tiering GATE held.** Step 0.9 recorded `$tiering`, and tiering counts as configured ONLY when the governing `guardrails.json` already carries a `routing` block on a prompt runner (or an existing `tiering` block), or the plan EXPLICITLY instructs the breakdown to author per-model routing — never inferred from a plan that merely sounds complex, and `not-configured` when in doubt. When NOT configured, the emitted folder contains **no `action.tier` (not even `"tier": null`), no `tiering` block in `guardrails.json`, and no classification report line — including any "tiering: not configured" note, which is itself one** — so a single-model user's breakdown is **byte-identical** to what this skill emitted before #225 existed (DoR Invariant 7; re-checked in the Step 7.0e self-review and proven externally by the committed no-`routing` golden plus its negative assertions). Sizing, the DAG, guardrail selection and `maxTurns` budgeting are unchanged on both sides of the gate.
- [ ] (#225) When tiering IS configured: every PROMPT task carries an `action.tier` of exactly `easy` | `medium` | `hard` (matched VERBATIM — a stray space or capital is a GR2043 error) classified by the Step 4c.3 rubric with a one-clause reason recorded; every surviving prompt-judge guardrail is classified and REPORTED (Stage 1 has no field to write it to — no invented `tier:` frontmatter key); script actions and deterministic guardrails are left untagged; the plan-wide `"tiering": { "defaultTier": "medium" }` is emitted ONCE in `guardrails.json` to cover anything left untagged **including a task a human hand-adds after the breakdown** (resolved at load: `action.tier` > `defaultTier` > `null`), without excusing an untagged emitted task; no tier weakened a guardrail, a TDD split or a `writeScope` (4c.5); tier vs `maxTurns` was cross-checked, not derived (4c.4); and the Step 7.4 report carries the `tier` column, the `hard` reasons, the judge tiers, the default and its hand-added-task coverage, and the "nothing routes on a tier yet" statement.
- [ ] (#500) **The flattened-Charter delegated-decision scan RAN, as a command.** Step 0d.1's two regexes were executed against the plan file **by explicit path** (never a directory sweep, and not `rg` without `--no-ignore --hidden` — a gitignored plan makes a recursive `rg` print nothing and exit 1, which is indistinguishable from "delegates nothing"), `declared == count(markers)` was asserted **explicitly** rather than inherited from Charter's word order, and a mismatch was re-scanned mechanically (suspecting the 283 KB skim first) before being filed as a Charter bug. **`$charter = true` ⇒ Step 0d did not run** (Step 0c rule 5 owns those; the two can never double-handle one plan). **Markers present** ⇒ every id is settled in `<plan>/decisions.md` under its reserved `` ## DECISION `<id>` `` heading with a Chosen value, a real Reason, the followed/DEPARTED verdict against Charter's `recommended`, and a plan-relative `Consumed by:` path (or `plan-shape`); the `` `<id>` = `<value>` `` constraint is folded into each consuming `action.prompt.md`; `<plan>/preflights/01-delegated-decisions-recorded.ps1` embeds the expected ids/values (it does NOT grep the sibling source plan — Step 0d.6's five reasons) and was two-sided smoke-tested (#302) with the mutation asserted to have actually changed the file; and the Step 7.4 ledger carries a row per id plus the statement that the gate proves recorded-and-folded-in, never that the choice was right. **No markers** ⇒ no `decisions.md`, no preflight, no ledger, **no note saying so** — byte-identical to a pre-#500 breakdown. On a JIT/regeneration invocation the scan targeted the **plan of record**, never a `brief.md` (which structurally cannot carry the count line), and an existing `decisions.md` was carried forward rather than deleted — the emit-nothing gate governs a plan that never delegated, and is never a licence to drop a decision already recorded. And the report states the gate's two limits: it does not prove the choice was good, and **it cannot prove the scan ran** (0d.6 — that half is the `validate`-GR follow-on).
<!-- END ADDED QUALITY-BAR ITEMS -->
