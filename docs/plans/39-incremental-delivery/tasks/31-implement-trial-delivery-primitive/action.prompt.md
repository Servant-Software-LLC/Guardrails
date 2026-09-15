## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `31-implement-trial-delivery-primitive`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "31-implement-trial-delivery-primitive": { "someKey": "someValue" } }`.
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

Make `TrialDeliveryPrimitiveTests` pass — design 39 §1's trial merge, review round 4's
`d39-trial-delivery-primitive` answer, as corrected by the 2026-09-13 reviews. Implement the three members
in `GitWorktreeProvider`, keeping the contract task 30 documented on `IWorktreeProvider`.

- **`CreateTrialDelivery`.**
  - Resolve the user's tip from `integ.OriginalBranch` and the plan tip from `integ.PlanBranchName`.
  - First remove whatever a crashed earlier attempt for the same `waveDir` left behind: its trial ref and
    its trial worktree.
  - **Equal tips FIRST.** If the user's tip and the plan tip are the same commit, a quiet-case fast-forward
    already landed. Point `refs/guardrails/trial/<waveDir>` at that commit, build nothing and run no hook;
    `AlreadyDelivered = true`, `UserTipWasAncestor = true`, `Commit` = that commit. Equal tips also pass both
    ancestry checks below, and taking the quiet case would promote again: that re-announces the delivery,
    and after a switched checkout it refuses a delivery that already landed (review 2026-09-13).
  - **Quiet case.** Otherwise, if `git merge-base --is-ancestor <user-tip> <plan-tip>` succeeds, point the ref
    at the plan tip, and do nothing else. `UserTipWasAncestor = true`.
  - **Already delivered.** Otherwise, if `git merge-base --is-ancestor <plan-tip> <user-tip>` succeeds, the
    work is already on the user's branch: a resume after a merge-commit promotion landed, or the user merged
    the plan branch themselves.
    - Point the ref at the user's tip, build nothing, and run no hook.
    - Set `AlreadyDelivered = true`, `UserTipWasAncestor = false`, and `Commit` to the user's tip.
    - Never fall through to the merge. There, `git merge` says "Already up to date", `git commit` fails
      with "nothing to commit", and that failure reads as a hook rejection that halts every resume
      (review 2026-09-13, measured).
  - **Otherwise, build the merge** in a worktree the harness owns, for example a detached
    `git worktree add` under the integration root. Never use the user's checkout, and never the
    integration worktree, which is on the plan branch.
    - Check out the user's tip and run `git merge --no-commit <plan-tip>`.
    - Commit with `--no-edit` and WITHOUT `--no-verify`, so the user's hooks run.
    - Capture the hook's stderr with `TryGitInWithStderr`, as `MergePlanBranchIntoUserBranch` does for its
      user-facing commit.
  - **Run the hooks the user's own checkout would run.**
    - `MergePlanBranchIntoUserBranch` commits in the user's checkout, where git finds hooks relative to
      that checkout.
    - A harness worktree resolves a RELATIVE `core.hooksPath` against ITSELF instead. husky's untracked
      `.husky/_` is then not found, and the commit goes through unchecked (review 2026-09-13, measured on
      Windows git 2.53).
    - So resolve the hooks directory in the user's repo (`rev-parse --git-path hooks`), make it absolute
      against the user's repo root when git returns a relative path, and pass it to the commit as
      `-c core.hooksPath=<absolute path>`.
  - **On success**, point the trial ref at the new commit and KEEP the worktree: return its path as
    `WorktreePath`, so the Scheduler can run the wave's exit gate in the trial tree. Only
    `DiscardTrialDelivery` removes it.
  - **A conflict.** Collect the conflicting paths (`git diff --name-only --diff-filter=U`), ordinal-sorted
    and newline-separated, as `RefusalDetail`. Then abort the merge, remove the worktree, leave no ref, and
    return `Refusal = Conflict`.
  - **A rejecting hook.** `RefusalDetail` is the hook's trimmed stderr. Abort the merge, remove the
    worktree, leave no ref, and return `Refusal = HookRejected`.
- **`PromoteTrialDelivery`.**
  - A trial carrying a `Refusal` returns it unchanged and touches nothing. An `AlreadyDelivered` trial
    returns `FastForwarded` and touches nothing: its tree is already on the user's branch.
  - Otherwise check, in this order, setting `LastMergeOnSuccessDetail` the way
    `MergePlanBranchIntoUserBranch` does:
    1. #588: `HeadMovedDetail(integ.OriginalBranch)`. A switched checkout returns `BranchMoved` with that
       text.
    2. The user's branch still points at `trial.UserTip`. If not, it moved after the trial was built:
       return `BranchMoved` and attempt no merge. After an advance, the detail reads exactly
       `'<branch>' moved from <sha10> to <sha10> after the trial was built` (the tip the trial was built
       from, then the tip now). Compare the sha itself, never the result of `--ff-only`: after the user
       rewinds their branch, the fast-forward to the trial commit SUCCEEDS and re-lands the commit they
       dropped.
    3. #448: the intersection computed against the TRIAL REF, not the plan branch (`BlockingDirtyPaths`
       takes the ref it compares with). Return `DirtyWorkingTree` with the paths.
  - Then run `git merge --ff-only <trial ref>` in the user's repo. Never fall back to a real merge the way
    `MergePlanBranchIntoUserBranch` does when `--ff-only` fails. The gate authorized the trial's tree, and
    nothing else may land.
- **`DiscardTrialDelivery`.** Run `git update-ref -d` on the trial ref, ignoring a missing ref. Remove the
  trial worktree (`git worktree remove --force`, then prune), ignoring one that is already gone.

**Leave task 30's throwing DEFAULT bodies on `IWorktreeProvider` exactly as they are** (review 2026-09-13).
A test double that forgets one of these members must fail loudly. A quiet default would let it record a
delivery with an empty commit while the user's branch never moved. Nothing in the plan needs a default:
`FakeWorktreeProvider` is not constructed anywhere in `src/`, and every test that delivers either drives the
real provider or implements the members it uses.

**Document both `BranchMoved` causes on `IWorktreeProvider`.** The summary of `LastMergeOnSuccessDetail`
says `BranchMoved` carries "the branch the run started on and the one HEAD is on now". From a promotion it
can also mean the branch moved after the trial was built. Name both causes, and on the trial members say
which remedy each needs. After an advance, the operator resumes, and the next trial includes the new
commits. After a switched checkout, they check the branch out again first.

Do NOT change `MergePlanBranchIntoUserBranch`: the run-end delivery still uses it for every flat plan and for
the waves the barrier does not deliver. This task's tests-pass guardrail also runs `MergeOnSuccessTests`,
which pins that path, and `GitHookIsolationTests`, which pins the #149 hook rules (harness commits skip the
user's hooks; the user-facing commit keeps them).

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/GitWorktreeProvider.cs`, `src/Guardrails.Core/Execution/IWorktreeProvider.cs`, and `src/Guardrails.Core/Execution/TrialDelivery.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
