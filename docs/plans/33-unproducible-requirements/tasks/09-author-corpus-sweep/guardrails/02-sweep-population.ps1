# catches: a corpus sweep that repeats the measurement error it exists to correct. The hand-run sweep
#          enumerated plan folders carrying a top-level tasks/ directory and walked 533 of 850 scripts,
#          silently excluding five WAVED folders that nest tasks under wave-NN-*/tasks/ - including
#          model-tiering-stage-2, the ONE plan known to fire. A sweep rebuilt the same way reports a
#          reassuring zero over a population that structurally cannot contain the finding.
#
#          It also catches the second way this gate goes hollow: a blanket-zero expectation. A sweep that
#          expects zero everywhere cannot tell a working check from a mute one. The required NON-ZERO on
#          model-tiering-stage-2 at 1b8e681 is what proves the sweep can fail in the FIRING direction;
#          the HEAD row proves it can fail in the SILENCE direction. Section 11 prohibition 5.
#
# Required-present baseline (#478): all four literals occur 0 times at author time - the file does not
#          exist yet. Expected 0.
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'tests/Guardrails.Core.Tests/ProducerCoverageCorpusTests.cs' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist.')
    exit 1
}

$raw  = Get-Content -LiteralPath $subject -Raw
$scan = [regex]::Replace($raw, '(?m)^\s*///?.*$', '')
$scan = [regex]::Replace($scan, '(?s)/\*.*?\*/', '')

$failures = New-Object System.Collections.Generic.List[string]

# The waved layout must be enumerated, not just the flat one.
if ($scan -notmatch 'wave-') {
    $failures.Add('THE SWEEP DOES NOT ENUMERATE THE WAVED LAYOUT: no wave- path pattern appears in ' + $subject + ' outside a comment. Four plan folders nest their tasks under wave-NN-*/tasks/ and one of them carries the positive control; a fifth, 09-preflight-first-class, was excluded for a different reason (neither layout). A sweep that walks only the flat tasks/ layout covers 533 of 850 scripts and reports a zero it cannot have earned.')
}

# The positive control must be named, at its commit.
if ($raw -notmatch 'model-tiering-stage-2') {
    $failures.Add('THE POSITIVE CONTROL PLAN IS NOT NAMED in ' + $subject + '. model-tiering-stage-2 is the one plan in the corpus GR2060 fires on, and the expectation table must carry it explicitly.')
}
if ($raw -notmatch '1b8e681') {
    $failures.Add('THE PRE-RUN COMMIT IS NOT PINNED in ' + $subject + '. Each plan is evaluated at its OWN pre-run commit; against today HEAD every requirement is satisfied because the plans RAN, so a HEAD-only sweep is structurally incapable of failing.')
}

# The expectation must not be a blanket zero.
if ($scan -notmatch '\b1\b') {
    $failures.Add('THE EXPECTATION LOOKS LIKE A BLANKET ZERO in ' + $subject + ': no expected count of 1 appears. The required non-zero on model-tiering-stage-2 is what proves the sweep can fail in the firing direction. Section 11 prohibition 5 forbids flattening it to a tolerance or a blanket zero.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The corpus sweep does not cover its population (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output 'The corpus sweep enumerates both layouts, pins the positive control at its pre-run commit, and carries a non-blanket expectation.'
exit 0
