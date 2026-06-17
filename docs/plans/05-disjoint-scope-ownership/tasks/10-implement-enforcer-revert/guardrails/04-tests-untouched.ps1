# catches: "making the tests pass" by editing the M5 revert test file instead of the
#          implementation. Recomputes Get-FileHash and compares to the SHA-256 the harness recorded
#          for the upstream test-author task's captureHashes (pwsh cmdlet, runs via the interpreter,
#          no git/allowedTools gate; contract-protected by single-writer-per-key, SSOT §6.2).
$stateIn = $env:GUARDRAILS_STATE_IN
if (-not $stateIn -or -not (Test-Path $stateIn)) {
    Write-Output "GUARDRAILS_STATE_IN not set or missing - cannot verify test file integrity."
    exit 1
}
$state = Get-Content $stateIn -Raw | ConvertFrom-Json
$storedHashes = $state.'09-author-enforcer-revert-tests'.fileHashes
if (-not $storedHashes) {
    Write-Output "State key '09-author-enforcer-revert-tests.fileHashes' missing - was captureHashes declared on the test-author task?"
    exit 1
}
$failures = @()
foreach ($file in $storedHashes.PSObject.Properties.Name) {
    $stored = $storedHashes.$file
    if (-not (Test-Path -LiteralPath $file)) {
        $failures += "$file was deleted by the implementation task"
        continue
    }
    $current = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash
    if ($current -ne $stored) {
        $failures += "$file was modified by the implementation task (expected $stored, got $current)"
    }
}
if ($failures) {
    Write-Output ($failures -join "; ")
    exit 1
}
exit 0
