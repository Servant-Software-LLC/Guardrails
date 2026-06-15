# Guardrails — Lunch & Learn AI Edition
### Half-Hour Walk-Through · 2026-06-15

---

## Timing guide
| Section | Slides | Time |
|---|---|---|
| The problem | 1–3 | 5 min |
| What Guardrails is | 4–6 | 6 min |
| How it works | 7–10 | 8 min |
| Demo | 11 | 8 min |
| Getting started + Q&A | 12–13 | 3 min |

---

---
## SLIDE 1 — Title

# Guardrails
## Reliable Agentic Engineering

*David Maltby · Lunch & Learn AI Edition · June 2026*

> **Speaker note:** Welcome everyone. Today I'm going to show you something that I think changes how we think about using AI agents on real engineering work — not just "ask it a question" but actually letting an AI agent do a meaningful chunk of a project, with checks built in so you can trust the result.

---

---
## SLIDE 2 — The Problem: AI Agents Are Powerful But Unguarded

### The promise
> "I'll let the AI write this feature."

### The reality
- It runs. It produces output. But did it actually work?
- You find out when a test fails in CI — or worse, in production
- Running it again gets a different answer
- There's no record of what it did or why

### The question nobody asks until it hurts
> **How do you verify what an AI agent did?**

> **Speaker note:** This is the gap. Everyone is excited about agents, and rightly so. But there's a missing layer between "here's a prompt" and "I trust this result." At Tricentis we care deeply about software quality — and right now, agentic AI has almost no quality layer built in.

---

---
## SLIDE 3 — The Agentic Engineering Gap

```
Plan  →  ??? →  Done
```

Today's workflow:
1. Write a plan (or just a prompt)
2. Hand it to an AI agent
3. Hope the output is correct
4. Review manually — if you have time

**What's missing:**
- Executable verification at each step
- Automatic retry when something goes wrong
- A record of what was tried and why it failed
- A way for humans to pick up exactly where the agent got stuck

> **Speaker note:** Think about what we do for human engineers: pull request reviews, CI pipelines, test suites, code review checklists. We have almost none of that for AI agents today. Guardrails is the CI pipeline for agentic work.

---

---
## SLIDE 4 — What Is Guardrails?

> A harness for agentic engineering that makes AI-driven tasks **verifiable, retryable, and resumable**.

**Three things it does:**

1. **Structures work** into a DAG of tasks — each with an action and proof-of-correctness checks
2. **Executes the DAG** — runs actions, verifies results, retries with targeted feedback on failure
3. **Preserves durable progress** — independent branches keep running; failed tasks halt for human review without losing completed work

**Delivered as:**
- A cross-platform dotnet tool: `guardrails`
- Claude Code skills: `/plan-breakdown` and `/guardrail-review`

> **Speaker note:** The name "Guardrails" refers to the verification checks on each task — not guardrails in the AI-safety sense. Think of them like the guardrails on a mountain road: they don't slow you down unless you're about to go off the edge.

---

---
## SLIDE 5 — The Four-Stage Workflow

```
① Write a Plan      →   ② Break It Down   →   ③ Review   →   ④ Execute
  (Markdown doc)         (/plan-breakdown)      (/guardrail-    (guardrails run)
                                                 review)
```

| Stage | Who does it | What happens |
|---|---|---|
| Plan | Human (+ AI assist) | Reviewed markdown — the what and why |
| Break down | `/plan-breakdown` skill | Generates a task folder with actions + guardrails |
| Review | `/guardrail-review` skill + human | Adversarial check: "what wrong implementation passes this?" |
| Execute | `guardrails run` | DAG scheduler runs tasks, verifies, retries, reports |

> **Speaker note:** The key insight is the human review step in the middle. The AI generates the task structure, but a human (and a second AI reviewer) inspect it before anything runs. You're not flying blind.

---

---
## SLIDE 6 — What a Task Folder Looks Like

```
my-feature/
├── guardrails.json          ← run config (parallelism, retries, timeouts)
├── state/
│   ├── state.json           ← shared state, harness-owned
│   └── logs/<task>/         ← per-attempt: inputs, outputs, feedback
└── tasks/
    ├── 01-write-tests/
    │   ├── task.json        ← { description, dependsOn, retries }
    │   ├── action.prompt.md ← what the agent is asked to do
    │   └── guardrails/
    │       ├── 01-tests-compile.ps1   ← cheapest check first
    │       └── 02-tests-fail-on-stub.ps1
    └── 02-implement-feature/
        ├── task.json        ← dependsOn: ["01-write-tests"]
        ├── action.prompt.md
        └── guardrails/
            ├── 01-build-passes.ps1
            └── 02-tests-pass.ps1
```

> **Speaker note:** This folder is a first-class artifact. You check it into source control. You can inspect every file by hand. A human can read every guardrail and decide if it's strong enough. It's not a black box — it's a recipe you can audit.

---

---
## SLIDE 7 — Guardrails: The Key Insight

> **Every task must prove it worked.**

