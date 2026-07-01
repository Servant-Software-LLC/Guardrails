# No-op baseline action. A Bucket-A baseline task does NO work — its guardrail IS the work
# (it establishes the "this area starts from green" baseline). This is doctrine that ships and
# validates today: a no-op-action ROOT task carrying a normal scope:"local" guardrail, that
# every modifier in the area `dependsOn` (docs/plans/09-preflight-first-class.md §"Bucket A").
# It writes no state fragment and makes no commit (invariant 2).
exit 0
