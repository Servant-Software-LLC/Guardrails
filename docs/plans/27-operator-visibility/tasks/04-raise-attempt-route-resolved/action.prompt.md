## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "04-raise-attempt-route-resolved": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Add ONE member to `IRunObserver` — `AttemptRouteResolved` — raise it at attempt **LAUNCH**, and forward
it explicitly from **both** transparent decorators. That is the whole task: no rendering, no console
line, no HTML. The surfaces that consume it are tasks 05 and 06.

**This is a contract change to a public interface.** It is its own task for that reason: folding it into
the rendering task would put a contract change and a column render in one write scope, which is the
over-scope fingerprint `guardrails validate` warns on (GR2042) and, more to the point, would let a
rendering failure and a contract failure share one retry budget.

### The measured defect this exists to fix

`IRunObserver.AttemptModelResolved` is correct for what it is and **fires only after the attempt's
action has already returned** — it carries best-known-actual, which the runner cannot report until it
has run:

```
src/Guardrails.Core/Execution/TaskExecutor.cs:726   ActionRun action = await _actionRunner.RunAsync(...)
src/Guardrails.Core/Execution/TaskExecutor.cs:802   _observer.AttemptModelResolved(task, attemptNumber, attemptModel, provenance.RequestedModel);
```

So any live surface fed **only** from it is a placeholder for the entire duration of the attempt.
Measured on this repo's own run `docs/plans/24-plan-source-provenance/state/run.json`: attempt
durations of **14m02s and longer**. The route, meanwhile, is resolved *before* anything launches
(`ResolveRoute` at `:648`, `BuildProvenance` at `:654`), and a §6.2 rung climb resolved there today
reaches **no console surface at all** — it is written only to `attempt-route.log`.

The design of record is `docs/plans/29-model-visibility-ux.md` §4.3 (and §1.1 for the measurement).
Read §4.3 before you write anything; it is short and it pins the signature you are being asked for.

**Write exactly four files:**

1. `src/Guardrails.Core/Execution/IRunObserver.cs` — the new member.
2. `src/Guardrails.Core/Execution/TaskExecutor.cs` — the raise, at launch.
3. `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs` — an explicit forward.
4. `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs` — an explicit forward.

**Scope boundary (harness-enforced):** Write only to those four paths. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — including
`src/Guardrails.Cli/Ui/LiveRunObserver.cs`, `src/Guardrails.Cli/ConsoleRunObserver.cs`, any file under
`tests/`, or any `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If
you hit a compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Navigate by SYMBOL, and know which line numbers are already stale

Tasks 01–03 ran before you and **one of your four files is in task 02's write scope**:
`OnTheFlyDiagramObserver.cs`. Every line number quoted for that file is stale — grep for the symbol.
The other three (`IRunObserver.cs`, `TaskExecutor.cs`, `OnTheFlyLogSiteObserver.cs`) are named by no
earlier task in this plan, so their line numbers were still accurate at authoring time; verify anyway
with `git log --oneline` and a read, and implement the **intent** if a landed shape makes an
instruction below impossible as written.

---

### 1. The new member — `IRunObserver.cs`

Add exactly this, **beside `AttemptModelResolved`** (which is the member it is the launch-time twin of),
with a doc comment in that member's own voice:

```csharp
/// A prompt attempt's ROUTE is resolved and the attempt is about to launch (#524).
/// Raised BEFORE the action runs — unlike AttemptModelResolved, which cannot fire until the
/// runner has reported what it ran on. `tier` is the rung SERVED (after any §6.2 climb),
/// `requestedTier` is non-null ONLY when the climb moved it, `runner` is the promptRunners
/// block key, `model` is the route's model. Default no-op.
void AttemptRouteResolved(
    TaskNode task, int attempt, string runner, string model,
    string? tier, string? requestedTier) { }
