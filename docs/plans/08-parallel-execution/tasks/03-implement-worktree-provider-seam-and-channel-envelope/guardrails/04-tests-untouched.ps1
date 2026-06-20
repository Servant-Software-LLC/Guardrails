# catches: "making tests pass" by editing the tests instead of the implementation;
#          reads the SHA-256 hashes the harness recorded for the upstream test-author task
#          (02-author-tests-worktree-provider-seam.captureHashes) and recomputes with Get-FileHash.
#          Get-FileHash runs via the interpreter, not the agent sandbox, so it always works (no git).
$state = Get-Content $env:GUARDRAILS_STATE_IN -Raw | ConvertFrom-Json
$storedHashes = $state.'02-author-tests-worktree-provider-seam'.fileHashes
if (-not $storedHashes) {
    Write-Output "State key '02-author-tests-worktree-provider-seam.fileHashes' missing - was captureHashes declared on the test-author task?"
    exit 1
}
$failures = @()
foreach ($file in $storedHashes.PSObject.Properties.Name) {
    $stored = $storedHashes.$file
    if (-not (Test-Path $file)) { $failures += "$file was deleted by the implementation task"; continue }
    $current = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash
    if ($current -ne $stored) { $failures += "$file was modified (expected $stored, got $current)" }
}
if ($failures) {
    Write-Output ($failures -join "; ")
    exit 1
}
exit 0
