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
**draft**: the human edits it, then `/guardrail-review` runs an adversarial pass,
and only then does `guardrails run` execute it.

**References (load as needed):**
- `references/guardrail-catalogue.md` — archetypes, decision tree, demotion gate,
  anti-patterns. **Read before Step 4, every time.**
- `references/schemas.md` — exact file formats to emit (excerpt of the SSOT,
  `docs/plans/02-schemas-and-contracts.md`).
- `references/example-breakdown.md` — a complete worked breakdown including an
  inserted task, plus a negative example. Read when in doubt about output shape.

## Step 0 — Preconditions

1. Resolve the plan path. If the file doesn't exist, stop and say so.
2. If `<plan-name>/` already exists next to the plan, **never silently clobber** —
   ask: overwrite, merge, or abort. A human may have edited that folder.
3. Confirm the plan is *reviewed* (ask if unclear). Breaking down an unreviewed plan
   multiplies its errors into N tasks.
4. Check `guardrails --version` works. If not on PATH, warn that Step 7
   self-validation will be skipped and the output is unverified.
5. Identify the **workspace** (the repo the plan operates on — normally the folder
   containing the plan) and what already exists there: test framework, linter,
   build system. Guardrail selection depends on what's real.

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

Heuristic: a typical feature plan yields **5–15 tasks**. Under 3 or over 25 →
re-examine, and tell the user why if it stands.

## Step 3 — Determine the DAG (`dependsOn`)

Edge sources, in priority order:
- **(a) artifact dependency** — task B consumes a file/state key task A produces;
- **(b) guardrail dependency** — B's guardrail executes A's artifact (tests, scripts);
- **(c) explicit plan ordering** ("after", "requires").

**Default to the sparsest correct DAG.** Plan prose order alone is NOT an edge —
parallelism is free, false edges serialize the run. Record a one-line justification
per edge (in the task description or the breakdown report). Verify acyclicity.

## Step 4 — Select guardrails (read `references/guardrail-catalogue.md` first)

Apply the decision tree per task. Rules that are never optional:
- 1–4 guardrails per task, **cheapest-first** filename order (`01-exists`,
  `02-builds`, `03-tests`, `04-review`).
- Every guardrail file opens with `# catches: <the wrong implementation it catches>`.
  Can't write the sentence → delete the guardrail.
- Every candidate prompt-judge passes the 4-question demotion gate or is demoted.
  A judge is never a task's only guardrail.
- Deterministic guardrails print ONE actionable failure line to stdout (it becomes
  retry feedback).
- "All tests pass" appears ONLY on a terminal integration task.

## Step 5 — Insert guardrail-enabling tasks (the generative step)

For every selected guardrail whose precondition doesn't exist yet, generate the
upstream task that creates it:

- Guardrail "tests X pass" and tests X don't exist → insert `NN-author-tests-X`
  BEFORE the implementation task. Its own guardrails: tests-build +
  **tests-fail-on-current-code** (the anti-tautology check). The implementation task
  gains a guardrail like `tests-untouched` (git-diff the test files) so it can't
  "pass" by editing the tests.
- Guardrail "schema validates" and no schema exists → insert an author-schema task
  (guardrails: schema file exists + parses + a known-bad sample FAILS validation).
- Guardrail "port answers" → ensure an ancestor produces the launch script, or the
  guardrail owns start/stop itself with a timeout.

**The artifact-ancestry rule:** a guardrail may only reference artifacts produced by
an ancestor task or pre-existing in the repo. Sweep all guardrails against this rule
before Step 6; every violation is a missing inserted task.

## Step 6 — Write the folder

Per `references/schemas.md`, exactly:

- Folder = plan filename minus `.md`, beside the plan. Tasks = `NN-verb-object`
  kebab-case; NN follows a valid topological order (human-scanning hint only).
- `guardrails.json`: version + sensible run config. **Any `.prompt.md` anywhere ⇒
  the `promptRunners` block with a resolvable default is REQUIRED** (else GR2008).
  Scope `allowedTools` to what the actions genuinely need.
- `task.json` per task: `description` (one actionable line), `dependsOn`, overrides
  only when justified. One `action.*` file per task folder.
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
   generated artifact, never hand-edited; see `references/schemas.md`).
4. Emit the **breakdown report**: task table (id, action kind, guardrails with
   archetype numbers, dependsOn), the inserted-task list with justifications, edge
   justifications, and any flagged non-executable plan content. Then **embed the
   generated Mermaid block inline** (paste the ```mermaid``` fence from `diagram.md`)
   so the human sees the DAG in chat, and **state the `<folder>/diagram.md` path**
   explicitly so they can render it in GitHub or VS Code.
5. Close with, verbatim in spirit:

   > **This is a draft.** Review the folder — especially the guardrails — edit,
   > delete, or add, then run `/guardrail-review <folder>` before executing with
   > `guardrails run <folder>`.

   Never present the output as execution-ready.

## Quality bar (verify before declaring done)

- [ ] Every task has ≥ 1 deterministic guardrail; judges passed the demotion gate and are never alone.
- [ ] Every guardrail file opens with its `catches:` line.
- [ ] Every guardrail respects the artifact-ancestry rule.
- [ ] Inserted test-author tasks include tests-fail-on-current-code; implementation tasks guard tests-untouched.
- [ ] Every `dependsOn` edge has a stated justification; no prose-order-only edges.
- [ ] All prompt actions contain the harness-contract block.
- [ ] `promptRunners` present iff any `.prompt.md` exists.
- [ ] `guardrails validate` exits 0 (or its absence is loudly reported).
- [ ] `diagram.md` generated via `guardrails graph` and its path reported (block embedded inline).
- [ ] Output explicitly presented as a draft for human review.