```

Three properties of that signature are load-bearing; keep all three:

- **Primitives only, no provenance type.** The reason is already written down two members above, on
  `AttemptModelResolved`: this interface is public, `Guardrails.Cli` has no `InternalsVisibleTo` into
  `Guardrails.Core`, and a provenance TYPE on the signature is inconsistent accessibility (CS0051) the
  moment that type is not public.
- **A default no-op body**, so no non-CLI observer, and no test double, has to change. Thirty-odd types
  implement `IRunObserver` across `src/` and `tests/`; an abstract member would break every one.
- **`requestedTier` is non-null ONLY when a climb moved the rung.** It is not "the tier that was asked
  for" written unconditionally. Its PRESENCE is the climb signal, exactly the way
  `AttemptProvenance.RequestedModel`'s presence is the substitution signal — an always-written copy
  destroys the signal and makes every ordinary attempt look like a climb.

Your doc comment must also carry the **decorator warning**, because the next person to add a decorator
reads the interface and not this prompt. `AttemptModelResolved`'s own doc block is the precedent and
the wording to follow: a transparent decorator must forward it EXPLICITLY, or the call resolves to the
empty default body and the disclosure is swallowed silently, in every mode.

### 2. The raise — `TaskExecutor.cs`

**Where.** In the method that runs one attempt, **after the §6.2 no-route branch has settled** and
**before `_actionRunner.RunAsync`**. Concretely, at authoring time: after the
`if (route is { NoRoute: true }) { return _journaler.NoRoute(...); }` block (`:677–681`) and before
`ActionRun action = await _actionRunner.RunAsync(` (`:726`). `route` (`:648`) and `provenance` (`:654`)
are both already in scope there, and `WriteRouteDisclosure` (`:663`) has already written the same facts
to `attempt-route.log` — so this raise adds **zero** new plumbing and re-derives **nothing**.

**Before `RunAsync` is the entire point, and a guardrail checks the ordering, not just the presence.**
A raise placed after the action returns is a second `AttemptModelResolved` wearing a different name and
fixes nothing: the fourteen minutes in §1.1 are exactly the window between these two anchors.

**The nullability trap — read this before you write the call.** The repo sets `<Nullable>enable</Nullable>`
and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props`, so **CS8604 is a
build ERROR here, not a warning**. The two values you want are nullable at the call site:

- `route.RunnerName` is `string?` (`TierResolution.RunnerName` — "Null when `NoRoute`");
- `provenance?.Model` is `string?` (`AttemptProvenance.Model` — "Null for a script attempt").

Both are non-null in practice on the path you are raising from (a script action makes `route` null
outright, and a prompt route always resolves a display model), but the compiler does not know that.
**Do not silence it with `!`.** Guard the raise on the pattern instead, so the code states the
precondition it actually depends on — something in the shape of:

```csharp
if (route is { RunnerName: { } runnerName } && provenance?.Model is { } routeModel)
{
    _observer.AttemptRouteResolved(
        task, attemptNumber, runnerName, routeModel,
        route.Tier, route.Climbed ? route.RequestedTier : null);
}
```

That guard is honest rather than defensive: with nothing to name, there is no route to disclose, and
the consuming surface keeps whatever it had. A `null!` or a `?? "unknown"` would invent a fact.

**A script attempt raises nothing, and that is correct.** `ResolveRoute` returns null unless
`task.Action.Kind == ActionKind.Prompt`, so the guard above already excludes scripts. Do not add a
second `Kind` test — one predicate, one owner.

**`route.Climbed` is the climb flag** and it is the ONLY correct source for it. Do not compare
`route.Tier` to `route.RequestedTier` yourself: `TierResolution.Climbed` "falls out of the candidacy
sweep", and re-deriving it here would be a second copy of a predicate that already has an owner.

### 3. Both decorators — the footgun this task exists around

`OnTheFlyDiagramObserver` wraps `OnTheFlyLogSiteObserver` wraps live-or-console, in **both** the live
and the `--no-ui` chain. A decorator that omits the new member **compiles cleanly**, satisfies the
interface, and silently drops the event for every operator in every mode. No build check can see it.
That is not hypothetical — it is written on `AttemptModelResolved`'s own doc block because
`VerifierAdvisoryFound` and the #469 breakdown-phase members were each lost this way once already.

Add to **each** decorator the one-line forward, in the shape the file already uses for
`AttemptModelResolved` (grep for it; it is the member immediately adjacent):

```csharp
public void AttemptRouteResolved(
    TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
    _inner.AttemptRouteResolved(task, attempt, runner, model, tier, requestedTier);
```

Forward the arguments **verbatim**. Neither decorator acts on this event: the route an attempt took is
not a shape of the DAG and not a log-site artifact, so each forwards and does nothing else. Carry a
short comment saying it is forwarded EXPLICITLY and why, matching the comment already sitting above
`AttemptModelResolved` in both files.

---

### What must NOT change — this is half the task

- **`AttemptModelResolved` is untouched.** Its four-argument signature, its wording, its raise point at
  `TaskExecutor.cs:802` and the `AttemptModelDisclosureTests` raise-count assertions all stand. The new
  event is ADDITIVE: the old one becomes the confirmation or correction of what the new one announced.
  A guardrail runs `AttemptModelDisclosureTests` and `AttemptModelForwardingTests` after you and will
  fail if you moved either.
- **`AttemptModelSummary` is untouched**, and you do not need it: it lives in `Guardrails.Cli` and
  nothing in this task formats a string for a human.
- **`WriteRouteDisclosure` and `attempt-route.log` are untouched.** The route is already written there
  correctly. This task adds an observer event beside that write; it does not replace it, reformat it,
  or move it.
- **No new field on `TierResolution` or `AttemptProvenance`.** Everything the event carries is already
  on one of them.

### The bar

- The raise site must READ the resolution, never re-run it. `route` and `provenance` are already in
  scope; a second `ResolveRoute(task)` call would be a third derivation of a decision that must have
  exactly one, and the two copies would disagree the day the resolver moves.
- The whole solution must build. Your diff spans two assemblies and a public interface: build
  `Guardrails.sln`, not one project.
- Publish what you shipped to the state-out path, so tasks 05 and 06 read the landed signature instead
  of re-deriving it:
  `{ "04-raise-attempt-route-resolved": { "member": "<the exact signature line you added>",
  "raiseSite": "<file:line of the _observer.AttemptRouteResolved call>",
  "forwardedBy": ["OnTheFlyDiagramObserver", "OnTheFlyLogSiteObserver"] } }`
