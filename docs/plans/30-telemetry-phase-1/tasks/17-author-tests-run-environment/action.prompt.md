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

Author **three** files, and only these three: the stub, the Core tests for the probe, and one
Integration test that the record the probe produces actually **survives to `state/run.json`**.

**Why the third file exists.** The probe is one hop of three. Seven of this plan's thirteen new facts
cross `probe -> RunJournal -> run.json`, and the Core tests in section 2 stop at the first hop: they
prove the probe returns the right record, and would keep passing if nothing ever persisted it.
`18-record-the-run-environment`'s own prompt names the failure exactly — *"a stamp placed the other way
round would be silently lost"*, because `RunJournal.LoadOrCreate` is called at two independent sites on
a real run and the second overwrites a document written before the stamp. Nothing observes that today.
Section 3 is the observation.

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

### All four of THESE tests must be RED, and there are no exemptions

The stub throws `NotImplementedException` unconditionally, so **every honest test here fails**. Every
test must actually **call `RunEnvironmentProbe.Probe` and assert on the record it returns**. A test
that constructs a `RunEnvironment` itself and asserts something about the object it just built is
hollow: it passes against the throwing stub and this task's guardrail will name it.

**Do NOT implement `Probe`.** The tests MUST COMPILE and FAIL against the throwing stub — failing is
intentional; not compiling is a mistake to fix. `18-record-the-run-environment` fills it.

Nothing in this repo reads `Environment.MachineName`, `Environment.ProcessorCount`,
`Environment.OSVersion` or `GC.GetGCMemoryInfo()` today — a repo-wide grep returns zero hits — so there
is no house style for you to match here, and no existing helper to reuse. Do not go looking for one.

### 3. `tests/Guardrails.Integration.Tests/Journal/RunEnvironmentJournalTests.cs` — the round-trip test

Class **`RunEnvironmentJournalTests`**, `public sealed`, in namespace
`Guardrails.Integration.Tests.Journal` (the namespace its sibling
`tests/Guardrails.Integration.Tests/Journal/DeliveryRecordTests.cs` already uses), carrying
`[Trait("Category", "ModelEvidence")]`, with exactly ONE `[Fact]` under **exactly this name** — this
task's guardrail and task 18's both bind to it:

| behaviour | test method name (VERBATIM) |
|---|---|
| after a real run, the plan's `state/run.json` carries a non-null `environment.host` | `AfterARealRun_RunJsonCarriesANonNullEnvironmentHost` |

**Model it on `tests/Guardrails.Integration.Tests/RunEndTelemetryIngestTests.cs`** — note the path: that
file sits at the ROOT of the Integration project, not under `Commands/`. **Read it before you write
anything.** It already does every mechanical thing you need:

- builds a throwaway plan with `ScriptPlanBuilder` (`using var plan = new ScriptPlanBuilder().AddTask("01-a");`),
- drives a REAL run through `CommandFactory.BuildRootCommand(io)` — the actual composition root
  `Program.cs` builds, never a hand-built one — with `"run", plan.PlanDir, "--no-ui", "--no-log-server"`,
- and reads the journal back **off disk** afterwards with
  `JournalReader.Read(RunJournal.PathFor(plan.PlanDir))` (its private `RunId` helper is exactly that
  one line — copy the idiom, not the helper).

Corpus isolation is already in force and you must not opt in to it by hand:
`tests/Guardrails.Integration.Tests/TelemetryCorpusIsolation.cs` is a `[ModuleInitializer]` covering the
whole assembly, so a real run started here writes its telemetry to a per-process temp corpus and never
to the operator's `~/.guardrails/telemetry/`. Do not set `GUARDRAILS_TELEMETRY_CORPUS_ROOT` yourself and
do not disable collection.

**What the test asserts, and the order matters:**

1. the run exits `ExitCodes.Success` — a BASELINE, not the point (it is already true today);
2. **the load-bearing assertion:** the document read back from `state/run.json` has a **non-null
   `Environment`** whose **`Host` is non-null and non-empty**.

**Read it back from the FILE.** Never assert against a `RunJournal` instance the test is holding, a
`JournalDocument` it built, or a `RunEnvironment` it constructed — any of those would pass while
`state/run.json` on disk carried nothing, which is the precise failure this test exists to observe.
`JournalReader.Read` deserializes the file, so a value that never got serialized comes back null.

**Why it is RED today, and why that reason is different from the four above.** The Core tests are red
because `Probe` throws. This one is red because **nothing stamps the environment onto the journal at
all** — `18-record-the-run-environment` adds the recorder and the call site. So a real run completes
normally, `state/run.json` is written, and its `Environment` is null. Compiling is required: `Environment`
is a member `03-extend-the-journal-record-shape` already added to `JournalDocument`, so nothing here
names a type that does not exist.

**The hollow shape this one invites, named so you avoid it:** asserting only that the run exited zero,
or only that `state/run.json` exists. Both are already true on this tree, both read as coverage, and
both would let task 18 stamp the environment in the wrong order — the "silently lost" failure its own
prompt describes — without anything going red. The `Host` assertion is the entire value of the test.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Journal/RunEnvironmentTests.cs`,
`src/Guardrails.Core/Journal/RunEnvironmentProbe.cs` (the stub file) and
`tests/Guardrails.Integration.Tests/Journal/RunEnvironmentJournalTests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths — including changes to
other production files (notably `JournalModel.cs`, which belongs to task 03, and `RunJournal.cs` /
`RunCommand.cs`, which belong to task 18), neighbouring test files, or any `.csproj`. An out-of-scope
edit fails the task immediately and consumes a retry. If you hit a compile error caused by a missing
symbol in another file — for instance if `RunEnvironment` does not carry a member you expected — do NOT
edit that file: write `{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the
state-out path and stop.
