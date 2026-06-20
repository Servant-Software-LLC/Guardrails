# catches: "making the matcher tests pass" by editing the truth table / fuzz properties instead of
#          the matcher; reads the SHA-256 the harness recorded for
#          11-author-tests-writescope-matcher-truth-table-and-fuzz.captureHashes and recomputes with
#          Get-FileHash (interpreter cmdlet, no git). Weakening the 27-row table is the exact gaming
#          this prevents.
$state = Get-Content $env:GUARDRAILS_STATE_IN -Raw | ConvertFrom-Json
$storedHashes = $state.'11-author-tests-writescope-matcher-truth-table-and-fuzz'.fileHashes
if (-not $storedHashes) {
    Write-Output "State key '11-author-tests-writescope-matcher-truth-table-and-fuzz.fileHashes' missing - was captureHashes declared on the test-author task?"
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