Guardrails are **executable checks** — not documentation, not hopes:

| Type | Example | Cost |
|---|---|---|
| File check | Does `feature.cs` exist? | Trivial |
| Build check | Does `dotnet build` exit 0? | Cheap |
| Test check | Do the specific new tests pass? | Medium |
| Port/API check | Does `GET /health` return 200? | Medium |
| AI judge | Does a second agent verify the output? | Expensive — last resort |

**Rule:** Start with the cheapest check that catches the failure. AI judges only when no deterministic check is possible.

> **Speaker note:** This is the thing that changes everything. Right now when an AI agent writes code and something is wrong, you find out much later. With Guardrails, the check runs immediately after the action. If the build doesn't pass, the harness knows before you do — and it tells the agent exactly what went wrong.

---

---
## SLIDE 8 — Retry with Targeted Feedback

When a guardrail fails, the harness **doesn't just try again** — it tells the agent exactly what failed:

```
Attempt 2 feedback:
  ✗ 01-build-passes.ps1 exited 1
    stderr: CS0103: The name 'CustomerRepository' does not exist in the current context
            src/Services/CustomerService.cs (line 47)

  ✗ 02-tests-pass.ps1 exited 1
    3 tests failed:
      CustomerService_GetById_ReturnsNull (expected NotNull)
      ...
```

The agent receives this as context on retry — **it knows what's wrong, not just that something is wrong**.

Default: 2 retries per task. Budget exhausted → task marked `NeedsHuman`.

> **Speaker note:** This is the difference between a useful failure and an inscrutable one. The harness composes the feedback from the guardrail outputs and injects it into the next prompt. The agent gets a second chance with a specific error message, just like a human would in a code review.

---

---
## SLIDE 9 — State Passing Between Tasks

Tasks share data through a **snapshot-in / fragment-out** model:

```
Task 01 runs:
  reads:  state snapshot (empty at start)
  writes: { "01-write-tests": { "testFileHashes": { ... } } }

Task 02 runs (after 01 succeeds):
  reads:  state snapshot including 01's output
  writes: { "02-implement": { "featureFile": "src/Feature.cs" } }

Task 03 runs (after 02 succeeds):
  reads:  full merged state — sees everything 01 and 02 produced
```

**No locks. No races.** The harness is the single writer; child processes only write their own fragment.

> **Speaker note:** This is how you build up context across a multi-task plan. Task 2 can reference what Task 1 produced — file paths, test names, API endpoints — without any shared mutable state. The harness merges everything safely.

---

---
## SLIDE 10 — Independent Branches Keep Running

```
        01-write-tests
              ↓
        02-implement         ← fails on attempt 3 → NeedsHuman
              ↓
        03-integration-test  ← Blocked (depends on 02)

        04-update-docs       ← Runs anyway (no dependency on 02)
        05-update-changelog  ← Runs anyway
```

**A failure doesn't stop unrelated work.**

Exit codes:
- `0` — all tasks succeeded
- `2` — one or more tasks need human review (durable progress saved)
- `1` — harness error (misconfiguration, missing file, etc.)

> **Speaker note:** This is important for long-running plans. If you have 12 tasks and task 3 fails, you don't want to lose the work done by tasks 4–12 that had no dependency on task 3. The harness surfaces all failures in one pass and saves its state so you can resume after fixing the problem.

---

---
## SLIDE 11 — Demo

*(Live demo — scenario from a current project)*

> **Speaker note:** [Run the demo here — approximately 8 minutes.]

---

---
## SLIDE 12 — Getting Started in 3 Commands

```bash
# 1. Install the tool
dotnet tool install --global ServantSoftware.Guardrails --prerelease

# 2. Install the Claude Code skills
guardrails skills install

# 3. Break down your plan and run it
#    (in Claude Code, after writing your plan.md)
/plan-breakdown
guardrails run my-plan/
```

**Requirements:**
- .NET 8+ runtime (runs on Windows, Linux, macOS)
- Claude Code CLI (for `/plan-breakdown` and prompt-action tasks)
- Scripts: PowerShell Core (`pwsh`) and/or `bash`

**NuGet:** `ServantSoftware.Guardrails` · current: `1.0.0-preview.3`

> **Speaker note:** The tool is available right now as a preview. I'm dogfooding it here at Tricentis as part of this engagement — so you're seeing the real thing, not a demo-ware version.

---

---
## SLIDE 13 — Questions?

### Key takeaways
1. **Agents need verification** — hope is not a strategy
2. **Guardrails = CI pipeline for AI agents** — executable checks, not documentation
3. **Failure is recoverable** — targeted feedback, retry, NeedsHuman, resume
4. **Human review is built in** — between plan-breakdown and execution

### Links
- GitHub: `github.com/Servant-Software-LLC/Guardrails`
- NuGet: `ServantSoftware.Guardrails`
- Docs: `docs/DEPLOYMENT.md` in the repo

> **Speaker note:** Happy to take questions, or if people want to pair on breaking down a real work plan after the session, I'm available.

---

*End of presentation*
