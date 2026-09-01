## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `01-author-tests-bucket-classifier`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-author-tests-bucket-classifier": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements the first half of section 3.2 of `docs/plans/30-telemetry-phase-1.md`.
**Read section 3.2 in full** — its table carries the six bucket rules verbatim, and the paragraph
above it carries the constraint that decides this type's SIGNATURE. Where this prompt and the plan
disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not
work.** Do not touch provenance-on-failed-attempts.

## Task

Author **two** files, and only these two.

### 1. `src/Guardrails.Core/Telemetry/TaskFingerprintBucket.cs` — the minimal stub

A `public static class TaskFingerprintBucket` in namespace `Guardrails.Core.Telemetry`, carrying:

- Six `public const string` bucket names, exactly these values:
  `TestAuthoring = "test-authoring"`, `Implementation = "implementation"`,
  `Structural = "structural"`, `CodePlusTests = "code+tests"`,
  `Documentation = "documentation"`, `NoWrite = "no-write"`.
- One method, with **exactly this signature**:

  ```csharp
  public static string? Classify(
      IReadOnlyList<string>? writeScope,
      IReadOnlyList<Guardrails.Core.Model.GuardrailDefinition> guardrails)
  ```

  whose body is `throw new NotImplementedException();`.

**The signature is the point, and it is load-bearing.** Section 3.2 quotes the report's own legend —
*"a bucket is a fact about a task, never one read off its name"* — and the reason this method takes
a `writeScope` list and a guardrail list rather than a `TaskNode` is that a parameter list with no
task identity in it makes reading the bucket off the name **impossible for the compiler to allow**,
not merely discouraged. Do NOT add a `TaskNode`, a `taskId`, a `name`, or an `id` parameter, and do
not "helpfully" widen it to take the whole task. A guardrail on the implementation task checks this.

Return type is `string?` on purpose: `null` means *no rule matched this write surface*, which the
corpus reader already renders as `(unbucketed)`. Do not invent a seventh sentinel string.

### 2. `tests/Guardrails.Core.Tests/Telemetry/TaskFingerprintBucketTests.cs` — the failing tests

Class **`TaskFingerprintBucketTests`**, `public sealed`, carrying
`[Trait("Category", "ModelEvidence")]` on the class (the convention every shipped telemetry suite in
this project uses — see `tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs:15`).

Encode **exactly these nine behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | an EMPTY `writeScope` (`[]`, the deliberate "writes nothing" declaration) is `no-write` | `EmptyWriteScope_IsNoWrite` |
| 2 | a NULL `writeScope` (the off-switch — not the same claim as `[]`) yields `null`, never `no-write` | `NullWriteScope_IsNull_NotNoWrite` |
| 3 | writes `tests/**` only AND carries a TDD-red guardrail (`tests-fail-on-stubs` or `tests-fail-on-current-code`) is `test-authoring` | `TestsOnlyWithATddRedGuardrail_IsTestAuthoring` |
| 4 | writes `src/**` only, gated by a `tests-pass` guardrail, is `implementation` | `SrcOnlyGatedByTestsPass_IsImplementation` |
| 5 | writes `src/**` only with NO behavioural gate is `structural` | `SrcOnlyWithNoBehaviouralGate_IsStructural` |
| 6 | writes `tests/**` only with NO behavioural gate is `structural` too | `TestsOnlyWithNoBehaviouralGate_IsStructural` |
| 7 | writes BOTH `src/**` and `tests/**` is `code+tests` **even when it also carries a TDD-red guardrail** — the write surface decides, and the guardrail shape does not override it | `BothSrcAndTests_IsCodePlusTests_EvenWithATddRedGuardrail` |
| 8 | writes `docs/**` / `.claude/**` only is `documentation` | `DocsOrClaudeOnly_IsDocumentation` |
| 9 | a write surface no rule matches (e.g. `src/**` together with `docs/**`) yields `null`, so the reader renders `(unbucketed)` rather than a guessed bucket | `AWriteSurfaceNoRuleMatches_IsNull` |
| 10 | `Classify`'s signature admits no task identity — **by reflection**: `typeof(TaskFingerprintBucket).GetMethod("Classify")` has exactly two parameters, named `writeScope` and `guardrails`, and no parameter's type is `TaskNode` nor is any parameter named `taskId` / `id` / `name` | `ClassifySignatureAdmitsNoTaskIdentity` |

**Behaviour 10 is the one test in this file that will be GREEN when you finish, and that is correct.**
The stub you write already carries the pinned signature, so a correct test passes against it. Its
guardrail declares it as an exemption and asserts only that it RAN. Do not "fix" it into failing, and
do not mark it `[Fact(Skip=…)]` — a skipped exemption is no coverage at all. It exists because the
implementation task writes this same file, so this is the check that stops the signature being widened
later to admit the task's name; that is the report legend's constraint made mechanical rather than
merely intended.

Behaviour 7 is the disambiguator and the reason the plan calls `code+tests` a named bucket rather
than an "other": section 3.2 measured 67 of 74 multi-root tasks as exactly this shape.

Every test must **invoke `TaskFingerprintBucket.Classify` and assert on its return value**. A test
that constructs inputs and asserts something about them without calling `Classify` is hollow: it
passes against the `NotImplementedException` stub and this task's guardrail will name it.

Build the `guardrails` argument from real `GuardrailDefinition` instances (see
`src/Guardrails.Core/Model/GuardrailDefinition.cs` for its required members — `Name` is the basename
without extension, which is what the archetype rules read). Do NOT introduce a test double or a new
interface; the type is already constructible.

**Do NOT implement `Classify`.** The tests MUST COMPILE and FAIL against the throwing stub — failing
is intentional; not compiling is a mistake to fix.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Telemetry/TaskFingerprintBucketTests.cs` and
`src/Guardrails.Core/Telemetry/TaskFingerprintBucket.cs` (the stub file). After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths — including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out
path and stop.
