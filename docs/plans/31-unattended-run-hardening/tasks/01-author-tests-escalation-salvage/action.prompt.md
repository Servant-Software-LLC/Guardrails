## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-tests-escalation-salvage": { "someKey": "someValue" } }`.
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

This task implements stage 1 of `docs/plans/31-unattended-run-hardening.md`. READ THE SECTIONS NAMED
BELOW before you start - the plan carries the reasoning, the rejected alternatives, and the exact
file:line evidence. Where this prompt and the plan disagree, the plan is authoritative and you should
say so in your summary.

Read: **plan sections 3.2, 3.3, 3.4, 3.5, 7 and 8 (the `#554` bullets)**.

## The one constraint that shapes everything here

**Every assertion must be on an OBSERVABLE ARTIFACT - a file on disk, a git ref, or a composed
string - and must name NO new API member.** Specifically: do NOT write `SalvageFraming`, do NOT write
`PriorAttemptRef.SalvagePatchPath`, do NOT write `PriorAttemptRef.SalvageRefName`, and do NOT add a
`restrictToScope` argument anywhere. Those are stage 2 and stage 3's deliverables; if these tests
named them they would not COMPILE today, and the whole point of this stage is that they compile
against today's assemblies and fail for the RIGHT reason (plan section 7). A guardrail enforces this with a
fail-on-present scan over comment- and string-literal-stripped source, anchored on a USE (a member
access or an enum-member access), so naming one in a comment is fine and calling one is not.

Consequence you must design around: to observe the forward carry you drive
`DependencyContextBuilder.BuildPriorAttempts` (which already exists) over a log directory you laid
down by hand containing a `prior-attempt.patch`, then compose with `PromptComposer.ComposeAction` and
assert on the composed BYTES. You never construct a `PriorAttemptRef` carrying a patch yourself.

## Task

### File 1 - `tests/Guardrails.Core.Tests/Execution/EscalationSalvageTests.cs`

Namespace **`Guardrails.Core.Tests.Execution`** (mirror the sibling `Execution/OverwatchClassifierTests.cs`;
note the `Loading/` folder in this project uses a flat namespace, `Execution/` does not).
Class **`EscalationSalvageTests`** - pinned, the guardrails filter on it. `public sealed class`.

Encode these four behaviours, one `[Fact]` each, with these EXACT method names:

| # | Method name | Behaviour |
|---|---|---|
| C1 | `PriorAttemptWithPatch_ComposedPromptCarriesSizeRoutedRecoveryChoice` | A prior attempt whose log dir contains a non-empty `prior-attempt.patch` composes a prompt carrying BOTH halves of the size-routed choice: read `prior-attempt.patch` for a small edit, and `git show "<ref>:<path>"` for a new file. Plan section 3.5 clarification 2: one more path bullet does NOT satisfy this - assert the routing text. |
| C2 | `PriorAttemptWithPatch_ComposedPromptCarriesTheWriteScopeCaveat` | The same composed prompt states that salvaged files remain subject to the task's `writeScope`. |
| C3 | `PriorAttemptWithPatch_ComposedPromptNamesTheDerivedSalvageRef` | The same composed prompt names `refs/guardrails/<taskId>/attempt-<N>` for the attempt that left the patch - derived, not journalled (plan section 3.3, "Why `PriorAttemptRef` and not a new journal field"). |
| C4 | `PriorAttemptWithoutPatch_ComposedPromptCarriesNoRecoveryBlock` | **DECLARED EXEMPTION** - see below. A prior attempt whose log dir has NO `prior-attempt.patch` composes a prompt with NO recovery-routing block. The empty-diff silence rule: offering "recover your work" for an absent patch is worse than silence (plan section 3.4, section 11). |

**C4 is a DECLARED EXEMPTION and the census knows it.** A CORRECT implementation leaves C4 GREEN on
today's tree, because today there is no recovery block at all - so demanding it be red would demand a
correct implementation fail. The census asserts C4 **executed** (present, not `[Skip]`ped) rather than
failed. Write it anyway and do not skip it: an undeclared omission is indistinguishable from an
oversight.

C1-C3 must FAIL against today's code. They will: `PromptComposer.AppendPreviousAttempt` renders priors
as a flat bullet list of log paths whose only instruction is "Read the transcript... and the feedback".

### File 2 - `tests/Guardrails.Integration.Tests/EscalationSalvageTests.cs`

Namespace **`Guardrails.Integration.Tests`** (flat - every file in that project uses it, including the
ones in subfolders). Class **`EscalationSalvageTests`** - pinned. `public sealed class`.

**These pins need a REAL git segment.** `IsRealGitSegment(worktree)` is false with the fake worktree
provider (`TaskBase = "0000..."`), which makes the whole salvage path a no-op - so a test written
against the fake would pass with the feature entirely absent (plan section 7). Build a real temp git repo and
run a real segment.

Encode these eight behaviours, one test each, with these EXACT method names:

