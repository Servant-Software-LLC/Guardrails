# catches: a combine action that produced out/greeting.txt without actually merging BOTH leaves
# (e.g. dropped one input, or wrote a placeholder). This is a LOCAL guardrail — it runs in-attempt
# on this task's OWN segment worktree (which already contains both leaves' files AND greeting.txt),
# so it can assert the terminal combine postcondition that is NOT yet true at an intermediate union.
#
# Env/cwd: in-attempt the harness sets $GUARDRAILS_WORKSPACE to this task's segment worktree and
# cwd to the plan workspace; resolve robustly (prefer the env var, else cwd).
$ErrorActionPreference = 'Stop'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$path = Join-Path $ws 'out/greeting.txt'
if (-not (Test-Path $path)) {
    Write-Output "out/greeting.txt missing — the combine action did not produce the fan-in output"
    exit 1
}

$greeting = Get-Content -Raw -Path $path
if (($greeting -notmatch 'Hello') -or ($greeting -notmatch 'World')) {
    Write-Output "out/greeting.txt must combine BOTH leaves (expected 'Hello' and 'World'); got: $greeting"
    exit 1
}

exit 0
