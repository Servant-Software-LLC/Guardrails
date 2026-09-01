## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `18-record-the-run-environment`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "18-record-the-run-environment": { "someKey": "someValue" } }`.
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

This task implements the **machine and concurrency profile** item of section 3.4 of
`docs/plans/30-telemetry-phase-1.md`: *"machine and concurrency profile including unified memory,
harness and skill versions"*. Read section 3.4; where this prompt and the plan disagree, the plan is
authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

The reason the memory figure is on the list is recorded in section 3.4 and was **confirmed by the
maintainer on 2026-09-01**: the 64GB Mac Studio is a **tighter** box than the 128GB MacBook available
today, so the same model name runs at a different quantization on each and **must not be pooled as one
sample**. Without a memory figure in the record, two rows naming the same local model are
indistinguishable in the corpus while being two different models.

## Task

Three changes, in three files, in this order.

### 1. Fill `RunEnvironmentProbe.Probe` — `src/Guardrails.Core/Journal/RunEnvironmentProbe.cs`

`17-author-tests-run-environment` wrote the stub with the pinned signature:

```csharp
public static RunEnvironment Probe(int maxParallelism, string? harnessVersion, string? skillVersion)
```

Fill it. **Do not change the signature** — a widened one would let the probe reach for values it is
supposed to be told, and the version parameters exist for a hard reason given in section 3 below.

This is **greenfield**: nothing in this repo reads `Environment.MachineName`,
`Environment.ProcessorCount`, `Environment.OSVersion` or `GC.GetGCMemoryInfo()` today — a repo-wide
grep returns zero hits — so there is no house style to match and no existing helper to reuse. Do not go
looking for one.

Notes on the members (read `RunEnvironment` in `src/Guardrails.Core/Journal/JournalModel.cs` for the
declaration `03-extend-the-journal-record-shape` actually wrote — that is authoritative over this list):

- **`TotalMemoryBytes`** — on Apple silicon the unified pool is what
  `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` reports, which is why the member is named this way.
- **`MaxParallelism`** — record the argument you were handed. **Do not fill it from
  `Environment.ProcessorCount`.** The CPU count describes the machine; the concurrency describes the
  run. They are different facts and the corpus needs both, and a pinned test asserts they are distinct.
- **`HarnessVersion` / `SkillVersion`** — record what you are handed, and record `null` when you are
  handed `null`. Never substitute an empty string and never fabricate a default: `null` means "no skill
  installed", which is a true and useful answer.

The probe must not throw. It is called once at run start, on a path that has no business failing a run
because a machine fact was unavailable; prefer an absent member to an exception.

### 2. Give `RunJournal` a recorder — `src/Guardrails.Core/Journal/RunJournal.cs`

`JournalDocument.Environment` (added by task 03) is a member of the in-memory document, and **the only
thing that persists that document is `RunJournal.Persist`**, which every one of `RunJournal`'s own
mutators calls. There is no existing mutator that can carry a `RunEnvironment`, so without this step the
probe would produce a record with no way to reach `state/run.json`.

Add ONE recorder and nothing else. **Follow the shipped `RecordDelivery` / `RecordHalt` shape** — grep
for `RecordDelivery` and read it in full before writing. Its comment block is the thing to read: it
documents, in detail, why a document-level record written from the CLI must **RE-READ FROM DISK FIRST**
rather than serializing this instance's possibly-stale in-memory document. Getting that wrong reverted
26 integration tests to `pending` on the first cut of that method.

Your call site is at run START, where this instance's document IS current — but write it the way its
siblings are written anyway. The next person to move the call site is not going to re-derive the
argument.

**This is the only change you make to this file.** TWO tasks edit `RunJournal.cs` before this one, and
this task now `dependsOn` both so that all three writers are strictly ordered rather than concurrent:
`06-journal-the-bucket-serial` widened three recorders in it, and
`16-carry-phase1-facts-through-the-worktree-settle` reworked the explicit-interface forwarder when
`ISchedulerJournal.RecordSettleWithAttempt` grew its optional `bucket` parameter. **Read what both did
before you write a line.** You are adding a recorder on top of a signature that has stopped moving — do
not undo either change, and do not tidy anything.

### 3. Call it once, where the run journal is created — `src/Guardrails.Cli/Commands/RunCommand.cs`

**Authoring-time state — VERIFY IT.** Grep for `RunJournal.LoadOrCreate`; do not trust a line number.

`RunJournal.LoadOrCreate` is called at **two independent sites on a real run**, each constructing its
own `RunJournal` over the same `run.json`:

