## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `03-forward-runfinished-in-decorators`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "03-forward-runfinished-in-decorators": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "03-forward-runfinished-in-decorators": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

Make the forwarding tests authored in task 02 pass, by declaring `RunFinished` on every transparent
decorator so it reaches the whole chain.

**Scope boundary (harness-enforced):** Write only to the four decorator files in this task's `writeScope`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT edit the tests authored upstream.** They are the specification. If one is genuinely wrong,
write `{"needsHuman": "<why>"}` to the state-out path and stop rather than changing it - an
out-of-scope edit to a test file fails the task immediately and consumes a retry.

### Declare and forward, in all four

Add an explicit `RunFinished(int? exitCode, string? faultKind)` to each of:

- `src/Guardrails.Core/Execution/RunEventStream.cs`
- `src/Guardrails.Core/Execution/ObserverProjection.cs`
- `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`
- `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs`

In **this** task every one of them is a **pure pass-through** to the inner observer:

```csharp
/// <inheritdoc/>
public void RunFinished(int? exitCode, string? faultKind) => _inner.RunFinished(exitCode, faultKind);
```

Writing the `run-finished` ROW in `RunEventStream` is task 05; recording it in `ObserverProjection` is
task 07. Here the member must exist and forward, nothing more.

**Why explicit rather than inherited:** the interface gives it a default empty body, so a decorator
that leaves it to the default compiles, satisfies the interface, and silently swallows the event for
everything further down the chain. That is the trap this task exists to close.

### Do NOT declare it on the renderers

`LiveRunObserver` and `ConsoleRunObserver` must **not** declare `RunFinished`, and they are not in your
`writeScope`. `RunFinished` is the first `IRunObserver` call made on the chain *after* the Spectre live
loop is torn down, so declaring it on a renderer is a use-after-dispose. Task 02 has a test asserting
they do not declare it.

### Done when

Every `ObserverForwardingSweepTests` test passes, and nothing else in either suite regresses.
