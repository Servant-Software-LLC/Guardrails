## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-author-tests-plan-edit-watch": { "someKey": "someValue" } }`.
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

This task implements stage 7 of `docs/plans/31-unattended-run-hardening.md`. **READ SECTIONS 5.1
THROUGH 5.5 IN FULL** - section 5.3 in particular is a CORRECTION to an earlier revision, and a pin
written from the earlier framing tests an unreachable state. Where this prompt and the plan disagree,
the plan is authoritative and you should say so in your summary.

Read: **sections 5.1, 5.2, 5.3, 5.4, 5.5**, and **section 8's `#545 part 3` bullets**.

## What stage 6 left you

`src/Guardrails.Core/Execution/LivePlanEditWatch.cs` declares the section 5.2 surface with `Poll()` and
`Rebaseline()` throwing `NotImplementedException`, and a constructor that does NOT throw - so you can
construct the watch and get a behavioural red rather than a construction failure. **This describes the
state at plan-authoring time, before stage 6 had actually run - verify it before assuming this shape.**
Find the type by symbol; do not rely on any line number.

## The pin that must NOT be written from the earlier framing

Section 5.3's whole point: **the overwatcher is NOT a mid-run definition writer.** `Overwatch.cs`
extracts only the two `Allowlist` levers and returns an **in-memory** decision; `FileEdit` /
`TaskFieldEdit` / `Denylist` are parsed and classified but have **no apply path in v1**
(`OverwatchFixClassifier.cs` says so in as many words - grep it for "v1-inert"). An earlier revision of
the plan carried a negative pin written against an overwatcher fix. **It tested an unreachable state
and would have passed with the whole feature absent**, which is precisely the archetype this plan
exists to hunt. That pin is deleted. **Do not resurrect it.**

The REAL mid-run definition writer is **JIT wave breakdown**: `WaveBreakdownInvoker.cs` runs a Claude
subprocess with `workingDirectory: plan.PlanDirectory`, `PermissionMode = "acceptEdits"`, `AllowedTools`
including `Write`/`Edit`/`Bash`, and no containment hook of any kind. That is what pin P2 exercises.

## Task

### File 1 - `tests/Guardrails.Core.Tests/Execution/LivePlanEditWatchTests.cs`

Namespace **`Guardrails.Core.Tests.Execution`** (mirror the sibling `Execution/OverwatchClassifierTests.cs`).
Class **`LivePlanEditWatchTests`** - pinned; the guardrails filter on it. `public sealed class`,
`IDisposable` for its temp-dir fixture.

The unit-level contract of section 5.2, one `[Fact]` each, with these EXACT method names:

| # | Method name | Behaviour |
|---|---|---|
| U1 | `Poll_WithNothingChanged_ReturnsEmpty` | Construct over a plan folder, `Poll()` once, `Poll()` again with nothing touched - the second returns empty. |
| U2 | `Poll_AfterAGuardrailScriptIsModified_ReportsThatTaskAndThatFile` | Modify a `guardrails/*.ps1` after the first `Poll()` - the next `Poll()` returns one `PlanEdit` for that task, whose `Files` names that file with `PlanEditKind.Modified`. |
| U3 | `Poll_ReBaselines_SoASecondPollAfterOneEditIsEmpty` | The re-baselining half of `Poll()`'s contract: report once, then stay silent. |
| U4 | `Rebaseline_WithNoIds_SilencesTheWholePlan` | `Rebaseline()` with no arguments after an edit - the next `Poll()` is empty. This is the plan-wide form section 5.3 requires after each of the five harness writers. |
| U5 | `Rebaseline_WithAnUnknownTaskId_IsANoOp` | An unknown id neither throws nor disturbs the baseline. |
| U6 | `Poll_WithAnUnreadableFile_DoesNotThrow` | Section 5.2: "Never throws: an unreadable file is skipped." |
| U7 | `Poll_IgnoresEditorArtifacts_DsStoreThumbsDbSwpOrigRej` | The section 5.2 ignore list, applied in the WATCH and not in `HashText` - changing `HashText` would move every recorded definition hash and turn the next resume of every affected plan into a drift halt. |
| U8 | `Poll_IgnoresLogsAndState_TheHarnessOwnWritesUnderThePlanFolder` | `logs/` and `state/` are not in `TaskDefinitionFiles.Enumerate`, which is why the harness's own constant writes into the plan folder cannot trigger the watch. |

`TaskDefinitionFiles` is **`internal`**, in namespace **`Guardrails.Core.Journal`** (not `Loading`) -
`Guardrails.Core.csproj` carries `InternalsVisibleTo` for both test assemblies, so it is reachable with
a `using`.

All eight must FAIL against the stubs, and they will: `Poll()` and `Rebaseline()` throw.

### File 2 - `tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs`

