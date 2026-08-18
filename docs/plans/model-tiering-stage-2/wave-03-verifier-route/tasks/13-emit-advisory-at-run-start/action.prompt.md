## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/13-emit-advisory-at-run-start`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/13-emit-advisory-at-run-start": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Surface the DoR §6.5 **verifier advisory** at **run start** — one line per affected task, before the
DAG executes — so an operator learns that a judge is weaker than the work it grades before paying
for the run, rather than by reading `run.json` afterwards.

This is the other half of §6.5's de-duplication ruling. Task 12 owns the JIT half (record silently
into provenance, log only on a difference); you own the run-start half (say it once, up front).

### The seam — all four pieces already exist

1. **`IRunObserver`** (`src/Guardrails.Core/Execution/IRunObserver.cs`) is the run's event surface.
   Add ONE event for the advisory. Follow **`ParallelismClampedNoProvider(int requested)`** exactly:
   it is the existing precedent for a one-off run-level diagnostic, right down to the defaulted
   empty body that keeps the addition non-breaking.
2. **`Scheduler`** raises it. `ParallelismClampedNoProvider` is raised near the top of the run — put
   your walk beside it. Iterate the plan's tasks, resolve each actor route and its judge, ask
   `VerifierAdvisory` for the finding, and raise ONE event per affected task. A task with no
   affected judge raises nothing; a run with no findings prints nothing at all.
3. **`ConsoleRunObserver`** renders the line.
4. **The two decorators must FORWARD it** — see below.

### The trap this task exists to walk into deliberately

The new `IRunObserver` method will have a **default empty body**, which is what makes adding it
non-breaking. But `OnTheFlyDiagramObserver` and `OnTheFlyLogSiteObserver` are **decorators**: they
implement the interface and forward each call to an `_inner` observer. A method they do not
explicitly forward resolves to the **interface default** — the empty body — so your advisory would be
silently swallowed in exactly the mode most operators run.

Grep `ParallelismClampedNoProvider` in both files: each carries a one-line forward. Add the same
one-line forward for your new event in both. Your guardrail checks precisely this, because it is
invisible to any test that goes through `ConsoleRunObserver` directly.

### It never halts, and it never blocks the DAG

Advisory means advisory. This walk runs before the DAG and must not fail the run, delay it
materially, or turn a finding into an error. If resolution throws for a task, skip that task — a
diagnostic that can abort a run is strictly worse than no diagnostic.

**Do not re-derive the rule.** `VerifierAdvisory` (tasks 09/10) decides what is and is not an
advisory condition. Call it. A second implementation of "is this judge weak" is the exact divergence
D22a exists to forbid.

### Scope

**Scope boundary (harness-enforced):** Write only to the five paths in this task's `writeScope`.
Three of them (`IRunObserver.cs` and the two `Ui/OnTheFly*Observer.cs` decorators) take **one line
each** — the event declaration and its two forwards. The real work is the Scheduler walk and the
Console rendering. After this task completes, the harness runs a `git diff` check and rejects any
edit outside those paths — including `VerifierAdvisory.cs` (task 10 owns it), `GuardrailRunner.cs`
(task 12), `TierResolver.cs`, or the `.csproj`.

`Scheduler.cs` runs every task of every plan in this repo. Add your walk beside the diagnostics
already there; do not restructure a method around it.
