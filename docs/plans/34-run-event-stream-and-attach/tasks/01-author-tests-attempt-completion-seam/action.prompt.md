## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `01-author-tests-attempt-completion-seam`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-author-tests-attempt-completion-seam": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

`IRunObserver` (`src/Guardrails.Core/Execution/IRunObserver.cs`) has THREE attempt-scoped members -
`AttemptStarting`, `AttemptModelResolved`, `AttemptRouteResolved` - and **no member for an attempt
FINISHING**. The next thing an observer hears is `TaskFinished(TaskResult)`, after the whole retry loop.
That gap is why the shipped `[retry] <task>: attempt 2/3` line cannot say WHY an attempt failed.

Two artifacts, both inside your writeScope:

1. **The stub** - add ONE new default-implemented member to `IRunObserver`:

       void AttemptFinished(TaskNode task, int attempt, AttemptOutcome outcome) { }

   `AttemptOutcome` is the EXISTING enum at `src/Guardrails.Core/Journal/AttemptOutcome.cs` (14 members:
   Succeeded, ActionFailed, GuardrailFailed, Timeout, OutputCap, MaxTurns, RateLimited, Cancelled,
   InvalidFragment, NeedsHuman, PermissionDenied, TaskPreflightFailed, NoRoute). **Do NOT mint a new
   reason enum** - this plan binds the stream's vocabulary to what the journal and the telemetry corpus
   already record. Give the member an XML-doc comment in the file's own house style, including the
   standing warning that a transparent DECORATOR must forward it EXPLICITLY (the same note
   `WaveGateFinished` and `VerifierAdvisoryFound` already carry).

2. **The tests** - create
   `tests/Guardrails.Integration.Tests/RunEvents/AttemptCompletionForwardingTests.cs`, class
   **`AttemptCompletionForwardingTests`**, every test carrying `[Trait("Category", "RunEvents")]`.

   Pin these test METHOD names, one behaviour each. Each constructs the REAL decorator with a recording
   `IRunObserver` as its inner observer, raises `AttemptFinished` on the decorator, and asserts the
   recorder observed it:

   - `OnTheFlyLogSiteObserver_ForwardsAttemptFinished`
   - `OnTheFlyDiagramObserver_ForwardsAttemptFinished`
   - `OnTheFlyLogSiteObserver_ForwardsOutcomeVerbatim` - assert the forwarded value IS
     `AttemptOutcome.MaxTurns`, not merely that something arrived
   - `EveryDecorator_ForwardsAttemptFinished_ForEveryOutcome` - a `[Theory]` over every `AttemptOutcome`
     member, so a decorator that special-cases one value cannot hide

   These MUST COMPILE and FAIL. Failing is intentional - the decorators inherit the interface's empty
   default body and swallow the call. NOT compiling is a mistake to fix. Do NOT edit the decorators;
   that is task 02.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RunEvents/AttemptCompletionForwardingTests.cs` and
`src/Guardrails.Core/Execution/IRunObserver.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
