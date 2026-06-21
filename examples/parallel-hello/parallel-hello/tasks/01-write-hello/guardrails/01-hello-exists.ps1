# catches: an action that exits 0 but never wrote out/hello.txt into its segment worktree
# (so the segment would have no committable change and the plan-branch commit would be empty).
#
# WORKSPACE RESOLUTION: this guardrail runs in TWO contexts with DIFFERENT env/cwd contracts:
#   - in-attempt: cwd = the plan workspace, and $GUARDRAILS_WORKSPACE points at the segment worktree;
#   - union re-verify (a non-FF integration / fan-in): cwd = the union worktree, and
#     $GUARDRAILS_WORKSPACE is NOT set (the attempt-decoupled re-verifier deliberately omits it).
# Resolve robustly: prefer $GUARDRAILS_WORKSPACE when set, else fall back to the current directory.
$ErrorActionPreference = 'Stop'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$path = Join-Path $ws 'out/hello.txt'
if (-not (Test-Path $path)) {
    Write-Output "out/hello.txt missing in the worktree ($path)"
    exit 1
}

$content = Get-Content -Raw -Path $path
if ($content -notmatch 'Hello') {
    Write-Output "out/hello.txt does not contain the expected greeting 'Hello'"
    exit 1
}

exit 0
