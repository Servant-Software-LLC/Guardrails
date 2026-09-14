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
`d39-trial-delivery-primitive` answer. Implement the three members in `GitWorktreeProvider`, keeping
the contract task 30 documented on `IWorktreeProvider`.

- **`CreateTrialDelivery`.**
  - Resolve the user's tip from `integ.OriginalBranch` and the plan tip from `integ.PlanBranchName`.
  - **Quiet case** — `git merge-base --is-ancestor <user-tip> <plan-tip>` succeeds: point
    `refs/guardrails/trial/<waveDir>` at the plan tip, and nothing else.
  - **Otherwise** build the merge in a TEMPORARY worktree the harness owns (for example a detached
    `git worktree add` under the integration root) — never in the user's checkout, and never in the
    integration worktree, which is on the plan branch. Check out the user's tip, `git merge --no-commit
    <plan-tip>`, then `git commit --no-edit` WITHOUT `--no-verify`, so the user's hooks run. That is the
    same command pair `MergePlanBranchIntoUserBranch` uses for its user-facing commit, with the same
    `TryGitInWithStderr` capture of the hook's stderr. On success, point the trial ref at the new commit.
  - A conflict or a rejecting hook: abort the merge, leave no ref, and return the refusal with its
    detail.
  - Always remove the temporary worktree.
- **`PromoteTrialDelivery`.**
  - A trial carrying a `Refusal` returns it unchanged and touches nothing.
  - Otherwise run the #588 check (`HeadMovedDetail(integ.OriginalBranch)`) FIRST, then the #448
    intersection computed against the TRIAL REF, not the plan branch (`BlockingDirtyPaths` takes the ref
    it compares with). Set `LastMergeOnSuccessDetail` exactly as `MergePlanBranchIntoUserBranch` does.
  - Then `git merge --ff-only <trial ref>` in the user's repo.
  - If the user's branch no longer points at `trial.UserTip`, it advanced after the trial was built:
    return `BranchMoved` with a detail naming the tip the trial was built from, and attempt no merge.
    Never fall back to a real merge the way `MergePlanBranchIntoUserBranch` does when `--ff-only` fails.
    The gate authorized the trial's tree, and nothing else may land.
- **`DiscardTrialDelivery`.** `git update-ref -d` on the trial ref, ignoring a missing ref, plus cleanup
  of any temporary worktree a failed create left behind.

Then replace task 30's throwing DEFAULT bodies on `IWorktreeProvider` with the in-process behaviour
every other member's default already models, so a Scheduler running on `FakeWorktreeProvider` or
`RecordingWorktreeProvider` reaches a waved delivery without throwing:

- `CreateTrialDelivery` returns a quiet-case trial whose `Commit` is `CurrentPlanBranchTip(integ)`.
- `PromoteTrialDelivery` returns `FastForwarded` — the same result the fake's
  `MergePlanBranchIntoUserBranch` hardcodes.
- `DiscardTrialDelivery` does nothing.

Do NOT change `MergePlanBranchIntoUserBranch`. The run-end delivery still uses it for every flat plan and
for the waves after the last delivery point.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/GitWorktreeProvider.cs`, `src/Guardrails.Core/Execution/IWorktreeProvider.cs`, and `src/Guardrails.Core/Execution/TrialDelivery.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
