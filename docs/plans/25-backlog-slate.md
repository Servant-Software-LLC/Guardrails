# 25 — SUPERSEDED: the post-tiering slate (re-cut into plans 26 and 27)

> **Status: ABANDONED, never run. Superseded 2026-08-29 by `26-guardrail-quality-gate.md` (#510) and
> `27-operator-visibility.md` (#522/#523/#524).** The task folder was authored in full and then deleted;
> it is recoverable at commit `cec5627`, which is also where the #511 provider-quota chain (tasks 05-06)
> lives — that chain was dropped by the re-cut, since #511 is two isolated tasks and does not warrant a
> plan of its own.
>
> **Why it was the wrong shape.** It batched five unrelated issues to amortise the ~$10 breakdown floor
> — a cost argument, not a design one. A plan's unit is a coherent deliverable, and this was a shopping
> list. Three of its five items collided on the same observer files, forcing a serialized chain that
> gave up the parallelism the harness exists for; its docs sink coupled three otherwise-independent
> chains; and delivery is all-or-nothing, so one failing chain would have stranded every other chain's
> finished work (#525).
>
> The document is kept because plans 26 and 27 both cite it, and because the contention table in §0 and
> the risks in §7 are the evidence for the re-cut. Nothing here should be executed.

**Issues:** #510, #511, #524, #522, #523.

Four independent deliverables chosen after the first tiered run (plan 24) landed. They are grouped into
one plan because three of them collide on the same files — not because they are one feature.

## 0. The shape, and the one constraint that decides it

**FLAT, not waved.** Nothing here builds on another item's materialized output; the ordering below is
about file contention, not about staged evidence.

**Three of the four items want the same observer files.** Measured before planning:

| file | wanted by |
|---|---|
| `Guardrails.Cli/ConsoleRunObserver.cs` | #524, #522/#523, #511 |
| `Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs` | #524, #522/#523, #511 |
| `Guardrails.Cli/Ui/LiveRunObserver.cs` | #524, #511 |
| `Guardrails.Cli/Commands/RunCommand.cs` | #522/#523, #510 |

Run in parallel, two tasks appending a member to `ConsoleRunObserver.cs` on separate branches merge with
**no conflict marker and two copies** — the CS0101 that red-halted plan-0009 (#175). So:

> **Every task that writes an observer file sits on ONE dependency chain.** Parallelism is given up
> deliberately, in exchange for removing a merge hazard the union guardrail can only *attribute* after
> the fact rather than prevent.

#510 is isolated (a new verb plus the preflight phase) and runs in parallel with that chain.

## 1. #510 — execute the committed sample pairs

**The gap:** `grep -rn "samples" --include=*.cs src/` returns nothing outside tests. The two-sided
sample pair is the strongest anti-tautology device the skills have, and it is **a claim recorded in a
folder** — never executed. A pair can ship with reversed polarity, with an `.invalid` sample the
guardrail passes, or stale after the script was edited, and every one of those is indistinguishable from
a correct pair by inspection, which is the only inspection that happens.

This is the tagline inverted: for guardrail *quality*, a prompt proposes and a prompt certifies.

**Settled here (the issue offered three homes; this plan picks two of them and rejects the third):**

- **A new verb, `guardrails samples verify [folder]`** — walks every `tasks/<id>/samples/` pair, runs the
  matching guardrail against each half, asserts `.valid` → exit 0 and `.invalid` → non-zero, and reports
  every mismatch with the guardrail path, the sample path and the observed code. CI-runnable, cheap,
  read-only apart from its own temp dirs.
- **A preflight-phase step in `run`** that invokes the same verifier, so a bad pair fails **before any
  task spends a token**.
- **NOT in `validate`.** Validate is static and offline, runs in editors and mid-authoring, and must stay
  that way. Making it execute arbitrary PowerShell is a semantic change this plan does not make.

**Polarity note that must survive into the implementation:** the harness already lints the guardrail that
can never PASS (**GR2055**). The dangerous polarity — the guardrail that can never FAIL — has no check,
and running the `.invalid` half *is* a can-never-fail detector. Say so in the failure text; an operator
who understands why the check exists will not delete it.

**Done when:** the verb exists and reports every mismatch class; the preflight step calls it and halts the
run before the DAG on a bad pair; a pair with reversed polarity, a passing `.invalid`, and a missing half
each produce a distinct, actionable message; and the SSOT records the verb and the phase step.

## 2. #511 — a provider quota limit at a wave barrier ends the run

A 429 **inside a task** is ridden out by the shipped #115 pause. The same 429 **at a wave barrier** ends
the run. Same signal, same provider, two outcomes, and the barrier is exactly where a long unattended run
has the most invested.

**The shape:** `nextProbe = min(resetInstant, now + probeInterval)` with a 30-minute default — wait and
re-probe rather than terminate, reusing the existing `PromptFailureKind` classification and the shipped
pause machinery rather than inventing a second path.

**Done when:** a barrier-time provider limit pauses and re-probes instead of ending the run; the operator
sees a pause with its reason and next-probe time, not a failure; and the wait is bounded and surfaced.

## 3. #524 — the run recorded which model ran and never surfaced it

Measured on plan 24's run: the run-level `index.html` contains **zero occurrences of "model"**;
`attempt-route.log` is purpose-built, correct, and **linked from nowhere**; the console line is emitted
above a pinned live region and is out of view by the time anyone asks.

The maintainer asked *"where do I see which model it chose?"* about a task that had already finished — and
a transient line cannot answer a question asked after the fact.

**Done when:** the model appears **in the task table row** (beside cost and duration, where it persists);
the run-level log index shows the model per task; and `attempt-route.log` is linked by name from the task
page with a label saying what it answers.

## 4. #522 — the live diagram's links are correct for a server that does not serve it

The diagram emits plan-folder-relative hrefs (`tasks/<id>/guardrails/<file>.ps1`). Measured against a live
run: `GET /tasks/01-…` → **200**, `GET /diagram.html` → **404**. So the only way to open it is `file://`,
where those paths resolve against the flat, script-free `logs/<runId>/` layout and every click 404s.

**Done when:** the log-site server serves the live diagram, so the existing hrefs resolve as authored. The
`file://` copy either works or says plainly that it is not the live view — a second link convention is what
created this bug and is not the fix.

## 5. #523 — the live diagram reloads the whole document every 3 seconds

`<meta http-equiv="refresh" content="3">`. The blink is the cheapest cost: pan/zoom and scroll die every
tick, clicks are racy, the Mermaid graph is re-laid-out for a diagram that changes only at task
boundaries, and it never stops after the run ends.

**Done when:** the page updates without a whole-document reload, or — as the smaller acceptable outcome —
it stops refreshing at a terminal state and the interval reflects how fast a DAG's status actually changes.

## 6. Out of scope, deliberately

- **#505 Deliverable C** (a `record-plan-source` verb for the interactive `/plan-breakdown` door).
- **#521's doctrine work.** It partly dissolves once #510 lands: a sample pair for the guardrail that
  shipped the `nameof` hole would have caught it mechanically. Re-triage after, rather than writing more
  prose about a class of defect #510 gates.
- **#518 / #520** (test-reliability). Real, small, not blocking.
- Any change to `validate`'s static-and-offline contract.

## 7. Risks this plan accepts, stated so they are not discovered

- **The observer chain is serial**, so wall-clock is longer than the task count suggests. That is the
  trade for removing the duplicate-definition merge hazard.
- **#523's "right" fix (DOM updates over a status endpoint) is larger than its symptom.** The plan permits
  the smaller outcome — stop at terminal state, widen the interval — rather than forcing a rewrite of the
  live viewer inside a backlog plan.
- **#510's preflight step adds work before every run.** It must be cheap and it must be skippable in the
  measurement, or it becomes a tax on every task in every plan.
