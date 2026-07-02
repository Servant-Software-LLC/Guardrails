# catches: this task's action starting against an Acme.Payments.Core source that does NOT yet
# carry the RequestId threading task 04 was supposed to deliver — e.g. a merge that silently
# dropped 04's commit, or a scheduling defect that let 05 start before 04 actually landed. A
# task-level guardrail (which runs AFTER the action) would catch this only after burning a full
# retry attempt on work built against the wrong starting point; this precondition catches it
# BEFORE the action runs.
#
# WHAT THIS FOLDER IS: the task-level JIT dependency-delivery precondition (a sibling of
# `guardrails/`), keyed to this task's `04 -> 05` `dependsOn` edge. It runs in 05's OWN segment
# worktree at `taskBase`, BEFORE 05's action — gating entry to the attempt loop. It is NOT a
# guardrail (a guardrail runs AFTER the action and consumes an attempt); it verifies the state no
# pre-DAG phase could see (no producer has run yet at pre-DAG time).
#
# On failure: outcome `precondition-failed` -> `needs-human`, WITHOUT burning a retry attempt.
# Positive / monotone-safe under merges ("the symbol IS present" only becomes more true as merges
# land) — so it NEVER joins the integration/union/terminal set. Single-shot DETERMINISTIC
# byte-check — NO live probe, NO process start, NO poll, NO network, per the advisory live-probe
# guidance.
$ErrorActionPreference = 'Stop'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# In a real plan this would deterministically assert that task 04's RequestId threading is
# present in the inherited Acme.Payments.Core source at THIS task's taskBase, e.g.:
#   if (-not (Select-String -Path (Join-Path $ws 'Acme.Payments.Core/ChargeResult.cs') -Pattern 'RequestId' -Quiet)) {
#     Write-Output 'producer 04 did not deliver RequestId into ChargeResult at taskBase'
#     exit 2   # precondition-failed -> needs-human, no retry attempt burned
#   }
# A byte-check on the wired/committed source — NOT a live probe. SIMULATED here as a fixed pass —
# this is an illustrative sample plan, not wired to a real repo.
Write-Output "task 04 delivered RequestId into Acme.Payments.Core at taskBase (simulated)"
exit 0
