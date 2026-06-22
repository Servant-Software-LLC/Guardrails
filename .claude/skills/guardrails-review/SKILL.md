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
- **Unregistered module**: a task adds a module/project to a build descriptor (`.csproj`
  → `.slnx`) but no guardrail checks the DESCRIPTOR names it — a descriptor build passes
  with the project unregistered. (Stack file → build-descriptor registration.)
- **Unreferenced abstraction**: a task creates an abstraction a later task must consume,
  but no guardrail checks the consumer's project file has a `<ProjectReference>` — builds
  pass independently, so a local copy of the interface slips through. (Stack file →
  cross-module reference.)
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
- [ ] No fix applied without explicit approval; human-authored guardrails called out.
