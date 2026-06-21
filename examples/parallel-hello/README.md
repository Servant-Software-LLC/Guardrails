# parallel-hello

A minimal **tokenless** example that exercises plan 08's **worktree-per-task** execution path:
two independent leaf tasks run concurrently in isolated git worktrees, then a fan-in
`integrationGate` sink integrates and gates the merged result.

It is both a runnable demo and an acceptance fixture for the parallel-execution engine.

## The shape (a diamond)

```
  01-write-hello   02-write-world      <- two independent leaves (no dependsOn between them),
        \               /                 run CONCURRENTLY at maxParallelism: 2
         \             /
        03-combine-greeting             <- fan-in sink, integrationGate: true
```

- **`01-write-hello`** and **`02-write-world`** are independent leaves. Each is a SCRIPT action
  that writes a distinct file (`out/hello.txt`, `out/world.txt`) into its segment worktree, so each
  segment has a real committable change. Each carries a narrow `writeScope` declaring exactly the one
  file it is allowed to touch.
- **`03-combine-greeting`** depends on BOTH leaves (a fan-in), is marked `integrationGate: true`,
  and carries a `scope: "integration"` guardrail (the whole-repo gate). It combines both leaves'
  files into `out/greeting.txt` and verifies that every leaf's contribution survived the union.

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
- the terminal integration gate (`03-combine-greeting`) ran on the final merged HEAD and saw both
  leaves' work.

To deliver the plan branch back onto your branch afterwards, re-run with `--merge-on-success`.
