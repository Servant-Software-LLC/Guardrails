# parallel-hello

A minimal **tokenless** example that exercises plan 08's **worktree-per-task** execution path:
two independent leaf tasks run concurrently in isolated git worktrees, a fan-in task combines them,
then the plan-level `<plan>/guardrails/` **terminal folder** gates the fully merged result.

It is both a runnable demo and an acceptance fixture for the parallel-execution engine.

## The shape (a diamond)

```
  01-write-hello   02-write-world      <- two independent leaves (no dependsOn between them),
        \               /                 run CONCURRENTLY at maxParallelism: 2
         \             /
        03-combine-greeting             <- fan-in task (dependsOn both leaves)
              │
      <plan>/guardrails/                <- terminal integration gate (a FOLDER, run once on the
                                           final merged HEAD after the whole DAG is green)
```

- **`01-write-hello`** and **`02-write-world`** are independent leaves. Each is a SCRIPT action
  that writes a distinct file (`out/hello.txt`, `out/world.txt`) into its segment worktree, so each
  segment has a real committable change. Each carries a narrow `writeScope` declaring exactly the one
  file it is allowed to touch.
- **`03-combine-greeting`** depends on BOTH leaves (a fan-in). It combines both leaves' files into
  `out/greeting.txt` and carries a `local` guardrail that verifies every leaf's contribution survived
  the union (a terminal postcondition it can assert on its own segment, which already holds both
  leaves' files). It is an ordinary task — the retired `integrationGate: true` sink modelling is gone.
- **`<plan>/guardrails/`** (a sibling of `tasks/`, NOT a task) is the **terminal integration gate**.
  Its one deterministic check (`01-whole-repo-greeting.ps1`, tagged `scope: "integration"`) is the
  run's integration-guardrail set: it re-runs on the merged bytes at every union AND once on the final
  merged HEAD after the whole DAG is green, asserting the merged `out/*.txt` are non-empty and
  conflict-marker-free (a union invariant — the GR2028 content teeth for a zero-toolchain plan).

There are no prompt/Claude actions and no prompt guardrails — every action and guardrail is a
deterministic PowerShell script (`.ps1`, resolved cross-platform via `pwsh`). The run spends **no
tokens**.

## You MUST run it with a git repository as the workspace

`maxParallelism: 2` (> 1) puts the harness in **worktree mode**: it forks a `guardrails/parallel-hello`
branch off the workspace's current HEAD, runs each task in an isolated linked git worktree, and
integrates every task's commit back onto that branch. **This requires the workspace to be inside a
git repository** (the harness needs a git top-level). `guardrails.json` sets `"workspace": ".."`, so
the workspace is the plan folder's parent — that parent (or an ancestor) must be a git repo with at
least one commit.

A serial run (`maxParallelism: 1`) would use the shared-workspace model instead and would NOT
exercise the worktree path this example is built to demonstrate.

## Run it

Place this plan folder so its workspace (`..`) resolves to a git repo root, then:

```sh
guardrails run examples/parallel-hello/parallel-hello --no-ui --no-log-server
```

Or, from a source checkout (Debug build, no self-lock):

```sh
dotnet run --project src/Guardrails.Cli -- run examples/parallel-hello/parallel-hello --no-ui --no-log-server
```

## What a green run proves

- exit code 0 — all three tasks and their guardrails passed;
- a `guardrails/parallel-hello` branch exists, carrying the integrated commits (each with a
  `Guardrails-Task: <id>` trailer — at least two, one per leaf);
- your original branch HEAD is unmoved (the plan branch is isolated; no `--merge-on-success`);
- the terminal integration gate (the `<plan>/guardrails/` folder) ran on the final merged HEAD and
  saw both leaves' work integrated cleanly.

To deliver the plan branch back onto your branch afterwards, re-run with `--merge-on-success`.
