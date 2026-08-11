# catches: the test file omitting the negative assertion - without it a later change could inject a
#          write verb (restore/checkout/reset) and every test would still pass.
$f = 'tests/Guardrails.Core.Tests/ToolGrantInjectionTests.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f
$missing = @()
foreach ($verb in @('git restore', 'git checkout', 'git reset', 'git apply', 'git stash')) {
    if ($c -notmatch [regex]::Escape($verb)) { $missing += $verb }
}
if ($missing.Count -gt 0) {
    Write-Output ("the tests do not assert these write verbs are NEVER injected: " + ($missing -join ', ') + " - the read-only-only decision must be pinned, not assumed")
    exit 1
}
exit 0
