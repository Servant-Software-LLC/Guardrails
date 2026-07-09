# Waved Hello — a two-stage greeting (reviewed plan)

A deliberately tiny plan authored as **two ordered stages**, where Stage 2 builds on the
**materialized output** of Stage 1. It is the flat `hello-guardrails` demo re-shaped into the
smallest thing that must become a *waved* breakdown (SSOT §14) — the companion the waved
few-shot (`.claude/skills/plan-breakdown/references/example-breakdown-waved.md`) walks through.

The stage boundary is real, not cosmetic: every Stage-2 task names files and a script signature
that **do not exist until Stage 1 has run** (`out/greet.ps1`, `out/config.json`). That is the
signal that turns a flat plan into a waved one — and the reason Stage 2 can be broken down
*just-in-time* against Stage 1's materialized workspace rather than guessed up front.

## Stage 1 (Wave 1) — Scaffold the greeting toolkit

Produce the two independent building blocks the second stage consumes:

1. A greeting script `out/greet.ps1` that prints `Hello, <name>!` for a `-Name` argument.
2. A recipient config `out/config.json` holding the name to greet (seeded via shared state).

These two deliverables have no dependency on each other — they are the wave's two parallel leaves.
The stage is done when both files exist, are non-empty, and carry no merge-conflict markers.

## Stage 2 (Wave 2) — Generate and check the greeting

Builds on Stage 1's **materialized** output (it may not begin until Stage 1's files are on the
branch):

1. Run `out/greet.ps1` for the name in `out/config.json` and save `out/greeting.txt`.
2. Write `out/report.md` quoting the generated greeting.

The stage — and the plan — is done when `out/greeting.txt` and `out/report.md` both exist and the
report quotes the real greeting.

## Notes for the breakdown

- Stage 2's tasks reference exact paths and the `greet.ps1 -Name` signature that Stage 1 produces.
  When those are not designable up front, break Stage 2 down *after* Stage 1 runs, reading the
  materialized workspace from the integration worktree (the JIT staged-breakdown flow).
- There is no cross-stage task edge to author: the wave barrier orders the stages. A Stage-2 task
  that named a Stage-1 task in `dependsOn` would be a hard error (GR2034).
