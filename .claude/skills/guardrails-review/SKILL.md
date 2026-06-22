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
- **Coverage gap**: the action's stated completion criteria exceed what guardrails
  verify — name the unverified criterion. (E.g. action says "sorted by category";
  no guardrail checks sorting.)
- **Tests gameable**: implementation tasks whose tests can be edited by the same
  action — the implementation task's `writeScope` must EXCLUDE the test files its
  upstream test-author task owns (the deterministic write-scope test-exclusion, SSOT
  §3.4), so an edit to a test file fails the harness's read-only write-scope check. An
  implementation task with no `writeScope`, or one whose scope covers the test files, is
  gameable. Inserted test tasks missing tests-fail-on-current-code.
- **Unactionable failures**: guardrails that fail without printing a usable reason
  (retry feedback quality).
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
- **Build/suite not marked `scope: "integration"`**: the whole-repo build and full test-suite
  guardrails must declare `scope: "integration"` (SSOT §4.3) so they join the integration set
  re-run at every union point and on the terminal gate. A whole-suite or whole-repo-build
  guardrail left at the `"local"` default is a coverage gap on an integration-sensitive (parallel)
  plan — the union points and the terminal gate would re-run nothing. Flag it (BLOCKER on a
  parallel plan with unions; WEAK otherwise) and name the guardrail to re-scope.
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

### 3. DAG soundness
- Every edge justified (artifact, guardrail, or explicit ordering — not prose order).
- **Missing edges**: task B reads a state key or file only task A produces, with no
  path A→B.
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

## Quality bar
- [ ] `guardrails validate` ran first; findings don't duplicate the tool.
- [ ] `guardrails graph --check` ran; exit 2 (stale/missing) → regenerated and noted; exit 1 (error) → surfaced, not silently regenerated.
- [ ] Every BLOCKER names the concrete wrong implementation, not a vibe.
- [ ] Terminal/e2e tasks claiming an output quantity assert a STRICTLY POSITIVE value (no hollow `Assert.Equal(0,…)` / `NotNull` / bare `exit 0`); every structural property check is accessor-order-insensitive (no `\{\s*get` / `\{\s*set` anchor).
- [ ] Every WEAK judge finding names its deterministic replacement (or proves none exists).
- [ ] Coverage gaps cite the exact unverified completion criterion.
- [ ] Every TDD implementation task's `writeScope` EXCLUDES its test-author task's test files; no task carries a vacuous `**`/over-broad `writeScope` (omission preferred over theater); confidently-scopable tasks declare a `writeScope`.
- [ ] A parallel plan (≥2 leaf tasks or any fan-in) has exactly one `integrationGate: true` sink carrying ≥1 `scope: "integration"` guardrail; the whole-repo build and full test suite are marked `scope: "integration"`.
- [ ] Every `IFoo`/`FooImpl` pair has a wiring task + a composition-root guardrail that drives the REAL assembler (no seam-injecting guardrail; whole-suite green does not stand in for wiring) (#120).
- [ ] Every forbidden-keyword scan over a source file strips comments before matching; no task both documents banned constructs in a header comment AND greps for them comment-blind (#97, #98).
- [ ] Every derived-corpus task asserts input→output coverage + per-output substance floor + index completeness (`produced ⊆ indexed`) + ingestion lower bound, named as lower bounds (no judge alone for faithfulness) (#99).
- [ ] Every `scope:"integration"` guardrail is union-safe (passes the "would this pass on a partial merge with a downstream task unsettled?" test); terminal postconditions live in a `local` guardrail on the sink (#125).
- [ ] Every task ran through the over-size split-trigger; any task bundling multiple deliverables / wide blast radius / 1:1-to-a-milestone / expensive-retry is flagged WEAK with a proposed split (#111).
<!-- BEGIN ADDED CHECKS #74/#75/#76/#96 -->
- [ ] Every "task A calls `B.Method()`" guardrail anchors on BOTH the type reference and the dotted call (`\.Method\s*\(`), never a bare method-name grep (#76).
- [ ] Every "extract a library that must write through `IInterface`" task has a forbidden-direct-call scan of the library folder — comment-stripped and dot-anchored, never a bare-name grep that false-REDs on a comment (#74).
- [ ] Every test-author task whose prompt enumerates ≥3 behaviors has a covers-key-behaviors check (2–3 distinctive terms, scoped to the one test file), named as a lower bound, with the unchecked behaviors reported (#75).
- [ ] Every producer↔consumer derived-name seam has a consumer-driven integration guardrail on a both-sides-present task that drives the real lookup for EVERY item and asserts 200 + a per-item marker — union-safe, no hard-coded name copy, no sampling (#96).
<!-- END ADDED CHECKS #74/#75/#76/#96 -->
- [ ] No fix applied without explicit approval; human-authored guardrails called out.
