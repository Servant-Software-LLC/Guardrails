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
- **Judge-where-deterministic-possible**: for every `.prompt.md` guardrail, name the
  deterministic archetype that could replace it, or confirm none can (the 4-question
  demotion gate).
- **Over-broad**: "all tests pass" anywhere except a terminal integration task.
- **Coverage gap**: the action's stated completion criteria exceed what guardrails
  verify — name the unverified criterion. (E.g. action says "sorted by category";
  no guardrail checks sorting.)
- **Tests gameable**: implementation tasks whose tests can be edited by the same
  action (no tests-untouched guardrail); inserted test tasks missing
  tests-fail-on-current-code.
- **Unactionable failures**: guardrails that fail without printing a usable reason
  (retry feedback quality).
- **Grep-scope contamination**: a file-content guardrail that greps the project tree
  (`Get-ChildItem -Recurse | Select-String`) instead of the one file the task owns — a
  same-wave sibling sharing the term can satisfy it. (Catalogue anti-pattern.)
- **Keyword-not-structural**: an "implements/extends/declares" check matching a bare
  type name (`Select-String "IFoo"`) that a comment, `using`, or local copy satisfies —
  it should match the declaration construct (stack file's structural regex).
- **Unregistered module**: a task adds a module/project to a build descriptor (`.csproj`
  → `.slnx`) but no guardrail checks the DESCRIPTOR names it — a descriptor build passes
  with the project unregistered. (Stack file → build-descriptor registration.)
- **Unreferenced abstraction**: a task creates an abstraction a later task must consume,
  but no guardrail checks the consumer's project file has a `<ProjectReference>` — builds
  pass independently, so a local copy of the interface slips through. (Stack file →
  cross-module reference.)

### 3. DAG soundness
- Every edge justified (artifact, guardrail, or explicit ordering — not prose order).
- **Missing edges**: task B reads a state key or file only task A produces, with no
  path A→B.
- **False edges** serializing genuinely parallel work.
- A terminal task aggregates (suite green / e2e) so the run has a meaningful end.

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
- [ ] Every WEAK judge finding names its deterministic replacement (or proves none exists).
- [ ] Coverage gaps cite the exact unverified completion criterion.
- [ ] No fix applied without explicit approval; human-authored guardrails called out.
