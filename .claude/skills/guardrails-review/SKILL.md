---
name: guardrails-review
description: |
  Adversarial second pass over a generated (and possibly human-edited) Guardrails
  task folder: per task, find the cheapest WRONG implementation that would pass all
  its guardrails. Use when the user says "review this task folder", "run guardrail
  review on <folder>", or after /plan-breakdown produces a draft. Read-only critique
  by default — applies fixes only with per-finding approval, and never deletes a
  human-added guardrail without naming it first.
---

# Guardrail Review

The plan-breakdown skill (and the human after it) decided what the guardrails are.
This skill attacks them. Its one question, asked per task:

> **"What is the cheapest action output that passes ALL these guardrails while not
> actually doing the task?"**

If such an output exists, that's a finding. An empty findings list backed by evidence
("attempted to game tasks 1–5 as follows; couldn't") is a valid outcome — don't pad.

## Procedure

### 1. Inventory
Read `guardrails.json`, every `task.json`, every action and guardrail file. Build the
DAG mentally. Run `guardrails validate <folder>` first — never hand-check what the
tool checks (schema, refs, cycles, zero-guardrail tasks, missing promptRunners).
Run `guardrails plan <folder>` to see the waves.

Then run `guardrails graph <folder> --check`. `diagram.md` is a deterministic
projection of the folder; the human and earlier passes edit guardrails between
breakdown and review, so a stale or missing diagram means the DAG changed since it
was drawn. Branch on the exit code:
- **exit 2** (stale or missing) → regenerate with `guardrails graph <folder>` and
  note in the Step 6 report that the diagram was refreshed to match the current folder.
- **exit 1** (a load/validate error) → do NOT regenerate; surface the error in the
  report — `--check` couldn't even load the plan, so the folder has a deeper problem.
- **exit 0** (fresh) → nothing to do.

### 2. Adversarial pass per task (the heart)
Role-play a lazy or wrong implementer. Concrete probes (mirror of the catalogue's
anti-pattern list — `.claude/skills/plan-breakdown/references/guardrail-catalogue.md`):

- **Tautology**: does any guardrail check something the action itself writes to
  satisfy it? (Action controls the evidence.)
- **Echo-judge**: does a prompt-judge read the action's claims (summary, report
  *about* the work) instead of the raw artifact?
