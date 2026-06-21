# catches: a union that left git conflict markers in the merged bytes, or an empty/garbled
# greeting file — the deterministic verdict on EVERY union's bytes (never git's no-conflict
# signal, never an AI's say-so).
#
# This is the run's integration-guardrail set (scope:"integration"). The harness re-runs it
# on the merged bytes at EVERY union point (each non-FF integration / fan-in) AND on the final
# merged HEAD at the terminal integrationGate. It therefore MUST assert only invariants that
# hold at every union — including an intermediate union where only a SUBSET of tasks has
# integrated so far. It must NOT require a downstream artifact (e.g. greeting.txt) that a later
# task produces; that terminal-only assertion lives in this task's local guardrail (02-*).
#
# Env/cwd: the attempt-decoupled re-verifier sets cwd to the union/integration worktree and does
# NOT set $GUARDRAILS_WORKSPACE. Resolve robustly: prefer $GUARDRAILS_WORKSPACE, else use cwd.
$ErrorActionPreference = 'Stop'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$outDir = Join-Path $ws 'out'
if (-not (Test-Path $outDir)) {
    # No out/ yet at this union point is fine — nothing to verify.
    exit 0
}

foreach ($file in Get-ChildItem -Path $outDir -Filter '*.txt' -File) {
    $content = Get-Content -Raw -Path $file.FullName
    if ([string]::IsNullOrWhiteSpace($content)) {
        Write-Output ("out/" + $file.Name + " is empty on the merged bytes")
        exit 1
    }
    if ($content -match '<<<<<<<' -or $content -match '>>>>>>>' -or $content -match '=======') {
        Write-Output ("out/" + $file.Name + " contains git conflict markers — the union did not cleanly integrate")
        exit 1
    }
}

exit 0
