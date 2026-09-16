## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `10-author-tests-commit-paths`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "10-author-tests-commit-paths": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author failing tests AND the minimal throwing stub for `SuppliedDrain.CommitPaths` — design 41 §5
("Drain target and provenance").

**Test file:** `tests/Guardrails.Core.Tests/Supply/SuppliedDrainTests.cs` (it already exists — EDIT it)
**Test class:** `SuppliedDrainTests`
**Stub: ONE new member on `src/Guardrails.Core/Execution/SuppliedDrain.cs`, with a THROWING body.**

```csharp
/// <summary>
/// Commit exactly <paramref name="paths"/> (workspace-relative, forward-slash) as they stand in
/// <paramref name="workspace"/>, with the §4 trailer <c>Supplied-By: &lt;by&gt;</c> /
/// <c>Guardrails-Run: &lt;runId&gt;</c>. The EXPLICIT PATHSPEC is the point: nothing else left in the
/// index rides along. On ANY failure the pre-commit <c>HEAD</c> is restored, so no partly-staged file
/// can be picked up by a later drain under someone else's name (design 41 §5).
/// </summary>
public static SuppliedDrainResult CommitPaths(
    string workspace, string runId, string by, IReadOnlyList<string> paths) =>
    throw new NotImplementedException();
```

Pin that signature exactly — task 11 fills it in and cannot change the shape, and the Scheduler's
auto-resolve (a later task) calls it with a list it computed, never with a staging tree.

**FIRST, fix the file's own false header.** `SuppliedDrainTests.cs:15` claims *"`SuppliedDrain.Drain`
currently throws `NotImplementedException`"*. That is not true — `Drain` is fully implemented and its five
tests are GREEN on your base. Rewrite that paragraph to say what is actually red here: `CommitPaths` is
the stub, and `Drain_CommitsOnlyTheStagedFiles_WhenAnUnrelatedFileIsStaged` is red because today's
`Drain` commits with NO pathspec. Do not copy the stale claim forward.

**The class-level trait becomes the plan trait.** Replace `[Trait("Category", "Supply")]` on the class
with `[Trait("Category", "OverwatchSupply")]`. This is deliberate and load-bearing: task 11's forward
check runs `Category=OverwatchSupply&FullyQualifiedName~SuppliedDrainTests`, and the EXISTING `Drain_*`
tests must be inside that filter — "`Drain`'s behaviour is unchanged after the refactor" is proven by
running those tests against the refactored `Drain`, not by reading it. A trait left on `Supply` silently
drops them from the only run that checks them.

**Extend the private nested `TempGitRepo`, do not reach for a shared helper.** There is none — the
fixture is copy-pasted ~20 times across this repo, and this class already owns a correct one (read-only
attributes stripped before delete via `SafeDelete.DeleteDirectory`, `core.autocrlf=false`, hooks pointed
at an empty dir, signing off). Keep all four of those and add what these tests need: a `WriteFile` that
`Directory.CreateDirectory`s the parent first (git PRUNES a now-empty parent on Git-for-Windows, so a
later write into it throws), a `ResetHard(commitish)` (`git reset --hard` — NEVER `git merge --abort`,
which exits 128 on a dirtied tracked path), a `Status()` returning `git status --porcelain`, and an
accessible `Git(params string[])` so a test can stage its own unrelated file.

**Pin these behaviours to these EXACT method names:**

- `CommitPaths_CommitsOnlyItsPathspec_WhenAnUnrelatedFileIsStaged` — **the headline property.** Write
  and `git add` an unrelated file named exactly `unrelated-staged.txt`, write `vendor/x.js`, then call
  `CommitPaths` for `vendor/x.js` ALONE. Assert the new commit's own file list (`git show --name-only
  --format=` on `HEAD`) is exactly `vendor/x.js`, and that `unrelated-staged.txt` is still uncommitted
  (it is still in `git status --porcelain`). Today `Drain` commits with no pathspec at all, which is the
  defect this member exists to close — an operator's half-staged work must never be committed under a
  supplier's name.
- `CommitPaths_CommitsWithTheSuppliedByTrailer` — a `[Theory]` over `operator`, `overwatcher` and
  `task:03-author-tests-drain`, mirroring `Drain_CommitsWithTheSuppliedByTrailer`. `Supplied-By: <by>`
  and `Guardrails-Run: <runId>` are present; the constant `Supplied-By-Operator` never is.
- `CommitPaths_ReturnsTheCommittedPathsBytesAndCommitSha` — `CommittedPaths` holds the paths as given
  (forward-slash, workspace-relative), `TotalBytes` is the sum of the committed files' lengths, and
  `CommitSha` equals the repo's new `HEAD`.
- `CommitPaths_OnAnEmptyPathList_MakesNoCommit` — the never-weaker row, matching `Drain`'s own
  empty-staging-tree guarantee: `HEAD` is unchanged, `CommittedPaths` is empty, `TotalBytes` is 0 and
  `CommitSha` is null.
- `CommitPaths_WhenTheCommitFails_ResetsHardToThePreCommitHead` — **make the failure happen at the
  COMMIT step, not the `add` step**, or the rollback is untested. The measured way (verified against real
  git while this prompt was written): commit a file, then call `CommitPaths` for that SAME path with its
  content UNCHANGED while an unrelated modification sits staged in the index — `git add` exits 0 and
  `git commit --no-verify -m … -- <path>` exits **1** with *"no changes added to commit"*. Assert that
  the call THROWS (`InvalidOperationException`, the exception `GitIn` already raises), that `HEAD` still
  equals the pre-call sha, and that `git status --porcelain` is now EMPTY. That last assertion is the
  only one that can see the `reset --hard` actually ran: the unrelated staged change is gone. A test that
  asserts only "HEAD is unchanged" passes against an implementation that rolls nothing back.
- `Drain_CommitsOnlyTheStagedFiles_WhenAnUnrelatedFileIsStaged` — the same protection for the OPERATOR
  path, which is why task 11 refactors `Drain` to go through `CommitPaths` rather than leaving two
  commit mechanisms. Stage `vendor/x.js` through the drain's staging tree, `git add` an unrelated
  `unrelated-staged.txt`, call `Drain`, and assert the drain commit's file list is exactly
  `vendor/x.js`. **This one is red against today's fully-implemented `Drain`**, not against the stub.

**The five existing `Drain_*` tests stay, unchanged, and are DECLARED EXEMPT from the red census**
(`Drain_OnAnEmptyStagingTree_DoesNothingAndMakesNoCommit`,
`Drain_CopiesEveryStagedFileToItsWorkspacePath`, `Drain_CommitsWithTheSuppliedByTrailer`,
`Drain_DeletesTheStagingTreeAfterCommitting`, `Drain_ReturnsTheCommittedPathsAndByteCount`). They pin
behaviour that must NOT change, so a correct test has nothing to be red about. The census asserts each
one still EXISTS, and task 11's forward census requires each observed `Passed`. Do NOT weaken or delete
them to please a check.

The six pinned tests MUST COMPILE and FAIL. Do NOT implement `CommitPaths`, and do not change `Drain`'s
body — that is task 11's work.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Supply/SuppliedDrainTests.cs` and `src/Guardrails.Core/Execution/SuppliedDrain.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