| # | Method name | Behaviour |
|---|---|---|
| I1 | `NeedsHumanAfterWritingFiles_LeavesANonEmptyPriorAttemptPatch` | An action that emits `needsHuman` AFTER writing an in-scope file leaves a non-empty `prior-attempt.patch` in THAT attempt's log dir. |
| I2 | `NeedsHumanAfterWritingFiles_LeavesASalvageRefForTheAttempt` | ...and a git ref at `refs/guardrails/<taskId>/attempt-<N>`. |
| I3 | `NeedsHumanWithAnOutOfScopeWrite_ThatWriteIsAbsentFromThePatchAndTheRefTree` | An escalating attempt that writes BOTH an in-scope and an out-of-scope file leaves a patch containing the first and NOT the second, and the ref's tree agrees. Asserted on the patch BYTES and on `git ls-tree` of the ref (plan section 3.4 divergence 3, section 3.5 clarification 3). |
| I4 | `NeedsHumanOnTheFinalAttempt_StillPreserves` | A `needsHuman` on a FINAL attempt still leaves a patch and a ref. This is the pin that catches an implementation that copied `StashIfRollingBack` verbatim - `WorktreeWillReset` is false on a final attempt, yet a final escalating attempt is precisely the one whose work a human is about to build on (plan section 3.4 divergence 1). |
| I5 | `NeedsHumanEscalation_ContextNamesTheRefAndThePatch` | The escalation `Context` a human or a firstmate reads at the halt names the salvage ref and the patch path. |
| I6 | `NeedsHumanHavingWrittenNothingInScope_LeavesNoPatchNoRefAndNoSalvageSection` | **DECLARED EXEMPTION.** An attempt that escalates having written nothing in scope leaves no patch, no ref, and no salvage section. The empty-diff guard is unchanged, and the scope filter can CREATE this case (plan section 3.4). |
| I7 | `SerialMode_EscalationPathPreservesNothing` | **DECLARED EXEMPTION.** In serial mode `IsRealGitSegment` is false, so nothing is preserved and nothing is advertised - correct, because the files are still on disk. |
| I8 | `RepeatEscalations_SalvageRefsAreCappedButNotEmpty` | Across more repeat escalations of one task than the retention cap, that task has AT LEAST ONE and AT MOST the cap's worth of `refs/guardrails/<taskId>/attempt-*` refs. Assert on `git for-each-ref`; do NOT name the constant `SalvageRefRetentionPerTask` - read the cap from the observed behaviour and pin the "at least one" half, which is what makes this red today. |
| I9 | `NeedsHumanEscalation_SalvageTextSaysOrphanedAndNeverClaimsARollback` | The salvage text the escalating attempt emits says the tree that produced the work is ORPHANED, and does NOT claim the work was rolled back and saved. Plan section 3.4 divergence 2: on this path nothing was rolled back (section 3.2 - the attempt loop returns terminally before the reset), so the Retry framing's wording would be actively false to the human deciding how to unblock. Assert BOTH halves on the emitted text. |

**I6 and I7 are DECLARED EXEMPTIONS** for the same structural reason as C4: today NOTHING is preserved
on the escalation path, so both are green against current code when they are correct. The census
asserts they **executed**. Write them; do not skip them.

I1-I5, I8 and I9 must FAIL against today's code, and they will: `TaskExecutor.cs:838-843` short-circuits
to `_journaler.NeedsHuman(...)` before any salvage call, so no patch, no ref and no salvage text are
ever produced on this path.

**I9 is an INSERTED pin the plan does not list.** Section 3.4 divergence 2 states the prohibition ("the
feedback wording must not claim a rollback") but section 3.5 and section 8 give it no pin, so nothing would have
caught an implementer who reused the `Retry` framing's bytes verbatim on this path - the cheapest
wrong implementation available, and one that tells a human the work was saved from a rollback that
never happened. Assert the positive half too (the text says the tree is orphaned), or the pin is
satisfied by a path that emits no text at all.

### Windows-Git test portability - four behaviours to get right (#116)

These tests build real git repositories, and Git-for-Windows has four semantics a POSIX-only helper
misses. **Mirror `tests/Guardrails.Integration.Tests/RetrySalvageTests.cs`** - it already builds real
repos, already uses the shared `HostRepoCleanlinessGuard` fixture declared at the foot of that file,
and already gets these right. Reuse that fixture via `IClassFixture<HostRepoCleanlinessGuard>` rather
than inventing a new one; do NOT author a new shared fixture file (it would be outside your
`writeScope`). Whatever helper you write inline must:

- **strip read-only attributes before `Directory.Delete(recursive)`** - Git marks loose objects under
  `.git/objects` read-only on Windows, and the delete throws `UnauthorizedAccessException`, not
  `IOException`;
- **recreate a directory that `git rm`/`git mv` pruned** before writing into it - Git-for-Windows
  removes the now-empty parent, and the next `File.WriteAllText` throws `DirectoryNotFoundException`;
- **roll back with `git reset --hard <preHead>`, never `git merge --abort`** - `--abort` fails rc=128
  on a dirtied tracked path;
- **set `core.autocrlf=false`** so fixture content hashes are deterministic across platforms.

### What you must NOT do

- Do NOT touch `src/**`. If a pin needs a production seam that does not exist, that is a finding, not
  a licence: write `{"needsHuman": "<what is missing>"}` and stop.
- Do NOT edit `tests/Guardrails.Core.Tests/RetryPolicySalvageAdviceTests.cs` or
  `tests/Guardrails.Integration.Tests/RetrySalvageTests.cs`. Those two shipped suites passing with ZERO
  edits is what makes stages 2 and 3 legitimately test-free (plan section 3.3); they are outside your
  `writeScope` and editing one fails this task immediately.
- Do NOT name `SalvageFraming`, `SalvagePatchPath`, `SalvageRefName`, or `restrictToScope` in code.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Execution/EscalationSalvageTests.cs` and
`tests/Guardrails.Integration.Tests/EscalationSalvageTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside these paths - including changes to other
production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.
