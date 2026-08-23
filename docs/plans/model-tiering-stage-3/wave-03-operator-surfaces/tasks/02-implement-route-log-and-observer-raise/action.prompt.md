## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-03-operator-surfaces/02-implement-route-log-and-observer-raise": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt), including the bare folder
  name and the stableId.
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

Make `AttemptModelDisclosureTests` pass. Two changes, both in
`src/Guardrails.Core/Execution/TaskExecutor.cs`, both at the same point in the attempt: the moment the
runner-reported model becomes known.

### The one structural fact that makes this task what it is

`attempt-route.log` is written **before the action runs**, and the observed model is only known
**after it returns**. Find both points by their durable markers — do not rely on line numbers, and do
not trust this prompt's description of the surrounding code over what you read:

- **The launch-time disclosure.** Grep for `static void WriteRouteDisclosure` (the writer) and for the
  call site, which sits immediately below the comment beginning
  `// #201 / DoR §6.2: the same ROUTE, in prose, beside it`.
- **The fold.** Grep for `--- #349: fold the OBSERVED model onto this attempt's provenance ---`. Inside
  that block, `provenance` is REASSIGNED to a copy carrying `Model` = best-known-actual and
  `RequestedModel` = the route's model **only when the two disagree**, and the block then re-mirrors
  `attempt-provenance.json` via `AttemptArtifacts.WriteProvenance(logDir, provenance)`.

That re-mirror is the precedent and the reason it exists is stated in place: on the guardrail-FAILED
path `attempt-provenance.json` is the only surface that records the fold at all. **The prose twin has
exactly the same problem and does not yet get the same treatment.** That asymmetry is this task's first
deliverable.

### 1. The log preamble must name the model that actually ran

`WriteRouteDisclosure` builds its `model: ` line from `provenance.Model`, which is already
best-known-actual **once the fold has happened** — so the writer barely needs to change. What it needs
is to be invoked again with the folded object, and to disclose the second string when there is one.

- Add a **`requested model: `** line, emitted **only** when `provenance.RequestedModel` is non-null.
  Its presence *is* the mismatch signal (read the `RequestedModel` doc comment in
  `src/Guardrails.Core/Journal/JournalModel.cs`); an always-written line would be a duplicate of
  `model: ` in the overwhelmingly common agreeing case, which is precisely what the contract refuses.
- `requested model:` is **not a new format** — it is the file's own `key: value` idiom and the exact
  sibling of the `requested tier:` line already there. Place it beside the existing model/tier lines,
  not in the WARNING block at the bottom: a mismatch is a disclosure, not a route change the harness
  made.
- **Re-invoke the disclosure from inside the fold block**, next to the existing
  `AttemptArtifacts.WriteProvenance` re-mirror, so the file on disk reflects what actually ran. The
  launch-time call stays: an attempt that dies before the runner returns must still leave a route log.
  Writing it twice is correct and cheap — the second write supersedes the first, exactly as the
  provenance re-mirror does. Keep the whole thing best-effort: an IO hiccup must never fail an attempt
  over a disclosure artifact, which is why the existing writer swallows `IOException` /
  `UnauthorizedAccessException`.

### 2. The attempt loop must raise `AttemptModelResolved`

Task 01 declared it on `IRunObserver`:

```csharp
void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) { }
```

Raise it through the executor's own `_observer` field (grep for `_observer.AttemptStarting` to see the
per-attempt precedent) once the best-known-actual model for this attempt is settled. Pass
`provenance.Model` and `provenance.RequestedModel` **verbatim** — do not recompute the comparison, do
not re-derive either string. There is exactly one place that decides which model this attempt ran on,
and it has already decided by the time you raise.

Raise it for **every prompt attempt that resolved a model**, mismatch or not: the agreeing case is the
common case and an operator who sees nothing cannot tell "it agreed" from "the surface is broken". An
attempt with no provenance at all (a script attempt in serial mode) has no model to announce and must
raise nothing — a `model` of `null` has no meaning on this signature.

### What NOT to do — and these are checked

- **Do not re-parse the stream.** Wave 2 owns capture; `ClaudeStreamParser` has no business in
  `TaskExecutor.cs`, and a second parse is a second derivation of a fact that must have exactly one.
- **Do not force `--model` onto the runner invocation.** Forcing one would pin the zero-setup user who
  deliberately passes nothing, and would record the model we *requested* — the weaker fact, and the
  exact thing #349 exists to stop reporting.

A guardrail scans this file (comments and string literals stripped) for both. It is a fail-on-present
check, so writing either as a USE fails the task.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/TaskExecutor.cs`.
The harness runs a `git diff` check after this task and rejects any edit outside that path — including
the three test files, `IRunObserver.cs`, `LiveRunObserver.cs`, `JournalModel.cs`, and the two on-the-fly
decorators. An out-of-scope edit fails the task immediately and consumes a retry. **In particular: do
NOT edit the authored tests.** If a test in `AttemptModelDisclosureTests` is genuinely wrong or
incompatible with the shipped contract, write
`{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the state-out path rather than
changing it.

### Done when

`dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~AttemptModelDisclosureTests"`
passes — five tests, covering both surfaces of this task in both the agreeing and the disagreeing case.
The other two new classes (`AttemptModelRenderingTests`, `AttemptModelForwardingTests`) stay RED; tasks
03 and 04 own them, and your guardrail does not select them.
