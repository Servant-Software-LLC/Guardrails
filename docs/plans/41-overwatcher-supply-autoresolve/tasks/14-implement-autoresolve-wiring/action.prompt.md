## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `14-implement-autoresolve-wiring`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "14-implement-autoresolve-wiring": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Wire the missing-resource auto-resolve into the **real composition root**. This is the seam issue #712
says does not exist: `OverwatchSupplyAutoResolve` shipped in plan 40 as a function with **ZERO production
callers**. Every other task in this plan built a piece that is certified in isolation; **this task is the
only one that puts them on the path a CLI run actually takes.**

Design of record: `docs/plans/41-overwatcher-supply-autoresolve.md`. **§4 (consumer and re-arm) and §7
(ownership of the seam) are your specification.** §2.1 gives the three tiers, §3.1 the gate's check
order, §3.3 the ordering inside `OnSettledAsync`, §5 the drain target and provenance, §6 the records and
the delivery interlock.

**Two files, both already in your `writeScope`:**

- `src/Guardrails.Core/Execution/Scheduler.cs` — a private
  `TryAutoResolveMissingResourceAsync(context, task, result, handle, ct)` returning the adopted result and
  handle, or nothing, called from `OnSettledAsync` **between the green settle and the existing
  classify-then-act dispatch** (§3.3).
- `src/Guardrails.Core/Execution/SchedulerFactory.cs` — `Create` builds **ONE** `Overwatch` and hands the
  same instance to both `TaskExecutor` and the `Scheduler`, through a new optional constructor parameter.

### The order inside `OnSettledAsync` (§3.3)

1. green settle (today);
2. **the missing-resource auto-resolve (new)**;
3. `ClassifyTaskGateAsync` on the **adopted** result (today);
4. the #550 best-guess re-drive, on the **adopted** handle (today).

**Certified and committed** ⇒ the original halt never reaches the classify-then-act dispatch, and the
`CriticalityJudge` is not consulted about it. **Anything else** — not engaged, wrong shape, a tier-2 stop,
no candidates, no verdict, refused — ⇒ the original halt goes to `ClassifyTaskGateAsync` exactly as it
does today. That is the never-weaker path, and it is most of the branches.

The re-armed run gets **nothing injected**: no overwatcher guidance, no best-guess text. The only thing
that changed is that the file is now there. Keeping the model's words out of the next attempt is the #709
lesson.

### The steps (§4)

1. **Tiers 0–2** (§2.1). Tier 0 and tier 1 write no record; each tier-2 stop writes one `observed`
   decision with its reason token.
2. **Facts** (§2.2) via `MissingResourceFacts`. No candidates, or `facts-unavailable`, ⇒ `observed`.
3. **Propose** (§2.3) via `Overwatch.ProposeResourceSupplyAsync`. A no-verdict is recorded by the
   existing path.
4. **Certify** (§3.1) via `OverwatchSupplyAutoResolve.Certify`. A refusal is recorded as `advisory`.
5. **Commit, under `_integrationLock`:** re-check every certified path against the integration `HEAD`
   (`run-base-changed`); `git checkout <sourceCommit> -- <paths>` in the **integration worktree**
   (`context.Integ.IntegrationWorktreePath`, **never `plan.Workspace`**); commit through
   `SuppliedDrain.CommitPaths`. On failure restore the pre-commit `HEAD`, record `advisory` with
   `commit-failed`, and let the original halt stand.
6. **Record, still under the lock and before any further fallible step:** `RecordSupplied { by: "overwatcher" }`,
   the `auto-supplied` decision with its source sha raised through `DecisionRecorded`, and
   `SuppliedResourcesCommitted(paths, commit, "overwatcher")`. **Writing the decision here is what keeps
   delivery suppressed however the re-arm below ends** — do not move it later.
7. **Cost cap.** If `CostCapHaltFor(task)` now returns a halt, record `advisory` with
   `rearm-skipped-cost-cap` and adopt that cost-cap result.
