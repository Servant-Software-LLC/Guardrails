# ============================================================================================
# Bucket C (SIMULATED — DEFERRED #183): a per-task JIT dependency-delivery precondition.
#
# WHAT THIS FOLDER IS. This `preflights/` folder (a sibling of `guardrails/`) is the ONE
# genuinely first-class slice of the partition — and it is the ONLY simulated thing left in
# this example. Buckets A (00, 01) and B (02) are real doctrine and VALIDATE today; the loader
# IGNORES this `preflights/` folder entirely (it only enumerates `guardrails/`), so its
# presence does NOT affect `guardrails validate`. It exists to SHOW the Bucket-C shape, not to
# run.
#
# WHAT A BUCKET-C PRECONDITION DOES (docs/plans/09-preflight-first-class.md §"Bucket C"):
#   - Runs in 05's OWN segment worktree at `taskBase`, BEFORE 05's action — gating entry to the
#     attempt loop. It is NOT a guardrail (a guardrail runs AFTER the action and consumes an
#     attempt); it is a precondition that runs BEFORE the action.
#   - Verifies the PRODUCER (04) actually delivered the symbol 05 builds against. 05
#     `dependsOn ["04-implement-correlation", "01-baseline-api-endpoint-up"]`, so it builds
#     against the RequestId threading task 04 must deliver into Acme.Payments.Core. This check
#     asserts that threading is present in the bytes 05 INHERITED at taskBase, after 04 merged
#     in. This is the state NO pre-DAG phase could see (no producer has run at pre-DAG time).
#   - On failure: outcome `precondition-failed` -> `needs-human` -> exit 2, WITHOUT burning a
#     retry attempt (the no-burn property). exit 2 already means "actionable condition found;
#     work durable / unstarted" — no new exit code.
#   - Single-shot DETERMINISTIC byte-check — NO live probe, NO process start, NO poll, NO network
#     (the live-probe ban, lifted verbatim from the volume-control gate).
#   - Positive / monotone-safe under merges ("the symbol IS present" only becomes more true as
#     merges land) — so it NEVER joins the integration / union / terminal set.
#   - Keyed to the 04->05 `dependsOn` edge (consumer->producer): there is no modify-vs-create
#     judgment, just a concrete delivered artifact named by the dependency. The harness never
#     DERIVES this — the skill authors it against the edge; the human reviews it.
#
# OPTION SHAPE. This folder is the OPEN folder-vs-flag decision's OPTION 1 (a `<task>/preflights/`
# folder). OPTION 2 would instead mark 05's FIRST guardrail with a `task.json` no-burn flag and
# carry no separate folder. The design doc does NOT resolve which wins (it is decided at trigger
# time against a real captured instance) — see docs/plans/09-preflight-first-class.md
# §"The open decision".
# ============================================================================================
$ErrorActionPreference = 'Stop'

# In a real Bucket-C precondition this would deterministically assert that task 04's RequestId
# threading is present in the inherited Acme.Payments.Core source at THIS task's taskBase — e.g.:
#   if (-not (Select-String -Path 'Acme.Payments.Core/ChargeResult.cs' -Pattern 'RequestId' -Quiet)) {
#     Write-Output 'producer 04 did not deliver RequestId into ChargeResult at taskBase'
#     exit 2   # precondition-failed -> needs-human, no retry attempt burned
#   }
# A byte-check on the wired/committed source — NOT a live probe. SIMULATED here as a fixed pass.
Write-Output "Bucket C (simulated): task 04 delivered RequestId into Acme.Payments.Core at taskBase"
exit 0
