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
   Add ONE event for the advisory, with a **defaulted empty body** so the addition is non-breaking.
   `ParallelismClampedNoProvider(int requested)` shows that declaration shape — **and nothing else;
   see the warning below.** Declare the parameters with **public or primitive types** (e.g.
   `(string taskId, string finding)`): `IRunObserver` is public, `Guardrails.Cli` has no
   `InternalsVisibleTo` into `Guardrails.Core`, and an `internal` finding type on this signature is a
   CS0051 inconsistent-accessibility error you cannot fix — `VerifierAdvisory.cs` is out of scope.
2. **`Scheduler`** raises it, **at the top of `RunAsync`, after the cycle check and before the
   integration handle is created** — NOT in the constructor. Iterate `plan.Tasks` (already the
   flattened union of every wave's tasks, so one walk covers a waved plan), resolve each actor route
   and its judge, ask `VerifierAdvisory` for the finding, and raise ONE event per affected task. A
   task with no affected judge raises nothing; a run with no findings prints nothing at all.
   **Resolve the actor route only for a PROMPT action.** `TaskExecutor.ResolveRoute` is private, so
   you will spell the call yourself — carry its `task.Action.Kind == ActionKind.Prompt` guard with
   it. Drop the guard and the walk emits advisories for script tasks that never resolve a judge at
   all, which is noise the operator cannot act on.
3. **BOTH leaves render it.** There are two, and the chain differs by mode — see
   `RunCommand.cs`'s composition root:
   - **live / default:** `OnTheFlyDiagramObserver` → `OnTheFlyLogSiteObserver` → **`LiveRunObserver`**
   - **`--no-ui`:** `OnTheFlyDiagramObserver` → `OnTheFlyLogSiteObserver` → **`ConsoleRunObserver`**
   `ConsoleRunObserver` is **not in the live chain at all**. Render in only one and the advisory is
   invisible in the other. In `LiveRunObserver` follow `PlanHashMismatch` / `DecisionRecorded` — a
   run-level `AnsiConsole.MarkupLine` under the `_gate` lock (the Scheduler runs *inside* the Spectre
   live region, so a raw `Console.Write` corrupts the table). In `ConsoleRunObserver` follow its
   `_output.WriteLine` calls.
4. **The two decorators must FORWARD it** — see below.

> **Do NOT copy `ParallelismClampedNoProvider` beyond its declaration shape.** It is declared, raised,
> and forwarded by both decorators — and **rendered by neither leaf**. It is a dead event: its "loud
> demotion notice" prints nowhere, in either mode. It is a live instance of the bug this task exists
> to prevent, so treat it as the cautionary case, not the worked example.

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

**Scope boundary (harness-enforced):** Write only to the six paths in this task's `writeScope`.
Four of them are tiny: `IRunObserver.cs` is the event declaration, the two `Ui/OnTheFly*Observer.cs`
decorators are one forward each, and the two leaf renders are a few lines apiece. The real work is
the Scheduler walk.

*(`guardrails validate` warns GR2042 on a six-path scope. That warning has been reviewed and
accepted rather than split: the deliverable is **one** end-to-end wire, and splitting Core from CLI
would leave the first task's guardrail unable to see whether the event is rendered at all — which
recreates the exact "raised into a void" hole this task exists to close. Keep the change narrow
instead: four of the six files take only a handful of lines.)* After this task completes, the harness runs a `git diff` check and rejects any
edit outside those paths — including `VerifierAdvisory.cs` (task 10 owns it), `GuardrailRunner.cs`
(task 12), `TierResolver.cs`, or the `.csproj`.

`Scheduler.cs` runs every task of every plan in this repo. Add your walk beside the diagnostics
already there; do not restructure a method around it.