Namespace **`Guardrails.Integration.Tests`** (flat - every file in that project uses it, including
those in subfolders). Class **`PlanEditedDuringRunTests`** - pinned. `public sealed class`.

| # | Method name | Behaviour |
|---|---|---|
| P1 | `AGuardrailEditedMidRun_EmitsExactlyOneObservedPlanEditDecision` | **Section 5.5 pin 1.** A run in which a task's `guardrails/*.ps1` is modified after the run starts and before that task settles emits **exactly one** `DecisionRecorded` call and **exactly one** `decisions[]` entry with `boundary: "plan-edit"`, `decision: "observed"`, naming that task and that file. |
| P2 | `AJitWaveBreakdownFollowedByRevert_EmitsZeroPlanEditEntries` | **Section 5.5 pin 2, and it must test a REACHABLE state.** A waved fixture whose wave-2 breakdown authors task folders and whose `BreakdownInventory.Revert` then rejects them emits **zero** `plan-edit` entries. **DECLARED EXEMPTION** - see below. |
| P3 | `ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero` | **Section 5.5 pin 3.** A run carrying a `plan-edit` observation **and nothing else** still fast-forwards on success and still exits **0**, not 5. Assert on the **exit code and the delivery record**, not on the `SuppressesDelivery` predicate. CREATE the observation, so this is red on the stubs. |
| P4 | `AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStillChanges` | **Section 5.5 pin 4.** Both halves in one test: the watch is silent, AND the same run's recorded `TaskDefinitionHash` still **changes** - proving the watch is quieter than the hash **by design rather than by accident**. **DECLARED EXEMPTION.** |
| P5 | `TheRenderedText_CarriesAllThreeSection51Consequences` | **Section 8's last `#545` bullet.** The rendered text states all three: what the edit REACHES (prompts and guardrail scripts are re-read per attempt, so an edit applies from the next attempt onward); what it does NOT reach (`task.json` and the DAG were loaded at run start); and that the task will record the POST-edit hash at settle, so a later resume will not flag it as drift. **Assert on the string** - this is the one place a half-true message actively misleads, and "your edit was ignored" is false. |

**P2 and P4 are DECLARED EXEMPTIONS.** Both assert an ABSENCE that is trivially true today: with the
watch inert, nothing emits a `plan-edit` entry at all, so a CORRECT test is GREEN on the stub tree and
demanding red would demand a correct implementation fail. Their job is to stay green after stage 9 (the wiring) -
P2 because the harness's own writes must never fire the watch, P4 because the watch must stay strictly
quieter than the hash. The census asserts they **executed** (present, not `[Skip]`ped). Write them; do
not skip them.

P1, P3 and P5 must FAIL against the stubs.

### Windows-Git test portability (#116)

`PlanEditedDuringRunTests` drives real runs over real git repositories, and **P2 needs a WAVED plan
that actually reaches a JIT breakdown checkpoint** - a plain repo fixture cannot produce one. Mirror
the siblings that already build exactly that: **`tests/Guardrails.Integration.Tests/WaveBreakdownRunTests.cs`**
and **`tests/Guardrails.Integration.Tests/WaveJitCheckpointRunTests.cs`**. They are the fixtures to copy
for P2. For the plain-repo mechanics the other pins need, `tests/Guardrails.Integration.Tests/RetrySalvageTests.cs`
declares the shared `HostRepoCleanlinessGuard` fixture at the foot of that file; reuse it via
`IClassFixture<HostRepoCleanlinessGuard>` rather than inventing a new one, and do NOT author a new
shared fixture file (it would be outside your `writeScope`). Whatever helper you write inline must:

- **strip read-only attributes before `Directory.Delete(recursive)`** - Git marks loose objects under
  `.git/objects` read-only on Windows, and the delete throws `UnauthorizedAccessException`, not
  `IOException`;
- **recreate a directory that `git rm`/`git mv` pruned** before writing into it;
- **roll back with `git reset --hard <preHead>`, never `git merge --abort`** (rc=128 on a dirtied
  tracked path);
- **set `core.autocrlf=false`** so fixture content hashes are deterministic across platforms.

### Do NOT

- Do NOT touch `src/**`. `LivePlanEditWatch` is stage 6's and stage 8's; `Scheduler.cs`,
  `DecisionEntry.cs`, `RunReport.cs` and `RunCommand.cs` are stage 9's.
- Do NOT write a negative pin against an overwatcher fix. Section 5.3 shows it cannot edit a definition
  in v1, so that pin would pass with the feature entirely absent.
- Do NOT assert P1 by counting `decisions[]` entries of ANY boundary - a run produces other decisions.
  Filter to `boundary == "plan-edit"` and assert exactly one.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Execution/LivePlanEditWatchTests.cs` and
`tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including production files,
neighbouring test files, and the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file - write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
