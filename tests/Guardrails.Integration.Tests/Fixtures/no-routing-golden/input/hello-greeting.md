# Hello, greeting

A deliberately tiny, single-model plan. It is the FIXED INPUT for the DoR Invariant 7
proof: broken down against a config that carries no `routing` block, the folder
`/plan-breakdown` emits must be byte-identical to the folder it emitted before difficulty
tiering existed (#225).

The plan is kept small on purpose, but it is not trivial in the way that matters — it
contains all three populations the gate could leak into:

- a SCRIPT task (never tagged on either side of the gate),
- two PROMPT tasks (each would carry an `action.tier` if tiering were configured),
- a surviving prompt-judge guardrail (which would be classified and reported if tiering
  were configured).

## Goal

Produce a personalised greeting and an honest review of it, entirely inside the plan
folder's `out/` directory.

## Steps

1. **Seed the recipient.** Write `out/recipient.txt` containing the single word `World`.
   The content is dictated here, so this step is a script, not a prompt.
2. **Write the greeting.** Read `out/recipient.txt` and write `out/greeting.txt`
   containing `Hello, <recipient>!` on one line.
3. **Review the greeting.** Write `out/review.md` with a `# Greeting review` heading, a
   `## Greeting` section quoting the greeting verbatim, and a `## Verdict` section
   assessing whether the greeting reads warmly. The verdict is a judgement call, so it
   is worth an independent judge alongside the structural check.

## Done when

- `out/greeting.txt` exists and greets the recipient named in `out/recipient.txt`.
- `out/review.md` exists, quotes the greeting verbatim, and carries a substantive verdict.

## Constraints

- **Single model.** This plan names no provider, configures no second prompt runner, and
  never asks for per-model routing of any kind. It is the ordinary case the gate protects.