8. **Re-arm.** `CreateSegment(task.Id, runJournal.NextAttemptNumber(task.Id), integ, ct)` under the lock —
   the attempt number matters, because `CreateSegment` names its branch and path `attempt-<n>` and a root
   task's original segment is already `attempt-1`. Register `context.Handles[task.Id]` and its directory
   ownership under `_gate` (load-bearing: dependents inherit through it). Leave the old segment owned for
   the end-of-run sweep. Release the lock, then `_executor.ExecuteAsync(task, rearmedHandle, ct)`, and
   send a green result through `SettleGreenIfWorktreeAsync`.
9. **Adopt the re-armed result whatever its outcome** — deliberately unlike #550, because here the base
   did change and "the file is missing" is now false. Leave `Summary` untouched.

Every step up to the commit is wrapped the way the #550 re-drive is wrapped, so a thrown git call or
runner never faults the run. Steps up to and including certification run **outside** the integration
lock: the diagnose can take minutes and other tasks' settles must not wait on it. Failures after the
commit record `advisory` with `rearm-failed` and report through `CleanupFailed`; the commit, its
`supplied[]` record and its `auto-supplied` decision all remain, because they are true.

### `SchedulerFactory` — one `Overwatch`, and a signature that does not move

`SchedulerFactory.cs:79` builds `new Overwatch(...)` today. Share **that** instance; do not build a
second one. The public `CreateExecutor(plan, processRunner, probe, observer, interaction)` **keeps its
signature and its tuple** — `Revalidate.cs:113` deconstructs it — so delegate to an internal overload
that accepts a prebuilt `Overwatch`.

### Corrected facts — earlier drafts of this design were WRONG on these

- `RunCommand.MissingResourceHaltLines` is declared at `RunCommand.cs:3357` and called at `:3291`. It is
  **not** at `:3339-3340`.
- `OverwatchSupplyAutoResolve.Resolve` has **ZERO production callers** today. Nothing you are replacing is
  reachable from a run.

Where this prompt states a line number, it was measured — but your ancestors have edited both files.
Before relying on any of them, run the grep:

```
grep -n "OnSettledAsync\|_integrationLock\|SettleGreenIfWorktreeAsync\|CostCapHaltFor" src/Guardrails.Core/Execution/Scheduler.cs
grep -n "new Overwatch(\|CreateExecutor" src/Guardrails.Core/Execution/SchedulerFactory.cs
```

**If your own grep disagrees with anything written here, trust the grep** and say so in your fragment.

### Your gate is the real-path proof — and you may NOT edit it

`tests/Guardrails.Integration.Tests/Supply/OverwatchSupplyAutoResolveWiringTests.cs` was authored RED by
task `01-author-tests-wiring-proof`. **It is NOT in your `writeScope`**, deliberately: a task that can
edit its own proof has no proof. All ten tests (P1, P2, C1–C7, N1) must be observed **executed, passed,
none skipped**.

- The pure `Certify`, parser and facts tests are necessary but **never sufficient** (design §7). A task
  whose only gate is those tests cannot be marked done — that is the #382 shape, and the plan 40 run that
  produced #712 took exactly that route.
- **C3, C5 and C6 are what prove `Certify` is on the real path.** A wiring that checked "any
  `resource-supply` op" and never called the gate is the #712 shape again, and it passes every other test
  in the file. **C7 proves the effective per-gate threshold is on the real path** — a per-gate
  `needs-human: high` under a run-wide `critical` must NOT engage the dial.
- **P2 proves the delivery interlock.** The positive path alone passes with or without it.

**Do not edit the authored tests, and do not weaken them.** If you become convinced a test is genuinely
wrong — not merely inconvenient, not merely hard — write
`{"needsHuman": {"question": "<the test, the assertion, and why it cannot be satisfied by a correct implementation>", "kind": "blocked-work"}}`
to the state-out path and stop. Difficulty is never a reason.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs` and
`src/Guardrails.Core/Execution/SchedulerFactory.cs`. After this task completes, the harness runs a
`git diff` membership check and rejects any edit outside these two paths — that check is what makes the
wiring proof a proof, and it explicitly excludes
`tests/Guardrails.Integration.Tests/Supply/OverwatchSupplyAutoResolveWiringTests.cs`. An out-of-scope edit
fails the task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path
and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
