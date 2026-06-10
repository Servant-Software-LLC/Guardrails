# 01 — Overview and Principles

## What Guardrails is

A reviewed markdown plan goes in; a file-system folder of **tasks** comes out; a
cross-platform harness executes them until every task's **guardrails** pass.

- **Task** = one unit of work with an **action** (an executable, a deterministic
  script, or an LLM prompt) and **one or more guardrails**. ALL guardrails must pass
  for the task to be complete.
- **Guardrail** = an executable check: exe/script (exit 0 = pass) or a prompt
  verifier (writes a `{pass, reason}` verdict). Guardrails lean **deterministic over
  prompts** — a unit test beats an LLM judge.
- **Harness** = the `guardrails` dotnet tool. Walks the dependency DAG, runs ready
  tasks (parallelism allowed), retries a failing task with the guardrail failures fed
  back to the action, and halts a branch as `needs-human` when the retry budget is
  exhausted — while independent branches keep going.
- **State** = a shared JSON document. Tasks receive an immutable snapshot and
  contribute fragments; the harness is the single writer (no races by construction).

## The workflow (four stages)

```
1. PLAN      agents + human write and extensively review <plan>.md
2. BREAK     /plan-breakdown generates <plan>/ — tasks, dependencies, guardrails
             (including INSERTED tasks that author the guardrails' preconditions,
             e.g. "write the unit tests" before "implement the feature")
3. REVIEW    human edits/deletes/adds guardrails; /guardrail-review runs an
             adversarial pass ("what wrong implementation passes these?")
4. RUN       guardrails run <plan>/ — executes to green or stops at needs-human
```

The generated folder is always presented as a **draft**. The human review in stage 3
is part of the design, not optional ceremony: the entire safety story is that a
human approves *the checks* once instead of reviewing *every output* forever.

## Principles

1. **Verification over generation.** The bottleneck in agentic engineering is
   checking work, not producing it. Guardrails make acceptance criteria executable.
2. **Deterministic first.** Prompt-judges are a last resort, never alone, and must
   pass a demotion gate (could a regex/schema/test check this instead?).
3. **Plain files, no platform.** Tasks, guardrails, state, and journal are files —
   git-diffable, human-editable, reviewable in a PR. No SaaS, no database, no daemon.
4. **Light setup.** `dotnet tool install -g guardrails` and you can run a plan
   folder. Skills are markdown — copy them into `~/.claude/skills/` to use anywhere.
5. **Crash-safe by construction.** Single-writer state, atomic writes, per-task
   fragments, resumable journal. A killed run resumes; a crashed task can't corrupt
   shared state.
6. **Honest halts.** When a task can't converge, the harness says so, with the full
   attempt history and composed feedback on disk for the human — it never quietly
   marks weak work done.

## Out of scope (v1)

- Worktree-per-task parallel workspace isolation (v2 bet #1 — v1 uses
  `exclusive`-by-default for prompt actions instead)
- CI mode / GitHub check-run integration (v2 bet #2)
- Executable guardrail template library (v2 bet #3 — v1 catalogue is documentation)
- Token cost caps (v2 bet #4 — v1 logs cost per attempt but doesn't enforce budgets)
- Non-Claude prompt runners (the `IPromptRunner` seam exists; only `claude` ships)
- Cross-machine / distributed execution

See `03-roadmap.md` for milestones and the v2 bets in full.

## Document map

| Doc | Contents |
|---|---|
| `00-initial-plan.md` | The approved founding plan (superseded section-by-section as numbered docs grow) |
| `01-overview.md` | This file — the mental model and principles |
| `02-schemas-and-contracts.md` | **Single source of truth** for every schema and process contract |
| `03-roadmap.md` | Milestones M1–M7 with exit criteria, plus the named v2 bets |

Convention: revise the doc in the same change that moves the code.
