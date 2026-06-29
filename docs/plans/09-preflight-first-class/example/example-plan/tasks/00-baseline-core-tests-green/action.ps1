# No-op preflight action. A precondition task does NO work — its guardrail establishes the
# baseline. In the Phase-2 first-class design the precondition guardrail runs in the pre-DAG
# preflight phase against the integration worktree at the user's HEAD, BEFORE any segment
# worktree is created (docs/plans/09-preflight-first-class.md §"The deferred Phase-2 first-class
# design"). It writes no state fragment and makes no commit (invariant 2).
exit 0