- **Replay-the-action**: does a guardrail re-run the action's own command (a full
  `dotnet build; dotnet test`) when the postcondition is expressible from recorded
  output — a produced artifact or a runner-written TRX (`GUARDRAILS_ACTION_RESULT` /
  `_STDOUT`, SSOT §5.1)? If so, suggest **verify-recorded-action-result** (#9): assert the
  artifact / parse the TRX instead of replaying. (Counter-check: a replay is the HONEST
  gate when no recorded GOOD target carries the postcondition — don't flag it then.)
- **Action-exit-code tautology / echo-judge on action stdout**: does a guardrail test
  `GUARDRAILS_ACTION_RESULT.exitCode -ne 0` (a tautology — the recorded exit code is
  ALWAYS 0 at guardrail time; a non-zero action failed the attempt before guardrails ran),
  or grep `GUARDRAILS_ACTION_STDOUT` for the action's own success word (`"Passed!"`,
  `"Build succeeded"` — an echo-judge, also SDK-version-brittle)? Fix: read a
  runner-written structured result (TRX) or a produced artifact, never the self-report.
- **Hollow output assertion** (#73): for a terminal/e2e guardrail whose task claims a
  **non-empty quantity of output** (migration moved-count, items written, rows produced,
  entities created), does the assertion green-light a **zero/null** result? Tells: a
  keyword-presence regex `Assert.*\([^)]*(Moved|Written|Count|Entities)` (matches
  `Assert.Equal(0, writer.Count)`), a bare `Assert.NotNull(...)`, or an `exit 0` with no
  positive-value check. "It didn't error" / "the keyword is present" is a structural no-op for
  "did anything get produced?" — a run that moved ZERO entities passes. Fix: require a
  **strictly positive** value (`(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)`), or
  read the runner-recorded count / state key and assert `> 0`. (Catalogue → positive-effect /
  non-hollow assertion.) BLOCKER — a zero-effect run goes green.
- **Judge-where-deterministic-possible**: for every `.prompt.md` guardrail, name the
  deterministic archetype that could replace it, or confirm none can (the 4-question
  demotion gate).
- **Over-broad**: "all tests pass" anywhere except a terminal integration task.
- **Missing baseline-green root on a brownfield plan (#181)**: does the plan build onto **existing
  code that already has tests in the touched area** (a brownfield plan — it modifies project(s) with
  existing test coverage), yet carry **no `00-baseline-<area>-tests-green` root**? Without it, a work
  task's `tests-pass` guardrail can fail from PRE-EXISTING breakage (misattributed → wasted retries →
  late `needsHuman`), and a new test's "red" is ambiguous (missing-behavior vs already-broken). The
  baseline-green root is a **no-op `exit 0` action** + ONE guardrail running the EXISTING area tests and
  asserting they PASS on the current code (area-scoped, using the #179 re-emit form), with
  `dependsOn: []` and **every** work task transitively depending on it ("never build on red"). Check
  three things if a baseline IS present: (a) its guardrail targets only the PRE-EXISTING tests, NOT the
  about-to-be-authored red tests (it runs at the root on the starting state — a baseline that would run
  a sibling `author-tests` task's failing tests is mis-scoped); (b) it is genuinely the ROOT (`dependsOn:
  []`) and all work tasks reach it transitively (a baseline no task depends on does nothing); (c) it is
  **distinct** from the terminal full-suite gate (green START at the root vs green END at the sink — a
  plan needs both). The inverse error: a **vacuous baseline on a GREENFIELD plan** (a `dotnet test` over
  a project with zero tests, which trivially passes) — it certifies nothing while looking like a gate;
  greenfield must have NO baseline. Composes with #174 (a RED baseline = a no-op whose guardrail fails →
  the no-op-deadlock short-circuit escalates fast to an actionable `needsHuman`). **WEAK** when the area
  is plausibly green at the start; **BLOCKER** when there is concrete reason the area's existing tests
  are already red (every work task then mis-fails). (Catalogue → "Baseline-green / start-from-green";
  `stacks/dotnet.md §21`. plan-breakdown Step 5 adds the matching insertion rule.)
- **Coverage gap**: the action's stated completion criteria exceed what guardrails
  verify — name the unverified criterion. (E.g. action says "sorted by category";
  no guardrail checks sorting.)
- **Stale coverage check (#157)**: the inverse of the coverage gap — here a
  `covers-key-behaviors` guardrail requires MORE than the action prompt asks for, so a
  CORRECT implementation following the prompt can never satisfy it. For every
  `covers-key-behaviors` guardrail (one `if ($content -match "<token>")` / `-notmatch …
  exit 1` per behavior, or a `$hits -lt N` threshold), verify each required token is named —
  directly or via an obvious synonym — somewhere in the SAME task's action prompt. A token
  the guardrail's `match` requires but the prompt's scenario list does NOT mention →
  **BLOCKER** with the message: "guardrail requires `<token>` but action prompt does not
  mention it — the task will fail every attempt." (Mechanism: the implementation follows the
  prompt, the guardrail keeps demanding the removed token, every attempt gets contradictory
  "need `<token>`" retry feedback and the task dead-ends at `needsHuman`.) This is the
  **human-judgement complement** to the deterministic **GR2026** warning `guardrails validate`
  already emits (SSOT §4.4): the lint is a conservative keyword-presence heuristic (it stays
  silent on a synonym or a regex-shaped token); the reviewer resolves the cases the lint can't
  — confirm a flagged token really is stale (the prompt dropped the scenario) vs named only via
  a synonym (a false positive to clear), and catch a stale token the lint skipped because it was
  regex-shaped. Cross-check `validate`'s GR2026 output against this pass; don't merely re-report it.
- **Tests gameable**: implementation tasks whose tests can be edited by the same
  action — the implementation task's `writeScope` must EXCLUDE the test files its
  upstream test-author task owns (the deterministic write-scope test-exclusion, SSOT
  §3.4), so an edit to a test file fails the harness's read-only write-scope check. An
  implementation task with no `writeScope`, or one whose scope covers the test files, is
  gameable. Inserted test tasks missing the TDD "red" guardrail for their type (#155): a
  BEHAVIORAL-type test-author task missing the `build-passes` + `tests-fail-on-stubs` pair
  (a lone non-zero-exit red passes on a non-compiling garbage test file — BLOCKER); a
  data-model task split without a structural `[Fact]`/`[Theory]` covers-key-behaviors check.
- **Missing scope-boundary warning (#154)**: for any test-author task (`author-tests-*` / a
  task whose deliverable is a test file), check that `action.prompt.md` contains an explicit
  **harness-enforcement paragraph** — it must name the allowed path(s) (test file AND, under
  #155, any stub file the `writeScope` covers), the post-action `git diff` membership check,
  the retry consequence of an out-of-scope edit, and the `{"needsHuman": …}` redirect for an
  upstream missing-symbol compile error. Absence is a **WEAK** finding — the harness injects
  `writeScope` at run time, but without the consequence the agent may still drift on a compile
  error and fix a neighbouring file (an out-of-scope edit that burns a retry). Fix: add the
  Scope boundary paragraph (plan-breakdown Step 6 has the verbatim shape).
- **Unactionable failures**: guardrails that fail without printing a usable reason
  (retry feedback quality).
- **Failure detail lost to the tail (#179)**: for every guardrail that asserts a test suite
  **PASSES** (a `tests-pass` / `all-tests-pass` / `specific-tests-pass`, or a test driving a
  production seam), check it **re-emits the failure DETAIL at the END of stdout**. The harness
  feeds back only the **tail** of a failed guardrail's stdout (last ~60 lines / 4000 chars); default
  `dotnet test` prints each failure's assertion/exception text mid-run and ends with only
  `[FAIL] <name>` + a count. A bare `dotnet test … ; if ($LASTEXITCODE -ne 0) { Write-Output "…";
  exit 1 }` therefore tails out the test NAMES only — the next attempt sees WHAT failed, not WHY,
  and retries blind (plan-0009 burned 12 attempts to `needsHuman`). Tell: no
  `Select-String`/re-emit of failure-signal lines (`Error Message:`, `Assert.`, `Exception`,
  `Expected:`, `Actual:`) after the `dotnet test` call. Fix: the capture → emit-full-log →
  re-emit-at-the-end pattern (catalogue → "Failure detail must reach the retry tail";
  `stacks/dotnet.md §4.2`). **WEAK** (the run still passes/fails correctly; it degrades retry
  feedback, costing attempts). Do NOT flag the INVERSE `tests-fail-on-stubs` /
  `tests-fail-on-current-code` checks — a non-zero exit is their success, so there is no failure
  detail to feed back.
- **Grep-scope contamination**: a file-content guardrail that greps the project tree
  (`Get-ChildItem -Recurse | Select-String`) instead of the one file the task owns — a
  same-wave sibling sharing the term can satisfy it. (Catalogue anti-pattern.)
- **Keyword-not-structural**: an "implements/extends/declares" check matching a bare
  type name (`Select-String "IFoo"`) that a comment, `using`, or local copy satisfies —
  it should match the declaration construct (stack file's structural regex). Also flag an
  **accessor-order-sensitive** structural regex (#112): a property "declared/removed" check
  keyed on a fixed leading accessor — `\{\s*get` or `\{\s*set` (e.g.
  `public\s+\S+\s+NAME\s*\{\s*get`) — is **itself a finding**. C# accessor order is free
  (`{ get; init; }` ≡ `{ init; get; }`), so it **false-passes a removal check** when the field
  survives as `{ init; get; }` (an incomplete refactor ships green) and **false-fails a declared
  check** symmetrically. Fix: match up to the brace (`public\s+TYPE\s+NAME\s*\{`),
  order-insensitive; if accessor presence matters, test `(get|set|init)` anywhere inside the
  block. (Catalogue → member-order insensitivity; `stacks/dotnet.md §3.1`.) BLOCKER on a
  removal check — a lingering field reads as gone.
- **Comment-blind forbidden-keyword scan (#97, #98)**: a guardrail that scans a **source file**
  for **banned** constructs (read-only `MERGE`/`EXEC`/`xp_cmdshell`, no-shell, no-eval,
  no-`console.log`) by matching the **raw file including comments** — `Get-Content $f -Raw` then
  a banned-keyword `-match` with no comment-stripping. It false-POSITIVES on a comment, a string
  literal, or disabled code that *names* the banned thing. The poison case to hunt: the **same
  task** both (a) tells the action to write a **safety-header comment** naming the banned constructs
  AND (b) greps for them without stripping comments — a guaranteed false positive that whack-a-moles
  a CORRECT read-only artifact to `needs-human` (each retry strips one mention, exposes the next).
  Tell: a `Get-Content -Raw` keyword check on a source file with no `/* */` + `--` (or `//`) strip
  upstream of the match. Fix: strip the source language's comments before matching (blank-in-place
  for line-number-reporting checks); and don't pair a header-documenting prompt with a comment-blind
  grep. BLOCKER — a correct implementation fails permanently. (Catalogue → comment-blind keyword
  scan; `stacks/dotnet.md §11`.)
- **Hollow / incomplete derived corpus (#99)**: a task whose deliverable is **derived artifacts
  over a set of inputs** (doc mining, codegen-from-spec, crawl→one-output-per-page, dataset import)
  whose guardrails verify only **shape** — `file-exists` + a marker line — so a green run ships an
  **empty or partial** corpus (worse than a hard failure: it looks done). Three tells: a one-line
  **stub** passes a marker check (F1); an **index** naming only 1 of N outputs "resolves" (F2); a
  crawl capturing **2 of N** pages passes because the checks verify "what I listed exists," never "I
  listed enough" (F3 — look for guardrails iterating the *outputs* rather than the *inputs*). Fix:
  require the four completeness/substance guardrails — input→output coverage, per-output substance
  floor (anti-stub), index completeness (`produced ⊆ indexed`), ingestion lower bound. Name them as
  **lower bounds**, not faithfulness checks (the semantic residual is a human pass or a
  demotion-gated judge — never a judge alone). BLOCKER — a green run ships a hollow/partial corpus.
  (Catalogue → corpus / aggregation completeness.)
- **Terminal-postcondition at integration scope (#125)**: a `scope:"integration"` guardrail that
  asserts a **terminal postcondition** — "the final combined output exists", "the sink wrote its
  aggregate", "all N contributors present" — instead of a **union-safe invariant**. Per SSOT §4.3
  the integration set re-runs at **every** union point (every fan-in / non-FF integration, §5.3 case
  B), on partial merges where downstream tasks have **not run yet** — so a terminal postcondition
  spuriously fails at an intermediate union and escalates a healthy partial merge to `needs-human`
  (surfaced live by `parallel-hello`). Decision test per integration guardrail: *"would this pass on
  a partial merge with a downstream task unsettled?"* If **no**, it is a terminal postcondition
  wearing an integration scope. Fix: keep the integration gate to an invariant true of any valid
  intermediate union ("any produced file present is non-empty + conflict-marker-free"); move the
  terminal assertion to a `local` guardrail on the sink (runs in-attempt on the sink's segment).
  BLOCKER on a parallel plan with unions — it spuriously red-halts a correct run. (Catalogue →
  union-safe integration section; SSOT §4.3/§5.3.)
- **Overlapping writeScopes with no integration union-guardrail (#132)**: when **two or more tasks
  have OVERLAPPING `writeScope`s on a shared file/path** (colliding siblings that can both write the
  same file — AI-merge territory at the union), verify **at least one** `scope:"integration"` guardrail
  (on the integration / fan-in task) asserts the **UNION invariant** on that shared file. The v1 union
  re-verify runs the **integration set ONLY** (SSOT §4.3) — it does NOT re-run a colliding sibling's
  per-attempt `local` guardrails (running them at the union false-fails: fragment-readers checking
  `GUARDRAILS_STATE_FRAGMENT`, anti-tautology `tests-fail-on-current-code`, not-yet-run tasks). So a
  hunk an AI-merge silently DROPS on the shared file is re-verified at the union **only** by an
  integration-scoped guardrail; a drop catchable solely by a sibling's `local` guardrail is NOT
  re-verified there (it surfaces at the terminal gate, or not at all). If **no** `scope:"integration"`
  guardrail asserts the shared-file union invariant → emit a finding **WEAK**: recommend adding one
  (as the texttools showcase does with `components-union-verified` — assert the merged shared file
  still holds every sibling's contribution, union-safe per #125). This is an **authoring nudge**, not a
  harness bug: the integration-set-only union re-verify is an accepted v1 design (#132). (Catalogue →
  overlapping-writeScope union-guardrail; SSOT §4.3 "Accepted residual".)
  - **Duplicate-definition sub-check on a shared CODE file (#175)**: tighten the above when the shared
    overlapping-`writeScope` file is a CODE file and **both** colliding tasks could ADD a type/member
    DEFINITION to it (each writes a `class`/`record`/`interface`/`enum`/method the other doesn't). A
    3-way / AI-merge of two branches that each appended the SAME new definition to **different** regions
    of the file produces **no textual conflict marker** — git keeps both copies — so a conflict-marker
    check passes while the merged file holds a **duplicate definition** (the CS0101 that red-halted
    plan-0009's terminal gate, #175). Require the `scope:"integration"` union-guardrail to carry a
    **duplicate-definition count check** for each definition both siblings could add — count occurrences
    and fail when **>1** (`[regex]::Matches($content,'class\s+<Name>').Count -gt 1` in .NET), naming the
    AI-merge duplicate. Keep it union-safe/conditional (#165) — run it only inside the existing
    file-present gate, so it passes trivially at a union where the file hasn't landed. A conflict-marker /
    contribution-present union-guardrail with **no** duplicate-definition count on a shared code file two
    tasks both define into → **WEAK** (the silent semantic-duplicate residual the harness can only
    *attribute* at the gate, not prevent, SSOT §3.3). (Catalogue → overlapping-writeScope union-guardrail;
    `stacks/dotnet.md §19`; SSOT §3.3 / §4.3 "Accepted residual".)
- **Union guardrail ancestor staleness (#159)**: for every `scope:"integration"` union guardrail on a
  fan-in / integration task, identify each **expected-contribution token** (the string it
  `match`/`notmatch`-checks for in the shared file), and for each token identify which task(s) would
  **produce** it (the task whose action / `writeScope` writes that marker). Then verify every producing
  task is in the **ancestor set** of the integration task — there is a directed path producer → fan-in in
  the DAG. If a producing task is NOT an ancestor — a **disconnected leaf** or a **side branch** with no
  path to the fan-in — flag it **WEAK**: "Union guardrail checks for `<token>` contributed by task
  `<N>`, but task `<N>` is not an ancestor of the integration task. If task `<N>` is later removed, this
  guardrail will fail spuriously. Either add a DAG edge (`<N>` → fan-in task) to make the dependency
  explicit, or remove the `<token>` check from the union guardrail." The trap is silent: a disconnected
  producer **still runs** (the harness executes every task), so the guardrail passes **today** — but the
  integration gate now **implicitly requires** a task no edge declares it depends on, and the day that
  task is deferred/removed the gate red-halts with a confusing "shared file is missing `<token>`" that
  reads as a merge failure but is a **stale guardrail**. This is the run-time-fragility analogue of the
  #132 nudge above — there the residual is a dropped hunk on a shared file; here it is a contribution
  check whose producer fell out of the ancestor set. (Relates to #132/#125; the plan-breakdown side adds
  the matching rule on dependency-edge removal, Step 4.)
- **Unregistered module**: a task adds a module/project to a build descriptor (`.csproj`
  → `.slnx`) but no guardrail checks the DESCRIPTOR names it — a descriptor build passes
  with the project unregistered. (Stack file → build-descriptor registration.)
- **Unreferenced abstraction**: a task creates an abstraction a later task must consume,
  but no guardrail checks the consumer's project file has a `<ProjectReference>` — builds
  pass independently, so a local copy of the interface slips through. (Stack file →
  cross-module reference.)
- **Built-but-unwired component (#120 — the recurring lesson)**: the plan adds an `IFoo`/`FooImpl`
  pair (or any collaborator a production assembler must construct + inject), the component tasks
  build + unit-test it through an injected constructor seam — but **no task constructs `FooImpl` and
  injects it at the production composition root** (factory / `Program.cs` / DI / `RunCommand`), and
  **no guardrail drives the REAL assembler with the new mode active**. Every check is green while the
  feature is inert (reachable only from xUnit, which injects the seam itself); the terminal
  whole-suite gate does NOT cover this. Also flag the inverse: a wiring guardrail that **constructs +
  injects `FooImpl` itself** then asserts it works — it proves the component, never the wiring; the
  guardrail must go through the production assembler (drive the real factory + assert observable
  output, or reflect on the constructed object for the non-null collaborator with a contrast case —
  the `Factory_Wires*` shape). Missing wiring task OR a seam-injecting guardrail OR reliance on
  whole-suite green to cover wiring = BLOCKER. (Catalogue → composition-root section, `stacks/dotnet.md §10`.)
- **Wrong-implementation swap (#158)**: the next failure past #120 — given the dispatch IS wired,
  is the **right concrete type paired with the right mode**? For a dispatch / wiring task that routes
  **≥2 enum (or discriminated) values to ≥2 concrete types** AND whose dispatch tests use
  **seam-injection** (`RecordingImporter` / `FakeHandler` patterns that replace the real impl via DI and
  assert only that *an* importer was called), verify a **per-pairing proximity check** exists — one
  guardrail per pairing asserting `<EnumValue>` sits within a bounded window (`[\s\S]{0,300}`,
  multiline-dotall, both orders) of `<ConcreteType>` in the dispatch file. The swap to hunt: an agent
  routes Mode B → the wrong importer and Mode C → the other; the **build passes** (either type satisfies
  the interface in either branch), the **seam-injected dispatch tests pass** (they never check which
  concrete type was registered), and a **bare keyword check** that all enum values AND all type names
  appear *somewhere* passes too (all present regardless of pairing) — the feature ships inverted on a
  fully green suite. Flag **WEAK if the per-pairing check is missing**; **BLOCKER if the only concrete
  check is `tests-pass` with seam-injection tests** (nothing binds enum to type, so a swap is fully
  invisible). **Do NOT flag** when the dispatch tests already assert the concrete TYPE NAME
  (`Assert.IsType<TcApiLocalImporter>` on the resolved object) — the test catches the swap and the
  proximity check is redundant (the catalogue's decision gate). Distinct from #120 (built-but-unwired):
  there nothing wires the impl at all; here it is wired but possibly to the wrong mode. (Catalogue →
  "Dispatch / factory wiring"; `stacks/dotnet.md §10d`; relates to #120.)
- **Vacuous `writeScope`**: a task declares `writeScope: ["**"]`, a bare top-level dir, or
  any over-broad surface that owns everything — the write-scope check (SSOT §3.4) then
  discriminates nothing and is theater (`validate` warns GR2020). The honest move is to omit
  `writeScope` entirely (reported as a broad surface) rather than emit a vacuous one. Flag
  every `**`/over-broad scope as WEAK and propose either a real surface or omission.
- **Tests not excluded from an implementation scope**: an implementation task with an
  upstream test-author task whose own `writeScope` covers (or fails to exclude) those test
  files — the deterministic "implementation may not write the tests" boundary is open, so the
  implementation can edit the tests to force a tests-pass guardrail green. The implementation
  task's `writeScope` must EXCLUDE every test file the test-author task owns. (BLOCKER — it is
  the TDD test-protection gate.)
- **Missing `writeScope` where one is needed**: a task with a clearly bounded surface (a TDD
  test-author or implementation task, or any task touching one project/file) that omits
  `writeScope` entirely — it gets NO write-scope check, so an out-of-scope escape (including
  an implementation editing the tests) goes uncaught. Omission is correct ONLY for a genuinely
  repo-wide task (a terminal whole-suite gate, a sweeping cross-cutting change); flag a
  confidently-scopable task with no `writeScope` as WEAK and name the surface it should declare.
- **Integration gate missing or empty**: in a plan with ≥2 leaf tasks or any fan-in, confirm
  **exactly one** task declares `integrationGate: true` (the terminal whole-repo sink, SSOT
  §3.3) and that sink carries **at least one** `scope: "integration"` guardrail — an empty gate
  verifies nothing. Zero gates on a multi-leaf/fan-in plan, two-or-more gates, or a gate with no
  integration-scoped guardrail is a BLOCKER (`validate` enforces GR2017/GR2018, but call it out
  with the missing/empty sink named).
- **Full build / whole suite marked `scope: "integration"` on the terminal gate (#165 — BLOCKER)**:
  a **whole-repo build** (`dotnet build <solution>`) or **full test suite** (`dotnet test` with no
  filter) guardrail on the terminal integration gate that declares `scope: "integration"` is the
  **#125 terminal-postcondition anti-pattern**. The integration set re-runs at EVERY union point
  (SSOT §4.3), and a full build/suite is a **terminal postcondition**, not a union-safe invariant: at
  an intermediate union in a TDD plan the merged bytes contain test files referencing types whose
  implementation task has not run yet, so the build/suite FAILS there and the harness rolls the whole
  wave back — even though every per-task guardrail passed (decision test: *"would this pass on a
  partial merge with a downstream task unsettled?"* — a full build/suite answers **no**). Flag it
  **BLOCKER** (it red-halts correct intermediate unions). Fix: drop the `scope` key (make the full
  build/suite **LOCAL** — it then runs only at the terminal gate's own attempt, the correct moment),
  and ensure the gate still carries a separate `scope: "integration"` **union-safe** guardrail to
  satisfy GR2018 (next check). (Catalogue → "A `scope:"integration"` guardrail MUST be UNION-SAFE";
  SSOT §4.3/§5.3.)
- **Terminal gate's integration-scoped guardrail not union-safe / missing (#165, GR2018 — BLOCKER)**:
  the gate sink must carry **≥1** `scope: "integration"` guardrail (GR2018), and that guardrail must
  be a **union-safe CONDITIONAL invariant** — conflict-marker-free / non-empty, or "IF contribution X
  is present, verify it's real" (gate-then-verify), so it passes trivially before a contributing task
  has run. The full build/suite does NOT qualify (previous check). If the gate's ONLY
  integration-scoped guardrail is the build/suite, or its union guardrail is written **unconditionally**
  (`if ($content -notmatch "<token>") { exit 1 }` — requires a contribution that a partial merge may
  not hold yet), flag **BLOCKER**: it either leaves the gate with no real union coverage or red-halts
  a healthy partial merge. Fix: author a conditional union invariant (the `parallel-hello`
  `01-whole-repo-greeting` template; the overlapping-writeScope union-guardrail, #132). (Catalogue →
  union-safe CONDITIONAL form; SSOT §4.3.)
- **Over-scoped task (#111 — is this too coarse to land in one session / retry cheaply?)**: a task
  trips the plan-breakdown Step 2 split-trigger — (a) it bundles multiple distinct deliverables
  ("do X **and** Y **and** Z"); (b) it has a wide blast radius (deletes ≥3 source files, or
  touches ≳10 files / test references in one action); (c) it maps 1:1 to a design milestone / phase;
  or (d) a single failed-guardrail retry re-runs an hour of work (a multi-deletion, a 100+-ref
  re-baseline). An over-scoped task thrashes at run time — every guardrail miss re-runs the whole
  oversized action — and is the most likely `needs-human` in a run. Flag it WEAK (its retry is
  expensive; it is mis-sized) and propose the split: name the deliverables it bundles and the
  smaller tasks they should become, each with its test re-baseline scoped to that piece. This is the
  inverse of the missing-insertion check (§4): there a deliverable maps to NO task; here ONE task
  carries too many deliverables.
<!-- BEGIN ADDED PROBES #74/#75/#76/#96 -->
- **Keyword-not-structural for a METHOD CALL (#76)**: a "file calls `B.Method()`" guardrail that greps a
  **bare method name** — `RunAsync\s*\(` — passes on a comment (`// RunAsync(scope)`), a **local stub**
  of the same name (`private void RunAsync(...)`), or any unrelated same-named method, none of which
  invoke the real library method. The call-site sibling of the keyword-not-structural type/member trap.
  Fix: require **two sequential checks** — the **type** is referenced (`MigrationRunner`, rules out a
  local stub) AND the **dotted call** (`\.RunAsync\s*\(`, rules out comments + standalone definitions).
  Apply to any "task A must call `B.Method()`" on a specific type in another project. (Catalogue →
  method-call anchoring; `stacks/dotnet.md §15`.) BLOCKER on a wiring guardrail — a local/commented stub
  reads as wired.
- **Library bypasses its injected interface (#74)**: a task extracts a library that must write
  **through** an injected `IInterface`; it is registered + builds + tests pass — but **no guardrail checks
  the library's internals don't call the CONCRETE method directly**, bypassing the abstraction. Tell: an
  "extract … must go through `IInterface`" / "must NOT call `X` directly" task with only registration +
  build + tests-pass guardrails and no forbidden-direct-call scan of the library folder. Fix: a
  comment-stripped (#97/#98), dot-anchored (#76) forbidden-call scan of the **library project's `.cs`
  only** (exclude `bin`/`obj`). Also flag the inverse mistake: a **bare-name** bypass grep with no
  comment-strip — it *false-REDs* a correct library on a comment (whack-a-mole to `needs-human`).
  (Catalogue → no-direct-bypass; `stacks/dotnet.md §16`.) BLOCKER — a bypass ships green.
- **Enumerated behaviors unverified (#75)**: a test-author task whose action prompt lists **≥3 named
  behaviors** to encode but whose guardrails are only `tests-exist` + `tests-fail-on-current-code` —
  neither verifies the named behaviors are present, so **one** trivially-failing stub satisfies both
  while behaviors 2–N are never encoded (the coverage-gap anti-pattern, made concrete). Fix: a
  `covers-key-behaviors` check for **2–3 distinctive terms** (domain type / enum / method name — never
  generic words) from the list, **scoped to the one test file**; name it a **lower bound** (a term
  present ≠ the behavior asserted) and report which enumerated behaviors went unchecked. (Catalogue →
  covers-key-behaviors; `stacks/dotnet.md §17`.) WEAK→BLOCKER depending on how load-bearing the
  unverified behaviors are (it is the coverage-gap probe, sharpened for enumerated lists).
- **Name-convention seam unverified (#96)**: task A produces artifacts a consumer (task B / a runtime
  component) resolves by a **derived or mapped name** (url→embedded resource, step id→filename, key→file,
  route→handler, message-type→schema) — and `file-exists`/`file-contains` on A plus content checks on B
  both pass while the **naming contract is never exercised**. B derives a name A never produced (case /
  separator / single special-case drift) and **404s/silently-falls-back at runtime** on a 100%-green
  suite — invisible until the first real run. Tell: a derived-name consumer (fetch-by-name,
  embedded-resource/reflection lookup, convention file-map, route resolution) with only per-side
  file-exists/content checks and **no end-to-end lookup over the whole set**. Fix: a **consumer-driven
  integration guardrail** on a **both-sides-present** task that **parses the consumer's real map** (never
  a hard-coded contract copy), drives the lookup for **every** item, and asserts **200 + a per-item
  marker** (not a fallback body); `scope:"integration"` and **union-safe** (#125 — "every present
  artifact resolves"). Also flag the weak forms: a **sampled** check (not every item — the drift hides in
  the one special case) and a **hard-coded name list** in the test (a copy hides a consumer-side drift).
  (Catalogue → name-convention seam; `stacks/dotnet.md §18`.) BLOCKER on a UI/transport/convention-heavy
  plan — the failure is invisible to the whole suite.
<!-- END ADDED PROBES #74/#75/#76/#96 -->
<!-- BEGIN ADDED PROBES #176 — transitive compilation dependency · negative-assertion gap -->
- **Transitive compilation dependency — a test-author ancestor references a non-ancestor's type (#176)**:
  the §3 "missing edge" check applied at the IMPLICIT COMPILATION level, not just the direct-artifact
  level. For **each** task **B** whose verification step runs `dotnet build` / `dotnet test` (it compiles a
  test project): identify B's ancestor **test-author** tasks (ancestors that write `.cs` test files — those
  files are in the test project B compiles). For each such test file, consider the types its **action
  prompt's scenarios / deliverables ALLOW it to reference** (the enumerated scenarios, the named
  collaborators — not every type imaginable). If any of those types is **PRODUCED by an implementation task
  C that is NOT in B's ancestor set**, flag the **missing edge B←C**. The decision rule, stated verbatim:
  *"Task B's verification compiles the output of ancestor test-author task A. A's prompt allows referencing
  types produced by task C. C is not in B's ancestor set → missing edge B←C (add `C` to B's `dependsOn`, or
  the agent will be trapped — it can't fix a compile error in a file outside its writeScope, and may
  compensate by redefining the type in its own scope → a duplicate-definition merge collision)."* This is
  the exact failure chain of plan-0009: task 09's `dotnet test --filter` compiled the test project holding
  task 08's `MigrateDispatchTests.cs`, which referenced `CommanderRestImporter` produced by task 07 — and 07
  was NOT in 09's ancestor set, so 09 hit an unfixable compile error and redefined the class in its own
  writeScope (`Launcher.cs`), colliding with 07's copy at the AI-merge (CS0101, #175/#174). Severity: **WEAK**
  when the trap merely risks a wasted retry / `needsHuman`; **BLOCKER** when the test file's scenarios
  plainly reference the non-ancestor type (the compile failure is certain). Fix: add the producing
  implementation task to B's `dependsOn` so its output is present in B's working tree. (Distinct from the
  direct-artifact missing-edge check, §3 — there B reads C's FILE; here B COMPILES a file that references
  C's TYPE.) (plan-breakdown Step 3 adds the matching authoring rule.)
- **Negative-assertion gap — a prompt excludes a scenario but no guardrail forbids it (#176)**: when a
  task's action prompt **EXPLICITLY EXCLUDES** a scenario/keyword ("Mode C / `CommanderRest` is
  wizard-blocked — do NOT include it in the dispatch tests"; "the importer must NOT call `X` directly"),
  the corresponding guardrail must carry a **NEGATIVE assertion** verifying the excluded keyword is
  **ABSENT** — `if ($content -match "CommanderRest") { Write-Output "…"; exit 1 }` (fail-on-present).
  Without it, the agent is free to include the removed scenario **undetected**: the positive
  `covers-key-behaviors` only checks PRESENCE of the kept scenarios, so a stray excluded scenario sails
  through (exactly what slipped past plan-0009's task 08 and fed the #176 compile trap). For every
  test-author / implementation task whose prompt names an excluded scenario, confirm a fail-on-present
  guardrail exists for that keyword; if absent, flag **WEAK** (the exclusion is unenforced) — **BLOCKER**
  when the excluded scenario is the very thing that traps a downstream compile (the #176 case). Fix: add a
  fail-on-present negative-assertion guardrail (catalogue → negative assertion; `stacks/dotnet.md §20`),
  paired with the positive `covers-key-behaviors`. Note it is **correct** that `guardrails validate`'s
  GR2026 stays SILENT on this guardrail's keyword (post-#177 GR2026 flags only POSITIVE require-present
  coverage tokens, SSOT §4.4) — a GR2026 warning on a negative assertion would be the #177 false positive,
  not a signal to remove the guardrail. (plan-breakdown Step 4 adds the matching authoring rule.)
<!-- END ADDED PROBES #176 -->

### 3. DAG soundness
- Every edge justified (artifact, guardrail, or explicit ordering — not prose order).
- **Missing edges**: task B reads a state key or file only task A produces, with no
  path A→B. **Apply this at the IMPLICIT COMPILATION level too** (#176): if B's verification
  compiles a test project containing an ancestor test-author task's `.cs` file that references a
  type produced by a non-ancestor implementation task, that is a missing edge — see the
  "Transitive compilation dependency" probe in §2.
- **False edges** serializing genuinely parallel work.
- A terminal task aggregates (suite green / e2e) so the run has a meaningful end.
- **Exactly one integration-gate sink on a parallel plan.** A plan with ≥2 leaf tasks or
  any fan-in (the shape a parallel run produces) MUST declare **exactly one**
  `integrationGate: true` sink — the terminal whole-repo gate run on the fully merged
  plan-branch HEAD (SSOT §3.3). Confirm the gate is the genuine sink the leaves fan into,
  not a mislabeled mid-DAG task, and that no second `integrationGate: true` exists. A
  single linear chain with no fan-in may omit it.

### 4. Missing-insertion check
Re-apply plan-breakdown Step 5: any guardrail referencing an artifact no ancestor
produces and the repo doesn't already contain → a missing guardrail-enabling task.

### 5. State-contract lint
- Every prompt action carries the harness-contract header block.
- Every state key consumed downstream is produced upstream (or seeded).
- **Every state key consumed downstream has a fragment-key-present guardrail on its
  producer** (reads `GUARDRAILS_STATE_FRAGMENT`, asserts non-null/non-empty) — otherwise
  the action can skip writing the key and the consumer runs with null. (Catalogue
  state-output leaf.)
- **State-out key MUST be the task FOLDER NAME, never the `stableId` (#164).** For every
  state-writing prompt, read the fragment example/instruction in the `## Task` body and the
  harness-contract header. The single top-level key must be **this task's folder name** (the
  directory the `task.json` lives in). A fragment example keyed by anything else — most often
  the task's `stableId` (a `^[a-z0-9][a-z0-9._-]*$` token like `j9hf6y` that is NOT the folder
  name), a foreign task's folder name, or an arbitrary shared key — is a **BLOCKER**: the
  harness rejects it as a foreign/unowned key on **every** attempt (single-writer-per-key, SSOT
  §6.2), rolling back file writes and dead-ending the task at `needsHuman` (the #164 failure
  loop). Cross-check that the producer's state-output guardrail indexes the **same** folder name
  (`$fragment.'<folder-name>'.<key>`); a mismatch between the prompt's key and the guardrail's
  index is the same BLOCKER. Fix: rewrite the fragment example to
  `{ "<this-task-folder-name>": { … } }` and align the guardrail's index.
- `promptRunners` present iff prompts exist; `allowedTools` scoped, not blanket.

### 6. Report

| Task | Guardrail | Severity | What wrong implementation slips through | Concrete fix |
|---|---|---|---|---|

Severities: **BLOCKER** (a wrong implementation passes) · **WEAK** (gameable,
nondeterministic-where-deterministic-possible, or unactionable) · **NIT**.
For WEAK prompt-judges, the fix column contains the replacement deterministic
guardrail — ideally as ready-to-paste script text.

Then ask: **"Apply fixes?"** — per-finding approval, never bulk-silent. If a finding
concerns a guardrail the human added or edited (check `git log`/`git diff` if the
folder is tracked, else say you cannot tell), name that explicitly before proposing
changes to it.

### 7. Record the review

When the review pass is complete (findings reported; fixes applied or explicitly declined), record it
so the harness's review nudge clears:

```bash
guardrails mark-reviewed <folder>
```

This writes the committed, plan-hash-keyed `state/guardrails-review.json` marker (SSOT §13) — the skill
can't compute the `PlanHash` itself, so it delegates to the CLI. Until the plan changes, `guardrails
validate`/`run` stop emitting the GR2025 "not reviewed" warning; editing any `task.json` /
`guardrails.json` re-stales the marker and the nudge returns. The marker is COMMITTED as part of the
reviewed plan: because it is `planHash`-keyed it is an attestation about the committed plan content
that self-invalidates the instant any `task.json` / `guardrails.json` changes the planHash (the
GR2025 nudge returns), so committing it can never falsely vouch for changed content. `--fresh` does
NOT wipe it — `--fresh` clears only genuine runtime state (`run.json`, `state.json`,
`merge-conflicts.log`, `logs/`, `captured/`). Do NOT mark a plan reviewed while a BLOCKER finding
remains unaddressed — the marker vouches that the plan was genuinely reviewed.

## Quality bar
- [ ] `guardrails validate` ran first; findings don't duplicate the tool.
- [ ] `guardrails graph --check` ran; exit 2 (stale/missing) → regenerated and noted; exit 1 (error) → surfaced, not silently regenerated.
- [ ] Every BLOCKER names the concrete wrong implementation, not a vibe.
- [ ] Terminal/e2e tasks claiming an output quantity assert a STRICTLY POSITIVE value (no hollow `Assert.Equal(0,…)` / `NotNull` / bare `exit 0`); every structural property check is accessor-order-insensitive (no `\{\s*get` / `\{\s*set` anchor).
- [ ] Every WEAK judge finding names its deterministic replacement (or proves none exists).
- [ ] Coverage gaps cite the exact unverified completion criterion.
- [ ] Every `covers-key-behaviors` guardrail's required tokens are each named (directly or via synonym) in the SAME task's action prompt; a token the guardrail requires but the prompt never mentions is a BLOCKER ("the task will fail every attempt") — the human-judgement complement to the deterministic GR2026 warning (#157).
- [ ] Every TDD implementation task's `writeScope` EXCLUDES its test-author task's test files (but may TARGET the stub file the test-author wrote, #155); no task carries a vacuous `**`/over-broad `writeScope` (omission preferred over theater); confidently-scopable tasks declare a `writeScope`.
- [ ] Every inserted test-author task carries the correct TDD "red" for its type (#155): a BEHAVIORAL type has `build-passes` + `tests-fail-on-stubs` (with minimal stubs in its `writeScope`), not a lone non-zero-exit red gameable by non-compiling garbage; a split data-model task has a structural `[Fact]`/`[Theory]` covers-key-behaviors check.
- [ ] Every test-author task's `action.prompt.md` carries a **Scope boundary (harness-enforced)** paragraph (allowed path(s) + `git diff` check + retry consequence + the `needsHuman` redirect for an upstream missing-symbol compile error); absence is WEAK (#154).
- [ ] Every state-writing prompt's fragment example/key is the task's FOLDER NAME (never the `stableId` or a foreign/shared key), and the producer's state-output guardrail indexes that same folder name — a `stableId`-shaped or otherwise-unowned key is a BLOCKER (harness rejects it every attempt → `needsHuman` loop, #164).
- [ ] A **brownfield** plan (modifies project(s) with existing tests in the touched area) has a `00-baseline-<area>-tests-green` ROOT — a no-op `exit 0` action + a guardrail running the EXISTING area tests asserting they pass (area-scoped, #179-re-emit form), `dependsOn: []`, with every work task transitively depending on it ("never build on red"); its guardrail targets the PRE-EXISTING tests, NOT the about-to-be-authored red tests; it is DISTINCT from the terminal full-suite gate (green START at the root vs green END at the sink). A **greenfield** plan has NO baseline (a vacuous `dotnet test` over a zero-test project is itself a finding). Missing baseline on brownfield is WEAK (BLOCKER when the area's existing tests are in fact red at start) (#181).
- [ ] A parallel plan (≥2 leaf tasks or any fan-in) has exactly one `integrationGate: true` sink carrying ≥1 `scope: "integration"` guardrail, and that guardrail is a **union-safe CONDITIONAL invariant** (conflict-marker-free / "if X present, verify it"), NOT the full build or whole suite. A full-build or whole-suite guardrail marked `scope: "integration"` on the terminal gate is the #125 terminal-postcondition anti-pattern → **BLOCKER** (it red-halts correct intermediate unions where downstream TDD tasks have not run yet); the full build/suite must be **LOCAL** (#165).
- [ ] Every `IFoo`/`FooImpl` pair has a wiring task + a composition-root guardrail that drives the REAL assembler (no seam-injecting guardrail; whole-suite green does not stand in for wiring) (#120).
- [ ] Every dispatch task routing ≥2 enum values to ≥2 concrete types whose dispatch tests use seam-injection has a per-pairing proximity check binding `<EnumValue>` to `<ConcreteType>` (WEAK if missing; BLOCKER if the only concrete check is `tests-pass`); omitted only when the tests assert the concrete TYPE NAME (#158).
- [ ] Every forbidden-keyword scan over a source file strips comments before matching; no task both documents banned constructs in a header comment AND greps for them comment-blind (#97, #98).
- [ ] Every derived-corpus task asserts input→output coverage + per-output substance floor + index completeness (`produced ⊆ indexed`) + ingestion lower bound, named as lower bounds (no judge alone for faithfulness) (#99).
- [ ] Every `scope:"integration"` guardrail is union-safe (passes the "would this pass on a partial merge with a downstream task unsettled?" test); terminal postconditions live in a `local` guardrail on the sink (#125).
- [ ] Every set of ≥2 tasks with OVERLAPPING `writeScope`s on a shared file has ≥1 `scope:"integration"` guardrail asserting the shared-file UNION invariant — the union re-verify is integration-set-only (#132), so a sibling's `local`-only coverage is NOT re-run at the union; flag WEAK if missing. When the shared file is a CODE file and both siblings could ADD a type/member definition, that union guardrail also carries a **duplicate-definition count check** (`[regex]::Matches($content,'class\s+<Name>').Count -gt 1`, union-safe/conditional) — a 3-way merge keeps both copies with no conflict marker (CS0101), the #175 residual; WEAK if absent.
- [ ] Every task whose verification runs `dotnet build`/`dotnet test` was checked for a **transitive compilation dependency** (#176): an ancestor test-author task's `.cs` file referencing a type produced by a task NOT in the verifying task's ancestor set is a missing edge — add the producing task to `dependsOn` (WEAK, or BLOCKER when the compile failure is certain).
- [ ] Every guardrail that asserts a test suite PASSES (`tests-pass`/`all-tests-pass`/`specific-tests-pass`, or a production-seam driver) re-emits the failure DETAIL (assertion/exception lines) at the END of stdout so it reaches the harness retry tail — not just the `[FAIL] <name>` summary default `dotnet test` leaves (#179); absence is WEAK (degrades retry feedback, costs attempts). The INVERSE `tests-fail-on-stubs` / `tests-fail-on-current-code` checks (non-zero exit = success) do NOT re-emit and must not be flagged.
- [ ] Every action prompt that **excludes** a scenario/keyword ("do NOT include `CommanderRest`") has a matching **negative-assertion** guardrail (`if ($content -match "<keyword>") { … exit 1 }`, fail-on-present) verifying the keyword is ABSENT (#176); absence is WEAK (BLOCKER when the excluded scenario traps a downstream compile). GR2026 correctly stays silent on the negative assertion's keyword (post-#177, §4.4) — a GR2026 warning there is the false positive, not a reason to delete the guardrail.
- [ ] Every `scope:"integration"` union guardrail's expected-contribution tokens are each produced by a task in the integration task's ANCESTOR set (a directed path producer → fan-in); a token whose only producer is a disconnected leaf / side branch is WEAK ("if task `<N>` is later removed, this guardrail will fail spuriously — add a DAG edge or drop the check") (#159).
- [ ] Every task ran through the over-size split-trigger; any task bundling multiple deliverables / wide blast radius / 1:1-to-a-milestone / expensive-retry is flagged WEAK with a proposed split (#111).
<!-- BEGIN ADDED CHECKS #74/#75/#76/#96 -->
- [ ] Every "task A calls `B.Method()`" guardrail anchors on BOTH the type reference and the dotted call (`\.Method\s*\(`), never a bare method-name grep (#76).
- [ ] Every "extract a library that must write through `IInterface`" task has a forbidden-direct-call scan of the library folder — comment-stripped and dot-anchored, never a bare-name grep that false-REDs on a comment (#74).
- [ ] Every test-author task whose prompt enumerates ≥3 behaviors has a covers-key-behaviors check (2–3 distinctive terms, scoped to the one test file), named as a lower bound, with the unchecked behaviors reported (#75).
- [ ] Every producer↔consumer derived-name seam has a consumer-driven integration guardrail on a both-sides-present task that drives the real lookup for EVERY item and asserts 200 + a per-item marker — union-safe, no hard-coded name copy, no sampling (#96).
<!-- END ADDED CHECKS #74/#75/#76/#96 -->
- [ ] No fix applied without explicit approval; human-authored guardrails called out.
- [ ] The review was recorded with `guardrails mark-reviewed <folder>` once findings were addressed/declined — clearing the GR2025 nudge (#79/#131); NOT run while a BLOCKER remained open.
