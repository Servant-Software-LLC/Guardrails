## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-author-tests-wave-executed-hash": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements stage 8 of `docs/plans/32-executed-definition-hash.md`. **Read sections 5.4 and
5.8's P7 in full**, plus section 3's row **B** (*"Not optional"*). Where this prompt and the plan
disagree, the plan is authoritative and you should say so in your summary.

Milestone B exists because §7.2/§14.5 already assert that *"the wave hash changes **iff** a constituent
task hash changes - the levels cannot drift apart."* Shipping milestone A alone makes that statement
**false**, and worse, makes the disagreement **harder** to notice than it is today, because today both
levels are consistently wrong.

## Task

Create **`tests/Guardrails.Core.Tests/Journal/WaveExecutedDefinitionHashTests.cs`**.

- Namespace **`Guardrails.Core.Tests`** - **FLAT, not `Guardrails.Core.Tests.Journal`, even though the
  file lives in the `Journal/` folder.** This is not a style preference and it is not negotiable:
  declaring `namespace Guardrails.Core.Tests.Journal` **anywhere in this assembly** introduces a
  `Journal` member under `Guardrails.Core.Tests`, which then **wins the enclosing-namespace walk** over
  the production `Guardrails.Core.Journal` for every unqualified `Journal.X` reference in the project.
  **Three files break with CS0234, all outside your `writeScope`**: `OverwatchNoVerdictTests.cs:355`
  (`Journal.TaskStatus.Running`), the shared helper `WavePlanBuilder.cs`, and
  `Journal/JudgeSpendRecordingTests.cs` itself. **Read that last one's header comment at lines 9-14
  before you write a line** - it is the sibling in the same folder, it declares the flat namespace, and
  it documents this exact hazard verbatim, naming `OverwatchNoVerdictTests.cs` and stating the fix is out
  of write scope. Folder and namespace are deliberately decoupled here. The guardrail's `--filter` is
  `FullyQualifiedName~Guardrails.Core.Tests.<ThisClass>`, which matches the flat form.
- Class **`WaveExecutedDefinitionHashTests`** - **pinned; the guardrails filter on it**. `public sealed
  class`, `IDisposable` for its temp-dir fixture.

Two `[Fact]`s, with these **EXACT** method names. **Both must FAIL on the current tree**, and both do:

| Pin | Method name | Behaviour |
|---|---|---|
| **P7a** | `TheWaveHashChanges_IffAConstituentTaskHashChanges` | The **task fold**. Edit one constituent task's `task.json` mid-run and assert the wave's recorded hash moves *iff* that task's recorded hash moves. Stages 3-5 have pinned the task level while the wave fold still recomputes from disk, so today the task's stamped hash describes the **pre-edit** bytes and the wave's describes the **post-edit** ones - the two levels disagree about the same tasks in the same journal. |
| **P7b** | `TheStampedWaveHash_IsUnmoved_WhenAWaveGateFileIsEditedMidRun` | The **wave-gate fold**. An implementation that folds `task.DefinitionHashAtLoad` for the task half but still walks the wave's `guardrails/**` and `preflights/**` from **current disk** passes P7a **exactly**, while leaving the wave-level half of the defect intact. Edit a wave **gate** file mid-run and assert the stamped wave hash is **unmoved**. |

### The echo-judge rule, and it is the whole difficulty of this stage

Section 5.8:

> **Neither leg may compute its expected value by calling the production pinned function** - that is an
> echo-judge, green by construction. The test reconstructs the fold independently, separators and labels
> included. That duplicates production logic, which is its own hazard; it is named as a deliberate trade
> rather than discovered by the implementer.

So: **reconstruct the fold in the test**, from values the test already holds - the journal's recorded
per-task hashes and the same `HashText` primitive the production fold uses - matching
`WaveDefinitionHash`'s framing exactly: each constituent task's entry in **wave-relative task-id order**,
then the wave's `guardrails/**`, then `preflights/**`, then the optional `brief.md`, with the same labels
and the same separators. Read `src/Guardrails.Core/Journal/WaveDefinitionHash.cs` and mirror it.

**Do NOT call `WaveDefinitionHash.Compute(wave)` as the expected value either.** That is the *disk* form,
and on an edited fixture it returns the post-edit value - so P7b would be **green today** and red after
stage 9, i.e. inverted. Guardrail 02's census catches that, and its message says so.

### NAME NO API MEMBER THIS PLAN HAS NOT WRITTEN YET

`WaveNode.DefinitionHashAtLoad` and the pinned fold function are **stage 9's** deliverables and do not
exist. Assert on the **journal's** recorded wave and task hashes, which do. `src/**` is outside your
`writeScope`, so a CS0117 here is your assertion to rewrite, never a member to add. Guardrail 01 enforces
this mechanically.

`TaskDefinitionFiles` and `HashText` are **`internal`**, namespace **`Guardrails.Core.Journal`**;
`Guardrails.Core.csproj` carries `InternalsVisibleTo` for both test assemblies, so a `using` is all they
need. **Do not modify either** - they are outside your scope and section 11 forbids touching them at all.

### Do NOT

- Do NOT weaken P7a into "the wave hash changed". Today it changes; that is the defect.
- Do NOT make the mid-run edit conditional, retimed, or removed to reach green. The edit IS the fixture.
- Do NOT touch any file outside the one named below.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Journal/WaveExecutedDefinitionHashTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path - including `WaveDefinitionHash.cs`,
`WaveNode.cs`, other test files, and the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that
file - rewrite the assertion against what exists today, or write `{"needsHuman": "<what is missing>"}` to
the state-out path and stop.