1. `RunCommand` (grep `RunJournal journal = RunJournal.LoadOrCreate(probe.Plan);` — the run's id is
   resolved from it on the next line), and
2. `SchedulerFactory.CreateExecutor` in `src/Guardrails.Core/Execution/SchedulerFactory.cs`, reached
   later from `RunCommand` when it builds the scheduler.

**The ordering is load-bearing.** `RunCommand`'s load happens FIRST; the scheduler's happens later,
when it builds the executor. So stamping the environment from `RunCommand` immediately after its own
load means the scheduler's later load reads a file that already carries it. **A stamp placed the other
way round would be silently lost** — the second load would read a document written before the stamp and
go on from there.

**The effective concurrency, not the configured one.** `plan.Config.MaxParallelism` is what was ASKED
for. The number a run actually uses is clamped: `Scheduler`'s constructor demotes a requested
parallelism greater than 1 to 1 when no worktree provider was wired (grep
`ParallelismClampedNoProvider`), and `SchedulerFactory` wires a provider on exactly the condition
`SchedulerFactory.WouldUseWorktreeMode(plan)` reports — it is `public static`, and `RunCommand` already
calls it (grep `WouldUseWorktreeMode`). Derive the effective number from that same predicate so the two
can never disagree, rather than re-spelling the condition.

**The two version strings.** The harness version is `GuardrailsVersion.Current`
(`src/Guardrails.Cli/GuardrailsVersion.cs`). The skill version is read by
`SkillFrontmatter.ReadGuardrailsVersion` (`src/Guardrails.Core/Prompts/SkillFrontmatter.cs`) from the
TEXT of an installed `SKILL.md` — note it takes the file's content, not a path; `SkillVersionReport` in
`Guardrails.Cli` is where the installed file is located, and grep for `ReadGuardrailsVersion` to see the
one shipped call. **A null skill version is correct and expected** when no skill is installed; do not
invent a fallback and do not fail the run over it.

This is exactly why `Probe` takes the versions as parameters instead of reading them: the harness
version lives in `Guardrails.Cli`, and `Guardrails.Core` cannot reference it — `Guardrails.Cli` depends
on `Guardrails.Core`, not the other way round, and there is no `InternalsVisibleTo` that would change
that. The probe stays in Core and the CLI supplies what only the CLI knows.

**Once per run.** The record describes the machine and the run, not the attempt. Do not stamp it per
task, per attempt or per wave.

**The ordering above is OBSERVED, not merely described.**
`17-author-tests-run-environment` also authored
`tests/Guardrails.Integration.Tests/Journal/RunEnvironmentJournalTests.cs`, one `[Fact]` named
`AfterARealRun_RunJsonCarriesANonNullEnvironmentHost`. It drives a real `guardrails run` through
`CommandFactory.BuildRootCommand` and then reads `state/run.json` **back off disk** with
`JournalReader.Read(RunJournal.PathFor(planDir))`, requiring a non-null `Environment` whose `Host` is
non-empty. That is exactly the "silently lost" failure above: get the stamp on the wrong side of the
second `RunJournal.LoadOrCreate` and the Core probe tests still pass while this one goes red. Your
guardrail runs it, in its own project. **The file is outside your `writeScope`** — read it, do not
edit it.

## Do not do these

- **Do NOT edit the tests.** BOTH
  `tests/Guardrails.Core.Tests/Journal/RunEnvironmentTests.cs` and
  `tests/Guardrails.Integration.Tests/Journal/RunEnvironmentJournalTests.cs` are outside this task's
  writeScope; an edit to either fails the write-scope check and burns a retry. If a test is
  genuinely wrong, write `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the
  state-out path.
- **Do NOT change `Probe`'s signature.** A pinned test asserts the concurrency it records is the one it
  was given, and the parameter list is what makes reading the versions from Core impossible rather than
  merely discouraged.
- **Do NOT add a second stamp site** "for safety". Two writers of one document-level field is how the
  two disagree.

## Scope boundary (harness-enforced)

Write only to `src/Guardrails.Core/Journal/RunEnvironmentProbe.cs`,
`src/Guardrails.Core/Journal/RunJournal.cs` and `src/Guardrails.Cli/Commands/RunCommand.cs`. After this
task completes, the harness runs a `git diff` check and rejects any edit outside those paths —
including changes to other production files, the authored test file, or the `.csproj`. An out-of-scope
edit fails the task immediately and consumes a retry.
