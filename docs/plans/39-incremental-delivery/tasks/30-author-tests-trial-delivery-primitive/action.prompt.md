## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `30-author-tests-trial-delivery-primitive`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "30-author-tests-trial-delivery-primitive": { "someKey": "someValue" } }`.
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

Author failing tests AND the stubs for the trial-delivery primitive — design 39 §1's trial merge, as
decided in review round 4 (`d39-trial-delivery-primitive`: *new provider members build the trial merge
commit WITH your git hooks, then re-check #588 and #448 against the trial ref before the fast-forward*),
and as corrected by the 2026-09-13 reviews.

**Test file:** `tests/Guardrails.Integration.Tests/WaveDelivery/TrialDeliveryPrimitiveTests.cs`
**Test class:** `TrialDeliveryPrimitiveTests`
**Stub files:** `src/Guardrails.Core/Execution/IWorktreeProvider.cs` and `src/Guardrails.Core/Execution/TrialDelivery.cs`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Why this primitive exists.** A delivering wave merges the plan branch onto a scratch ref, runs its exit
gate against that tree, and only then fast-forwards the user's branch to it. No `IWorktreeProvider`
member can do that today, and the three checks the run-end delivery relies on are private to
`GitWorktreeProvider.MergePlanBranchIntoUserBranch`: the #588 moved-branch refusal (`HeadMovedDetail`),
the #448 dirty-tree intersection (`BlockingDirtyPaths`), and the #149 rule that the commit landing on the
user's branch runs the user's git hooks. A promotion is always a fast-forward, and a fast-forward runs
no hook — so the hooks have to run when the TRIAL merge commit is created, or `HookRejected` silently
becomes unreachable for waved plans. The maintainer chose to keep the hooks.

**Write these stubs, and make them COMPILE:**

- `src/Guardrails.Core/Execution/TrialDelivery.cs` — a WORKING data record. The declaration is the
  data; what is unimplemented is the provider members that produce it.

  ```csharp
  public sealed record TrialDelivery
  {
      public required string WaveDir { get; init; }
      public required string TrialRef { get; init; }          // refs/guardrails/trial/<waveDir>
      public string? Commit { get; init; }                    // what TrialRef points at; null when Refusal is set
      public required string UserTip { get; init; }           // the user's branch tip the trial was built from
      public required bool UserTipWasAncestor { get; init; }  // true: the user's tip is the plan tip or an ancestor of it; no merge commit, no hook
      public bool AlreadyDelivered { get; init; }             // true: the user's branch already contains the plan tip (equal tips, or the plan tip is an ancestor)
      public string? WorktreePath { get; init; }              // the harness worktree checked out at Commit; set only when a merge commit was built
      public MergeOnSuccessResult? Refusal { get; init; }     // Conflict or HookRejected when no trial could be built
      public string? RefusalDetail { get; init; }             // the hook's output, or the conflicting paths
  }
  ```

- Three DEFAULT-BODIED members on `IWorktreeProvider`, each throwing `NotImplementedException`, so
  `FakeWorktreeProvider`, `RecordingWorktreeProvider` and every other implementer keep compiling. Task 31
  keeps these defaults throwing: a test double that forgets a member must fail loudly, never record a
  delivery that did not happen.

  ```csharp
  TrialDelivery CreateTrialDelivery(IntegrationHandle integ, string waveDir, CancellationToken ct) =>
      throw new NotImplementedException();

  MergeOnSuccessResult PromoteTrialDelivery(IntegrationHandle integ, TrialDelivery trial, CancellationToken ct) =>
      throw new NotImplementedException();

  void DiscardTrialDelivery(IntegrationHandle integ, string waveDir) =>
      throw new NotImplementedException();
  ```

  Document each member with the contract below. A default interface member is reachable only through
  the interface, so every test calls these on an `IWorktreeProvider`-typed variable that holds a real
  `GitWorktreeProvider`.

**The contract the tests pin:**

- **`CreateTrialDelivery`** builds `refs/guardrails/trial/<waveDir>` from the user's branch tip
  (`integ.OriginalBranch`) and the plan branch tip, never touching the user's checkout. It checks four
  cases, in this order:
  1. **Already delivered, quiet.** The user's tip EQUALS the plan tip: a resume right after a quiet-case
     promotion, whose fast-forward already landed. The ref points at that commit, and `Commit` is it. No
     merge commit, no hook, no worktree; `AlreadyDelivered = true`, `UserTipWasAncestor = true`. This check
     comes first because equal tips also pass both ancestry checks below.
  2. **The quiet case.** The user's tip is a STRICT ancestor of the plan tip: the ref points at the plan
     tip. No merge commit, no hook, no worktree; `UserTipWasAncestor = true`, `AlreadyDelivered = false`.
  3. **Already delivered, merged.** The plan tip is a strict ancestor of the user's tip, so the work is
     already on the user's branch: a resume after a merge-commit promotion landed, or the user merged the
     plan branch themselves. The ref points at the user's tip, and `Commit` is that tip. No merge commit,
     no hook, no worktree; `AlreadyDelivered = true`, `UserTipWasAncestor = false`.
  4. **A merge commit.** Otherwise it creates, in a HARNESS-OWNED worktree, a merge commit whose FIRST
     parent is the user's tip and second parent is the plan tip, WITHOUT `--no-verify`, running the hooks
     the user's own checkout would run, including hooks under a RELATIVE `core.hooksPath`.
     `UserTipWasAncestor = false`. The worktree stays checked out at the new commit and `WorktreePath`
     names it, so the Scheduler can run the wave's exit gate there.
  - A rejecting hook, or a conflict, yields `Refusal` = `HookRejected` / `Conflict`, `Commit` null, and no
    trial ref or worktree left behind. `RefusalDetail` is the hook's output for `HookRejected`, and the
    newline-separated, ordinal-sorted conflicting paths for `Conflict` (the format #448 uses for dirty
    paths).
- **`PromoteTrialDelivery`**:
  - A trial carrying a `Refusal` returns it unchanged and touches nothing. An `AlreadyDelivered` trial
    returns `FastForwarded` and touches nothing.
  - Otherwise it checks, in this order, putting the detail on `LastMergeOnSuccessDetail`:
    1. #588: the checkout is still on `integ.OriginalBranch`. If not, it returns `BranchMoved` with
       `HeadMovedDetail`'s text, which names both branches.
    2. The user's branch still points at `trial.UserTip`. If not, it returns `BranchMoved` with a detail
       naming the branch, the tip the trial was built from, and the tip now. After an advance, the
       detail reads exactly `'<branch>' moved from <sha10> to <sha10> after the trial was built`,
       each sha shortened to its first 10 characters.
    3. #448: tracked dirt the fast-forward to the trial ref would overwrite. If there is any, it returns
       `DirtyWorkingTree` with the paths.
  - Only then does it fast-forward the user's branch to the trial commit and return `FastForwarded`. It
    never creates a commit, never falls back to a real merge, and never forces.
  - The two `BranchMoved` causes need different remedies, which is why their details differ. After an
    advance, the operator resumes, and the next trial includes their new commits. After a switched
    checkout, they check the branch out again first.
- **`DiscardTrialDelivery`** deletes the trial ref and removes the trial's worktree, and does not throw
  when there is neither.

**Pin these behaviors to these EXACT method names:**

- `ATrialMergeCommit_RunsTheUsersHooks_SoAHookCanRejectIt` — the user's branch gains a commit mid-run,
  so a merge commit is needed, and a `pre-commit` hook exits non-zero. Assert `Refusal == HookRejected`,
  `RefusalDetail` carries the hook's output, no trial ref and no trial worktree exist, and the user's
  branch is unmoved. This is the row that separates the maintainer's answer from the rejected
  `--no-verify` option. Rejects: a trial merge made with `--no-verify`, or with `git commit-tree`, which
  runs no hook.
- `ATrialMergeCommit_RunsHooksFromARelativeUntrackedHooksPath` — husky's layout. Set the temp repo's local
  `core.hooksPath` to the RELATIVE path `.husky/_`, and put a failing, output-writing `pre-commit` hook
  there WITHOUT committing it. Then take the moved case. Assert `Refusal == HookRejected`, `RefusalDetail`
  carries the hook's output, no trial ref and no trial worktree exist, and the user's branch is unmoved.
  Rejects: committing the trial merge with git's own hook lookup inside the harness worktree. Git resolves
  a relative `core.hooksPath` against THAT worktree, finds nothing, and lets the commit through unchecked.
  This was measured in review (2026-09-13, Windows git 2.53), and the `.git/hooks` fixture in the row above
  cannot see it.
- `TheQuietCase_CreatesNoMergeCommit_AndRunsNoHook` — the plan branch carries a commit and the user's branch
  did not move; install the same failing hook. Assert `UserTipWasAncestor`, `AlreadyDelivered` false,
  `Refusal` null, and the trial ref resolving to the plan tip. Rejects: always creating a merge commit (the
  hook would reject it).
- `UserTipWasAncestor_IsCorrectBothWays` — once with a plan commit and the user's branch unmoved (true), once
  with a user commit mid-run (false). Rejects: a constant, and comparing tips for equality instead of
  ancestry.
- `ATrialRebuiltAfterAQuietPromotionLanded_IsAlreadyDelivered` — the resume right after a quiet-case promotion.
  - With a commit on the plan branch only, build the trial (the quiet case), promote it (`FastForwarded`, so
    the user's tip now equals the plan tip), and discard it.
  - Install a failing `pre-commit` hook that also writes a marker file, and call `CreateTrialDelivery` again
    for the same wave.
  - Assert `AlreadyDelivered`, `UserTipWasAncestor` true, `Refusal` null, `Commit` equal to both tips,
    `WorktreePath` null, and no marker file.
  - Then check out a different branch in the user's repo and call `PromoteTrialDelivery` on that trial. It
    returns `FastForwarded`, and neither branch moved.

  Rejects: taking the quiet case on equal tips. That promotes again, re-announcing a delivery that already
  landed; after a switched checkout it refuses that delivery as `BranchMoved` and halts over work already on
  the user's branch.
- `ATrialRebuiltAfterItsPromotionLanded_IsAlreadyDelivered` — the resume after a crash that followed the
  promotion.
  - Build a trial in the moved case, promote it (`FastForwarded`), and discard it.
  - Install a failing `pre-commit` hook that also writes a marker file, then call `CreateTrialDelivery`
    again for the same wave.
  - Assert `AlreadyDelivered`, `UserTipWasAncestor` false, `Refusal` null, `Commit` equal to the user's
    tip (the promoted merge commit), `WorktreePath` null, no marker file (no hook ran), and the user's
    branch unmoved.
  - Then `PromoteTrialDelivery` on that trial returns `FastForwarded`, and the user's branch tip is still
    the same commit.

  Rejects: rebuilding the merge. `git merge` then reports "Already up to date", and `git commit` fails
  with "nothing to commit". Read as a hook rejection, that failure halts every resume. It was measured in
  review.
- `TheTrialMergeCommit_HasTheUsersTipAsFirstParent` — in the moved case, `<Commit>^1` is the user's tip
  and `<Commit>^2` is the plan tip, so after promotion the user's `--first-parent` history is their own
  line, exactly as today's run-end merge commit leaves it. Rejects: merging the user's tip INTO the plan
  branch's line.
- `TheMovedCase_KeepsAWorktreeAtTheTrialCommit_UntilDiscarded` — in the moved case, `WorktreePath` is
  set, the directory exists, `git -C <WorktreePath> rev-parse HEAD` equals `Commit`, and the path is
  neither the user's repo nor `integ.IntegrationWorktreePath`. In the quiet case, `WorktreePath` is null.
  After `DiscardTrialDelivery` the directory is gone, and `git worktree list --porcelain` no longer lists it
  (compare full paths after normalizing separators). Rejects: removing the worktree before the Scheduler
  can run the exit gate in it, and leaking one worktree per delivery.
- `ATrialThatConflicts_IsRefusedWithTheConflictingPaths` — the plan branch and a mid-run commit on the
  user's branch change the same lines of two tracked files. Name them so that ordinal order differs from
  the order you create them in, for example by committing `b.txt` before `a.txt`. Assert:
  - `Refusal == Conflict` and `RefusalDetail` equal to `"a.txt\nb.txt"` (newline-separated, ordinal-sorted);
  - `Commit` null, no trial ref, and no trial worktree left behind (`git worktree list --porcelain` lists
    the same worktrees as before);
  - the user's branch unmoved and their checkout unchanged.

  Rejects: a `Conflict` with no detail. Today's run-end `Conflict` carries none, so a halt would read
  "delivery REFUSED (conflict):" with nothing after it.
- `CreatingATrial_NeverTouchesTheUsersCheckout` — with a passing hook, in the moved case: after
  `CreateTrialDelivery` the user's `HEAD` sha, current branch, and `git status --porcelain` output are
  identical to before. Rejects: building the merge in the user's checkout and resetting afterwards.
- `Promotion_RefusesWhenTheCheckoutMovedToAnotherBranch` — build a trial, then check out a different
  branch in the user's repo. Assert `BranchMoved`, both branch names in `LastMergeOnSuccessDetail`, and
  neither branch moved (#588).
- `Promotion_RefusesDirtTheFastForwardWouldOverwrite` — build a trial whose tree changes a tracked file,
  then leave an uncommitted edit to that file in the user's checkout. Assert `DirtyWorkingTree`, the path
  named in `LastMergeOnSuccessDetail`, and the user's branch unmoved. In the same test, dirt in a file
  the trial does NOT change must not block the promotion (#448's narrowing).
- `Promotion_RefusesWhenTheUsersBranchAdvancedAfterTheTrial` — build a trial, then commit on the user's
  branch, as a user keeps working while the gate runs. Assert:
  - `BranchMoved`;
  - `LastMergeOnSuccessDetail` equal to `'<branch>' moved from <sha10> to <sha10> after the trial was
    built`, where the first sha is `trial.UserTip` and the second is the new commit, each cut to 10
    characters;
  - the user's branch still at their new commit, and no commit created.

  Rejects: falling back to a real merge when `--ff-only` fails, as `MergePlanBranchIntoUserBranch` does,
  which lands a tree no gate saw.
- `Promotion_RefusesWhenTheUsersBranchWasRewoundAfterTheTrial` — build a trial in the moved case, so the
  user's tip is their own mid-run commit, then reset the user's branch back one commit (`git reset --hard
  HEAD~1`), dropping that commit. Promote. Assert:
  - `BranchMoved`, with `LastMergeOnSuccessDetail` containing both the dropped commit's and the new tip's
    10-character shas;
  - the user's branch still at the reset tip;
  - the dropped commit NOT an ancestor of it.

  Rejects: relying on `--ff-only` failing to notice a moved branch. After a rewind, the fast-forward to the
  trial commit SUCCEEDS and silently re-lands the commit the user dropped.
- `Promotion_FastForwardsTheUsersBranchToTheTrialCommit` — build a trial in the moved case and promote
  it. Assert `FastForwarded` and that the user's branch tip IS the trial commit — the same sha, so the
  tree the gate saw is the tree that landed, and no further commit was made.
- `Discard_RemovesTheTrialRef` — after a trial in each case, `DiscardTrialDelivery` leaves no
  `refs/guardrails/trial/<waveDir>` (`git show-ref --verify` fails), and a second call does not throw.

**Build the fixtures the way the house already does.**
- Drive the REAL `GitWorktreeProvider` over temp repos: `MergeOnSuccessTests` constructs one directly for
  `MergePlanBranchIntoUserBranch_HeadCutToAnotherBranch_RefusesAndMergesNothing` and its siblings.
- `CreateIntegration` gives you the plan branch and its worktree to commit on.
- Install hooks the way `GitHookIsolationTests` does, including the executable bit on Linux and macOS, for
  the `.husky/_` hook too: CI runs all three.
- A guardrail requires the test file to construct `new GitWorktreeProvider(`. Every row calls the members on
  the real provider, so a wrapper would only test the wrapper. The guardrail forbids:
  - `FakeWorktreeProvider`, `RecordingWorktreeProvider`, or any other provider type named in a string for
    reflection;
  - any type declared, aliased or `DispatchProxy`-built to implement `IWorktreeProvider`;
  - `IWorktreeProvider` handed to a mocking library.

  A `Func<IWorktreeProvider>` helper is fine.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong. A hook, and a `core.hooksPath` in the temp repo's own config, live
inside the temp repo, not in process state.

The tests MUST COMPILE and FAIL: every one of them calls a stubbed provider member. Do NOT implement the
members.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/WaveDelivery/TrialDeliveryPrimitiveTests.cs`, `src/Guardrails.Core/Execution/IWorktreeProvider.cs`, and `src/Guardrails.Core/Execution/TrialDelivery.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
