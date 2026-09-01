## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-tests-executed-hash": { "someKey": "someValue" } }`.
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

This task implements stage 1 of `docs/plans/32-executed-definition-hash.md`. **Read sections 4.1, 4.2,
5.1, 5.2, 5.5 and 5.8 in full.** Where this prompt and the plan disagree, the plan is authoritative and
you should say so in your summary.

The defect in one sentence: the definition hash stamped into the journal at settle is computed from the
bytes **on disk at settle**, not the bytes the attempt **executed**, so a mid-run `task.json` edit yields
a silent false green that no later resume can detect.

## Task

Create **`tests/Guardrails.Core.Tests/Journal/ExecutedDefinitionHashTests.cs`**.

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
- Class **`ExecutedDefinitionHashTests`** - **pinned; the guardrails filter on it**. `public sealed class`,
  `IDisposable` for its temp-dir fixture.

Four `[Fact]`s, with these **EXACT** method names:

| Pin | Method name | Behaviour |
|---|---|---|
| **P1** | `TheRecordedHash_IsThePreEditPin_WhenTaskJsonIsEditedMidRun_Serial` | Section 5.8's acceptance form, in **serial / shared-workspace** mode (write site **W1**, `AttemptJournaler.CompleteSucceededOrInvalidFragment`). Compute `hashBefore = TaskDefinitionHash.Compute(task)` after the plan loads; edit `tasks/<id>/task.json` on disk; drive the task to a successful settle; assert the journal's recorded `definitionHash` **equals `hashBefore`** AND **does not equal** a fresh `TaskDefinitionHash.Compute(task)` taken after the edit. **Both halves fail today** - that is the point. |
| **P14** | `TheRecordedHash_IsTheRunStartValue_WhenTaskJsonIsEditedBetweenAttempts` | Section 6.7's discriminator between a **load-time** pin and an **attempt-start** one. Run a task that FAILS its guardrail once, edit `tasks/<id>/task.json` **between attempt 1 and attempt 2**, let attempt 2 succeed, and assert the recorded hash still equals the **run-start** value. An attempt-start capture records the post-edit hash and fails this; a load-time pin passes. Section 5.7 rejects candidate (2) in prose and nothing else pins the rejection. |
| **P5** | `AnUneditedRun_RecordsAHashIdenticalToAPostRunRecompute` | Section 5.5's no-op property: with **no** mid-run edit, the recorded hash equals `TaskDefinitionHash.Compute(task)` computed **after** the run. This is the pin that proves no migration wave and no drift wave is owed. **DECLARED EXEMPTION** - see below. |
| **P8** | `TaskDefinitionHashCompute_OutputHasNotMoved_OnAPinnedFixtureFolder` | Section 5.8's byte-pin. Build a fixed definition surface (a `task.json`, one action file, one `guardrails/` script) **in a temp directory from literal strings inside this test file**, call `TaskDefinitionHash.Compute` over the loaded node, and assert the result equals a **hard-coded `sha256:...` literal**. This plan changes *when* the hash is computed, never *what* it is computed over; a later task that "simplifies" the file set or the framing would trigger a repo-wide drift wave, and this is the tripwire. **DECLARED EXEMPTION.** |

### The constraint that makes this stage possible: NAME NO NEW API MEMBER

Every assertion above is on an **observable artifact** - the journal's recorded hash, the output of the
already-public `TaskDefinitionHash.Compute`. **None of them may name a member this plan has not written
yet** - not `TaskNode.DefinitionHashAtLoad`, not `DefinitionFilesAtLoad`, not
`definitionHashAtSettle`, not `RunReport.ExecutedDefinitionDivergence`.

That is a deliberate constraint, not an accident (section 15 row 1): it is the whole reason these tests
**compile against today's assemblies and fail for the right reason**, with no stub stage in front of them,
and it is what lets stages 3, 4 and 5 legitimately carry no `tests/**` path. Guardrail `01-build-passes`
enforces it mechanically - a name that does not exist is a compile error, and a compile error here is
**your** bug to fix by rewriting the assertion, never by widening the change into `src/**` (which is
outside your `writeScope` and fails the task immediately).

