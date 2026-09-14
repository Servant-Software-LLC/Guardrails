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
commit WITH your git hooks, then re-check #588 and #448 against the trial ref before the fast-forward*).

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
      public required bool UserTipWasAncestor { get; init; }  // true: the quiet case, no merge commit, no hook
      public MergeOnSuccessResult? Refusal { get; init; }     // Conflict or HookRejected when no trial could be built
      public string? RefusalDetail { get; init; }             // the hook's stderr, or the conflicting paths
  }
  ```

- Three DEFAULT-BODIED members on `IWorktreeProvider`, each throwing `NotImplementedException`, so
  `FakeWorktreeProvider`, `RecordingWorktreeProvider` and every other implementer keep compiling:

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
  (`integ.OriginalBranch`) and the plan branch tip, in a HARNESS-OWNED worktree — never the user's
  checkout.
  - If the user's tip is an ancestor of the plan tip (the quiet case), the ref points at the plan tip:
    no merge commit, no hook, `UserTipWasAncestor = true`.
  - Otherwise it creates a merge commit whose FIRST parent is the user's tip and second parent is the
    plan tip, WITHOUT `--no-verify`, so the user's hooks run exactly as they do on today's run-end merge
    commit; `UserTipWasAncestor = false`.
  - A rejecting hook, or a conflict, yields a `TrialDelivery` whose `Refusal` is `HookRejected` /
    `Conflict`, with `RefusalDetail` filled, `Commit` null, and no trial ref left behind.
- **`PromoteTrialDelivery`** re-checks #588 (the checkout is still on `integ.OriginalBranch`), then #448
  (tracked dirt the fast-forward to the trial ref would overwrite), in that order — returning
  `BranchMoved` / `DirtyWorkingTree` with the detail on `LastMergeOnSuccessDetail`, exactly as
  `MergePlanBranchIntoUserBranch` does. A user's branch that no longer points at `trial.UserTip` is also
  `BranchMoved`: the gate authorized the trial's tree and nothing else. Only then does it fast-forward
  the user's branch to the trial commit and return `FastForwarded`. It never creates a commit, never
  falls back to a real merge, and never forces.
- **`DiscardTrialDelivery`** deletes the trial ref, and does not throw when there is none.

**Pin these behaviours to these EXACT method names:**

- `ATrialMergeCommit_RunsTheUsersHooks_SoAHookCanRejectIt` — the user's branch gains a commit mid-run,
  so a merge commit is needed, and a `pre-commit` hook exits non-zero. Assert `Refusal == HookRejected`,
  `RefusalDetail` carries the hook's output, no trial ref exists, and the user's branch is unmoved. This
  is the row that separates the maintainer's answer from the rejected `--no-verify` option. Rejects: a
  trial merge made with `--no-verify`, or with `git commit-tree`, which runs no hook.
- `TheQuietCase_CreatesNoMergeCommit_AndRunsNoHook` — the user's branch did not move; install the same
  failing hook. Assert `UserTipWasAncestor`, `Refusal` null, and the trial ref resolving to the plan
  tip. Rejects: always creating a merge commit (the hook would reject it).
- `UserTipWasAncestor_IsCorrectBothWays` — once with the user's branch unmoved (true), once with a user
  commit mid-run (false). Rejects: a constant, and comparing tips for equality instead of ancestry.
- `TheTrialMergeCommit_HasTheUsersTipAsFirstParent` — in the moved case, `<Commit>^1` is the user's tip
  and `<Commit>^2` is the plan tip, so after promotion the user's `--first-parent` history is their own
  line, exactly as today's run-end merge commit leaves it. Rejects: merging the user's tip INTO the plan
  branch's line.
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
  branch, as a user keeps working while the gate runs. Assert `BranchMoved`, a `LastMergeOnSuccessDetail`
  naming the tip the trial was built from, the user's branch still at their new commit, and no commit
  created. Rejects: falling back to a real merge when `--ff-only` fails, as `MergePlanBranchIntoUserBranch`
  does, which lands a tree no gate saw.
- `Promotion_FastForwardsTheUsersBranchToTheTrialCommit` — build a trial in the moved case and promote
  it. Assert `FastForwarded` and that the user's branch tip IS the trial commit — the same sha, so the
  tree the gate saw is the tree that landed, and no further commit was made.
- `Discard_RemovesTheTrialRef` — after a trial in each case, `DiscardTrialDelivery` leaves no
  `refs/guardrails/trial/<waveDir>` (`git show-ref --verify` fails), and a second call does not throw.

**Build the fixtures the way the house already does.** Drive the REAL `GitWorktreeProvider` over temp
repos — `MergeOnSuccessTests` constructs one directly for
`MergePlanBranchIntoUserBranch_HeadCutToAnotherBranch_RefusesAndMergesNothing` and its siblings, and
`CreateIntegration` gives you the plan branch and its worktree to commit on. Install the hook the way
`GitHookIsolationTests` does, including the executable bit on Linux and macOS: CI runs all three.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong. A hook is a file inside the temp repo, not process state.

The tests MUST COMPILE and FAIL: every one of them calls a stubbed provider member. Do NOT implement the
members.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/WaveDelivery/TrialDeliveryPrimitiveTests.cs`, `src/Guardrails.Core/Execution/IWorktreeProvider.cs`, and `src/Guardrails.Core/Execution/TrialDelivery.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
