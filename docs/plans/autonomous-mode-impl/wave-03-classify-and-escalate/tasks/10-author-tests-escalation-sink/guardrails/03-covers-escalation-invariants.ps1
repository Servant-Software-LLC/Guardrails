# catches: an EscalationSinkTests file that drops the monotonic/never-reused seq (the answer-injection
#          replay defence, doc 12 §7.1) or the decisions[]-escalated record. LOWER BOUND, scoped to the file.
$test = "tests/Guardrails.Core.Tests/EscalationSinkTests.cs"
if (-not (Test-Path $test)) { Write-Output "$test does not exist"; exit 1 }
$c = Get-Content -Raw -Path $test
$missing = @()
if ($c -notmatch '(?i)seq')                                { $missing += 'seq' }
if ($c -notmatch '(?i)monoton|reused|increasing|counter')  { $missing += 'seq-monotonic/never-reused' }
if ($c -notmatch [regex]::Escape('escalated'))             { $missing += 'decisions-escalated' }
if ($c -notmatch '(?i)escalations')                        { $missing += 'escalations-record' }
if ($missing.Count -gt 0) { Write-Output ("EscalationSinkTests missing: " + ($missing -join ', ')); exit 1 }
exit 0