### Two DECLARED EXEMPTIONS, and why they are not dropped rows

P5 and P8 assert properties that are **true today and must stay true**. With the defect present and the
folder unedited, the settle-time recompute already equals a post-run recompute (P5), and
`TaskDefinitionHash.Compute`'s output has not moved (P8). So a **CORRECT** test is **GREEN** on today's
tree, and demanding red would demand a correct implementation fail.

The red census in guardrail `02` therefore asserts those two rows **executed** (present in the runner's
result file, not `[Skip]`ped) rather than **failed**. They stay IN the manifest: a dropped row and an
oversight look identical from the outside. **Write them; do not skip them.**

P1 and P14 must **FAIL** against today's tree, and they will: today the settle stamps the post-edit
disk hash.

**Two of four exempt is a high ratio and it is honest here** - this file carries two defect pins and two
regression pins by design (section 5.8 lists them as separate kinds). If you find yourself wanting a
third exemption, that is the signal you have written a forward census wearing the red one's name; escalate
with `needsHuman` instead.

### Serial mode is not a detail

P1 is asserted **in serial / shared-workspace mode** here because that is the mode `AttemptJournaler`'s
settle (**W1**, `AttemptJournaler.cs:91`) governs. The **worktree**-mode halves (W2/W3) are stage 7's, on
a real git segment - do not try to cover them from here (section 8: a design that proved this only in
serial mode would have proved it in the mode plan 28 did not use).

### Sequencing the mid-run edit

The edit must land **after** the plan loads and **before** the task settles. The shipped mechanism for
this is `tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs`'s `CreateMidRunEditPlan` - a
two-task plan whose first task's action WRITES into the second task's folder, so the edit is sequenced by
the DAG rather than by a timer. **Read it before inventing anything.** At Core level you may instead drive
the settle path directly with a plan whose in-memory node and on-disk bytes are constructed to differ
(section 8 names that fallback in advance so you do not improvise a weaker assertion).

**Do NOT make the edit conditional, retimed, or removed to reach green.** The edit IS the fixture
(section 11).

### Where the pieces live

- `TaskDefinitionHash.Compute(TaskNode)` - `src/Guardrails.Core/Journal/TaskDefinitionHash.cs`, **public**.
- `TaskDefinitionFiles.Enumerate(TaskNode)` - `src/Guardrails.Core/Journal/TaskDefinitionFiles.cs`,
  **`internal`**, namespace **`Guardrails.Core.Journal`** (not `Loading`).
  `Guardrails.Core.csproj` carries `InternalsVisibleTo` for both test assemblies, so a `using` is all it needs.
- The recorded value: `TaskJournalEntry.DefinitionHash` (`src/Guardrails.Core/Journal/JournalModel.cs:374`).
  Note the type is **`TaskJournalEntry`**, not `TaskEntry`. `RunJournal` exposes a
  `RecordedDefinitionHash(taskId)` helper - `PlanEditedDuringRunTests` uses it.
- Existing suites worth reading first, because they already do most of this setup:
  `tests/Guardrails.Core.Tests/RunJournalDefinitionHashTests.cs` and
  `tests/Guardrails.Core.Tests/TaskDefinitionHashTests.cs`. **Do not edit either** - they are outside your
  `writeScope`, and stage 3's guardrails run them to prove nothing moved.

### Do NOT

- Do NOT weaken P1 into "the hash is non-null" or "the hash changed". The assertion is an **equality
  against a value captured before the edit**; anything weaker passes with the defect intact.
- Do NOT compute P8's expected value by calling the function under test at assertion time - that is an
  echo judge, green by construction. Compute it once, read it off the failing assertion, and write the
  literal into the file.
- Do NOT touch any file outside the one named below.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Journal/ExecutedDefinitionHashTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path - including production files,
neighbouring test files, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file - the
missing symbol is almost certainly a member this plan has not written yet, so **rewrite the assertion to
name only what exists today**; if you genuinely cannot, write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
