# catches: the DoR 6.3 open question being ANSWERED IN CODE but never published, so the SSOT task
#          (14) - which documents the answer in the new 9.6 - has to re-derive it by reading the
#          classifier diff, or worse, guesses. The question is an explicit deliverable of this wave;
#          an answer that exists only as a regex is an answer nobody can cite.
# STATE-OUTPUT guardrail: reads GUARDRAILS_STATE_FRAGMENT, parses it, and asserts this task's own
# key carries the three fields task 14 consumes. The fragment is keyed by the WAVE-QUALIFIED task id
# (SSOT 14.3) - a bare folder name is rejected by the harness as foreign on every attempt.
$ErrorActionPreference = 'Continue'
$key = 'wave-02-attempt-launch-wiring/04-implement-unavailability-classification'

$path = $env:GUARDRAILS_STATE_FRAGMENT
if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path $path)) {
    Write-Output "no state fragment was written - this task must publish its 6.3 answer to GUARDRAILS_STATE_OUT as { `"$key`": { `"alreadyCovered`": [...], `"added`": [...], `"newEnumMember`": false } }"
    exit 1
}

try {
    $fragment = Get-Content -Raw $path | ConvertFrom-Json
} catch {
    Write-Output "the state fragment at $path is not parseable JSON: $($_.Exception.Message)"
    exit 1
}

$mine = $fragment.$key
if ($null -eq $mine) {
    $actual = ($fragment.PSObject.Properties.Name -join ', ')
    Write-Output "the state fragment has no top-level key '$key' (found: $actual). In a WAVED plan the single top-level key is the WAVE-QUALIFIED task id - not the stableId, and not the bare folder name; the harness rejects any other key as foreign on EVERY attempt."
    exit 1
}

$failures = @()
if ($null -eq $mine.alreadyCovered) { $failures += "'alreadyCovered' is missing - name the shapes the shipped quarantine ALREADY classified Transient (an empty array is a real answer; absent is not)" }
if ($null -eq $mine.added)          { $failures += "'added' is missing - name the shapes this task had to add (an empty array is a real answer meaning 'no extension was needed'; absent is not)" }
if ($null -eq $mine.newEnumMember)  { $failures += "'newEnumMember' is missing - 6.3 forbids a new probe enum in v1, so this must be present and false" }
elseif ($mine.newEnumMember -ne $false) {
    $failures += "'newEnumMember' is not false - DoR 6.3 states plainly that no new probe enum is introduced in v1"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== 6.3 answer fragment: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
