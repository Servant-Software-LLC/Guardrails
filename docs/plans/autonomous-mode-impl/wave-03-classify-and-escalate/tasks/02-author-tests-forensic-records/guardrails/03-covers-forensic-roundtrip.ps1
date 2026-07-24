# catches: an AutonomyForensicRecordsTests file that tests only the throwing-stub red and drops the §6
#          forensic NON-LOSSY round-trip — the autonomy.jsonl detail record or the DecisionEntry
#          additive-fields/tokens round-trip. A LOWER BOUND, scoped to the one test file this task authors.
$test = "tests/Guardrails.Core.Tests/AutonomyForensicRecordsTests.cs"
if (-not (Test-Path $test)) { Write-Output "$test does not exist"; exit 1 }
$c = Get-Content -Raw -Path $test
$missing = @()
if ($c -notmatch [regex]::Escape('autonomy.jsonl'))  { $missing += 'autonomy.jsonl' }
if ($c -notmatch [regex]::Escape('answer-injected')) { $missing += 'decisions-token(answer-injected)' }
if ($c -notmatch '(?i)round.?trip|roundtrip')        { $missing += 'round-trip' }
if ($missing.Count -gt 0) {
    Write-Output ("AutonomyForensicRecordsTests missing: " + ($missing -join ', ') + " — the §6 non-lossy round-trip (autonomy.jsonl + the DecisionEntry additive round-trip) must be a named test")
    exit 1
}
exit 0
