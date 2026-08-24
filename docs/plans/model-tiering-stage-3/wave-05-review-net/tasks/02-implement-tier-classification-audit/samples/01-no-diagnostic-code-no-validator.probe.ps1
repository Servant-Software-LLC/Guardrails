# The author-time two-sided proof for guardrails/01-no-diagnostic-code-no-validator.ps1 (#302/#468).
#
# It runs the REAL guardrail script - not a copy of its regexes - by building a throwaway workspace whose
# tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs is the sample under test, then
# invoking the guardrail with that workspace as the working directory. A probe that re-implemented the
# clause list could go green while the shipped script was broken, which is the shape all of this exists to
# remove.
#
# Six cases:
#   valid            -> exit 0   (every banned token present, but only in comments - proves the strip works)
#   invalid          -> exit 1   (the committed defect: a code cited in a finding MESSAGE, not a comment)
#   mutant x4        -> exit 1   (the valid sample with ONE ban moved into a code position, per clause, so
#                                 no clause can be dead while its siblings carry the exit code)
#   missing subject  -> exit 1   (the precondition path)
#
# Read-only against the repo: everything is built under %TEMP% and removed in the finally block.
#
# Run it from the plan branch or any checkout:
#   pwsh -NoProfile -File docs/plans/model-tiering-stage-3/wave-05-review-net/tasks/02-implement-tier-classification-audit/samples/01-no-diagnostic-code-no-validator.probe.ps1
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$guardrail = Join-Path (Split-Path -Parent $here) 'guardrails/01-no-diagnostic-code-no-validator.ps1'
$validSample = Join-Path $here '01-no-diagnostic-code-no-validator.valid.cs'
$invalidSample = Join-Path $here '01-no-diagnostic-code-no-validator.invalid.cs'

foreach ($required in @($guardrail, $validSample, $invalidSample)) {
    if (-not (Test-Path $required -PathType Leaf)) {
        Write-Output "PROBE PRECONDITION FAILED: $required is missing"
        exit 1
    }
}

$valid = Get-Content -Raw -Path $validSample

# One mutant per clause. Each moves a banned token from a comment into a CODE position by appending a
# single line - the smallest edit that makes the clause the only difference from the valid sample.
$mutants = @(
    @('code literal in a message', '        _ = "GR2051 is a warning, not this";'),
    @('member access on the registry', '        _ = DiagnosticCodes.TieringInert;'),
    @('a Diagnostic constructed', '        _ = new Diagnostic();'),
    @('a PlanValidator constructed', '        _ = new PlanValidator(probe);')
)

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-w5-probe-" + [guid]::NewGuid().ToString('N'))
$results = @()

function Invoke-Guardrail {
    param([string]$Workspace, [string]$Content, [switch]$OmitSubject)

    $target = Join-Path $Workspace 'tests/Guardrails.Core.Tests/ModelTiering'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    if (-not $OmitSubject) {
        Set-Content -Path (Join-Path $target 'TierClassificationAudit.cs') -Value $Content -NoNewline
    }

    Push-Location $Workspace
    try {
        & $guardrail *>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}

try {
    $i = 0

    $ws = Join-Path $root ("case-" + $i++)
    $results += @{ Name = 'valid'; Expected = 0; Actual = (Invoke-Guardrail -Workspace $ws -Content $valid) }

    $ws = Join-Path $root ("case-" + $i++)
    $results += @{ Name = 'invalid (code cited in a message)'; Expected = 1
                   Actual = (Invoke-Guardrail -Workspace $ws -Content (Get-Content -Raw -Path $invalidSample)) }

    foreach ($m in $mutants) {
        $ws = Join-Path $root ("case-" + $i++)
        # Appended INSIDE the class body: the last closing brace of the type declarations is not needed -
        # this is never compiled, only scanned - so a plain append is enough to put the token in a
        # non-comment position.
        $results += @{ Name = "mutant: $($m[0])"; Expected = 1
                       Actual = (Invoke-Guardrail -Workspace $ws -Content ($valid + "`n" + $m[1] + "`n")) }
    }

    $ws = Join-Path $root ("case-" + $i++)
    $results += @{ Name = 'precondition (subject file missing)'; Expected = 1
                   Actual = (Invoke-Guardrail -Workspace $ws -Content '' -OmitSubject) }
}
finally {
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

$bad = @($results | Where-Object { $_.Expected -ne $_.Actual })
foreach ($r in $results) {
    $verdict = if ($r.Expected -eq $r.Actual) { 'ok  ' } else { 'FAIL' }
    Write-Output ("{0}  expected {1}, got {2}  <- {3}" -f $verdict, $r.Expected, $r.Actual, $r.Name)
}

if ($bad.Count -gt 0) {
    Write-Output ""
    Write-Output "$($bad.Count) of $($results.Count) case(s) behaved wrongly. A mutant that exits 0 means that clause is DEAD - it can never fire, however far the real defect goes. A valid sample that exits 1 means the guardrail false-REDs a correct implementation, which dead-ends every attempt at needs-human."
    exit 1
}

Write-Output ""
Write-Output "all $($results.Count) case(s) behaved as specified"
exit 0
