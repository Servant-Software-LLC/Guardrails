# catches: a regression suite that drops one of the four enumerated behaviours. Test 2 (the finding is
#          still REPORTED) and test 3 (a COMPLETE plan is still blocked) are the two most likely to be
#          skipped, and they are the two that keep the mitigation honest: without test 2 a fix that
#          makes the error VANISH passes; without test 3 the excuse can leak to complete plans and
#          GR2060 stops meaning anything.
#
# Required-present baseline (#478): all four literals occur 0 times at author time - the file does not
#          exist yet. Expected 0.
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist.')
    exit 1
}

$raw  = Get-Content -LiteralPath $subject -Raw
$scan = [regex]::Replace($raw, '(?m)^\s*///?.*$', '')
$scan = [regex]::Replace($scan, '(?s)/\*.*?\*/', '')

$required = @(
    'PartialPrefix_TrippingGr2060_IsNotReverted',
    'PartialPrefix_TrippingGr2060_StillReportsTheFinding',
    'CompletePlan_TrippingGr2060_IsStillBlocked',
    'PlainValidate_OnAPartialPrefix_StillErrors'
)

$failures = New-Object System.Collections.Generic.List[string]
foreach ($m in $required) {
    if ($scan -notmatch [regex]::Escape($m)) {
        $failures.Add('MISSING BEHAVIOUR: no test method named ' + $m + '. All four names are pinned in the prompt and are the contract this guardrail enforces.')
    }
}

$attrs = ([regex]::Matches($scan, '\[\s*(?:Fact|Theory)\b')).Count
if ($attrs -lt $required.Count) {
    $failures.Add('TOO FEW xUnit ATTRIBUTES: found ' + $attrs + ' for ' + $required.Count + ' required behaviours. A method without one is never executed.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== #501 regression behaviours not covered (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output ('All ' + $required.Count + ' #501 regression behaviours are present, each with an xUnit attribute.')
exit 0
