## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `17-author-tests-run-environment`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "17-author-tests-run-environment": { "someKey": "someValue" } }`.
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

This task authors the failing tests, plus the minimal throwing stub they compile against, for the
**machine and concurrency profile** item of section 3.4 of `docs/plans/30-telemetry-phase-1.md`:
*"machine and concurrency profile including unified memory, harness and skill versions"*. **Read
section 3.4 in full.** Where this prompt and the plan disagree, the plan is authoritative and you
should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

### Why unified memory is on this list at all

Section 3.4 records a fact the maintainer **confirmed on 2026-09-01**: the 64GB Mac Studio is a
**tighter** box than the 128GB MacBook available today, so **the same model name will run at a
different quantization on each and must not be pooled as one sample.** That is the entire reason the
record carries a memory figure — without it, two rows naming the same local model are indistinguishable
in the corpus while being, in fact, two different models.

On Apple silicon the unified pool is what `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` reports,
which is why the member is named `TotalMemoryBytes` rather than anything more specific.

## Task

Author **two** files, and only these two.

### 1. `src/Guardrails.Core/Journal/RunEnvironmentProbe.cs` — the minimal stub

A **`public static class RunEnvironmentProbe`** in namespace `Guardrails.Core.Journal`, carrying one
method with **exactly this signature**:

```csharp
public static RunEnvironment Probe(int maxParallelism, string? harnessVersion, string? skillVersion)
```

whose body is `throw new NotImplementedException();`.

`RunEnvironment` is the record `03-extend-the-journal-record-shape` already added to
`src/Guardrails.Core/Journal/JournalModel.cs`. **Read its declaration before you write anything** — it
carries `Host`, `Os`, `CpuCount`, `TotalMemoryBytes`, `MaxParallelism`, `HarnessVersion` and
`SkillVersion`, and your tests must spell those members exactly as it declares them.

**`public`, not `internal`.** `18-record-the-run-environment` calls this from
`src/Guardrails.Cli/Commands/RunCommand.cs`, which is a different assembly.

**The signature is the point, and it is load-bearing.** The two version strings are **passed in**
rather than read here, and the constraint that decides that is real:

- The harness version is `GuardrailsVersion.Current` — `src/Guardrails.Cli/GuardrailsVersion.cs`. It
  lives in **`Guardrails.Cli`**, and `Guardrails.Core` cannot reference it: `Guardrails.Cli` depends on
  `Guardrails.Core`, not the other way round, and there is no `InternalsVisibleTo` that would change
  that. A probe that tried to read it would not compile.
- The skill version is read by `SkillFrontmatter.ReadGuardrailsVersion`
  (`src/Guardrails.Core/Prompts/SkillFrontmatter.cs`) **from the text of an installed `SKILL.md`** —
  note it takes the file's CONTENT, not a path. Finding which `SKILL.md` is installed is CLI knowledge
  (`SkillVersionReport` in `Guardrails.Cli` does it), and the answer is legitimately `null` when no
  skill is installed.

So the probe stays in Core and the CLI supplies what only the CLI knows. **Do not add a `TaskNode`, a
`PlanDefinition`, a `RunConfig`, or any parameter that would let the probe reach for a value it is
supposed to be told.** Do not widen it to read the versions itself.

`maxParallelism` is an `int` and not nullable: a run always has an effective concurrency, even when it
is 1.

### 2. `tests/Guardrails.Core.Tests/Journal/RunEnvironmentTests.cs` — the failing tests

Class **`RunEnvironmentTests`**, `public sealed`, in namespace `Guardrails.Core.Tests.Journal`,
carrying `[Trait("Category", "ModelEvidence")]` on the class (the convention every shipped telemetry
suite in this project uses — see `tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs`).

Encode **exactly these four behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | the probe records the machine's host name, its OS description and its CPU count | `TheProbeRecordsHostOsAndCpuCount` |
| 2 | the probe records total available memory, the figure the unified-memory comparison needs | `TheProbeRecordsTotalMemory_ForTheUnifiedMemoryComparison` |
| 3 | the probe records the concurrency it is GIVEN, which is a different number from the CPU count | `TheProbeRecordsTheEffectiveConcurrency_NotTheConfiguredOne` |
| 4 | the probe records the version strings it is handed, and leaves null the ones it is not | `TheProbeRecordsTheVersionsItIsGiven_AndNullsItIsNotGiven` |

### What behaviour 3 actually asserts, because the name compresses it

The wrong implementation this test exists to catch is **conflating concurrency with core count** —
filling `MaxParallelism` from `Environment.ProcessorCount` because both are "how parallel is this box".
They are different facts and the corpus needs both: the CPU count describes the machine, the
concurrency describes the run.

The probe cannot see the configured value at all — that is the point of taking it as a parameter — so
assert it against a value that could not have come from anywhere else: call
`Probe(maxParallelism: 1, …)` and require `MaxParallelism == 1` **while** `CpuCount` reports the
machine's real core count, and assert the two are distinct fields with distinct values on any box with
more than one core. Say in a comment that a single-core CI box would make that particular
distinctness assertion vacuous, and assert the identity (`MaxParallelism` equals the argument) as the
load-bearing half — that one holds everywhere.

The *effective* concurrency, and where task 18 gets it from, is a run-time fact: `Scheduler`'s
constructor CLAMPS a requested parallelism greater than 1 down to 1 when no worktree provider was
wired (grep `ParallelismClampedNoProvider`), so the number a run actually uses is not always
`plan.Config.MaxParallelism`. That is the CALLER's problem and belongs to task 18. Your tests assert
only that the probe faithfully records what it is told.

### Behaviour 4 needs both halves

`skillVersion` is legitimately `null` when no skill is installed, so "the version it is not given stays
null" is a real behaviour, not a defensive nicety. Assert both directions in the one test: a value
passed in comes back on the record, and a `null` passed in comes back as `null` — never as an empty
string and never as a fabricated default.

### All four tests must be RED, and there are no exemptions

The stub throws `NotImplementedException` unconditionally, so **every honest test here fails**. Every
test must actually **call `RunEnvironmentProbe.Probe` and assert on the record it returns**. A test
that constructs a `RunEnvironment` itself and asserts something about the object it just built is
hollow: it passes against the throwing stub and this task's guardrail will name it.

**Do NOT implement `Probe`.** The tests MUST COMPILE and FAIL against the throwing stub — failing is
intentional; not compiling is a mistake to fix. `18-record-the-run-environment` fills it.

Nothing in this repo reads `Environment.MachineName`, `Environment.ProcessorCount`,
`Environment.OSVersion` or `GC.GetGCMemoryInfo()` today — a repo-wide grep returns zero hits — so there
is no house style for you to match here, and no existing helper to reuse. Do not go looking for one.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Journal/RunEnvironmentTests.cs` and
`src/Guardrails.Core/Journal/RunEnvironmentProbe.cs` (the stub file). After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths — including changes to
other production files (notably `JournalModel.cs`, which belongs to task 03), neighbouring test files,
or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a
compile error caused by a missing symbol in another file — for instance if `RunEnvironment` does not
carry a member you expected — do NOT edit that file: write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and
stop.
