## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/05-build-conformance-harness`, NOT the stableId and
  NOT the bare folder name. The harness REJECTS a fragment keyed by anything else
  (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/05-build-conformance-harness": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Build the **shared test host** every Stage 2 conformance test (task 06, and wave 3's verifier tests)
drives the real attempt-launch path through. It is authored ONCE here so the next two tasks — and the
next wave — do not each re-discover the same non-trivial setup.

- **`tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs`**
- namespace `Guardrails.Integration.Tests.ModelTiering`
- public class **`Stage2PlanHarness`** — this exact name; the next task's prompt and its guardrails
  reference it

**This is test INFRASTRUCTURE with no behaviour of its own, so it is deliberately TDD-exempt** — its
proof is that task 06's suite compiles and runs against it. It gets a build guardrail and a
structural shape guardrail instead of a test pair.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside that path — including any production
file, the conformance suite (task 06 owns it), or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

### Start from the shape that already works

**`tests/Guardrails.Integration.Tests/ActionModelResolutionTests.cs`** already does the core of this
for the `action.model` override (#200): it writes a temp plan folder, loads it with the real
`PlanLoader`, builds a `PromptRunnerRegistry` over a recording fake `IPromptRunner`, constructs the
real `TaskExecutor` + `Scheduler`, runs it, and reads the resulting `run.json` with `JournalReader`.
**Read that file first and generalize it** — do not invent a second, different harness shape.
*(This reflects the state at plan-authoring time; verify it is still what that file does before
relying on the details.)*

### What the harness must expose

1. **A parameterized plan builder.** One call configures, at minimum:
   - the `promptRunners` registry as a list of blocks, each with a name, optional `model`,
     optional `effort`, optional `kind`, optional `strength`, optional `costly`, and an optional
     `routing.tiers` list — enough to build a registry where a rung is served by exactly one block,
     by several of differing strength, by only a `costly: true` block, or by nothing at all;
   - the `promptRunners.default` pointer;
   - an optional plan-wide `tiering.defaultTier`;
   - one or more tasks, each with an optional `action.tier`, `action.model`, `action.runner`,
     `action.effort`, and an optional per-task retry count;
   - a per-task switch making the fake runner **fail the first attempt and succeed the second**, so a
     test can observe a RE-ATTEMPT (task 09's D28 warning fires on re-attempt, and one clause asserts
     that resolution runs per ATTEMPT rather than once per task).
   Emit the JSON with a real serializer or careful raw strings; whichever you choose, a malformed
   plan must surface as a loud assertion on `PlanLoadResult.HasErrors`, never as a mysterious later
   failure.
2. **A recording fake `IPromptRunner`** capturing every `PromptInvocation` in call order, with a
   per-call scripted `PromptResult` (success, or a failure with a chosen `PromptFailureKind`). This
   is the ONLY thing the harness fakes: it is the **process/CLI boundary**, which a test may fake.
   The in-process seam — `PlanLoader`, `Scheduler`, `TaskExecutor`, the resolver — is the thing under
   test and must be the REAL one (#382). Do not add a hook that lets a test substitute a resolution.
3. **A result object** exposing at least: the `RunReport`, the parsed `JournalDocument`, the recorded
   invocations, and the **plan root path** — the last so a test can read an attempt's log dir
   (`logs/<runId>/…`) and assert on the route disclosure file task 09 writes. Expose a small helper
   that returns the log-dir path (or its files) for a given task id + attempt number, reading it from
   the journal's `AttemptRecord.LogDir` (which is plan-relative) rather than reconstructing the
   layout by hand.
4. **Deterministic, isolated, self-cleaning.** A fresh temp root per run (`Path.GetTempPath()` +
   a GUID), `maxParallelism: 1`, a trivially-passing task guardrail, and teardown in a `finally` /
   `IDisposable` that tolerates an `IOException` on Windows — the shape `ActionModelResolutionTests`
   already uses. No network, no real `claude` process, no dependence on the developer's
   `~/.claude` settings.

### Two things to get right, because they are what makes the suite honest

- **Do not reference `TierResolver` or `TierResolution` from this file.** The harness observes the
  route through the journal and the captured invocation; a harness that calls the resolver would let
  every conformance test prove the resolver against itself instead of proving the WIRING.
- **Keep it a host, not a set of assertions.** No `Assert` on routing outcomes lives here (the
  plan-load sanity assertion is fine); the clauses belong in task 06's suite where the terminal
  gate's behaviour manifest can discover them by name.
