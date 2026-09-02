# catches: a test file that drops one of the ten enumerated behaviours, or names it something else so a
#          later filter or reviewer cannot find it. The prompt PINS all ten method names; this is the
#          agreement half of #455 - a pinned name in the prompt is worth nothing if nothing checks it.
#
# WHAT THIS DOES NOT PROVE, stated rather than implied (#375's boundary): the red here is a COMPILE
#          failure, so there is no per-test result file and no per-test census is possible. This clause
#          proves each behaviour is PRESENT and carries a real xUnit attribute; it CANNOT prove the body
#          asserts anything. A correctly-named method with Assert.True(true) passes this, and is caught
#          only by task 4's forward run and by human review of the draft.
#
# Required-present baseline (#478): all ten required literals occur 0 times in the subject at author
#          time - the file does not exist yet. Expected 0.
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'tests/Guardrails.Core.Tests/ProducerCoverageTests.cs' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist. Every clause below would crash without it.')
    exit 1
}

$raw  = Get-Content -LiteralPath $subject -Raw
# Blank comments so a TODO listing the method names cannot satisfy the presence clauses.
$scan = [regex]::Replace($raw, '(?m)^\s*///?.*$', '')
$scan = [regex]::Replace($scan, '(?s)/\*.*?\*/', '')

$required = @(
    'Fires_OnRecoveredPositiveControl_NamingTierSourceAndTheSsotPath',
    'Recovered_Silent_OnTheSameScript_AtTodaysCommit',
    'Extracts_OneHopAssociation_TestPathThenGetContentShape',
    'Extracts_DoubleQuotedPathOperand_WithNoDollarAndNoBacktick',
    'Recovered_Silent_WhenThePathIsCoveredByATaskWriteScope',
    'Silent_WhenTheWitnessIsPresentInTheFile',
    'Silent_WhenTheFileIsNotGitTracked',
    'Silent_WhenTheProbeAnswersNotKnown',
    'Silent_WhenThePathIsUnderThePlanFolder',
    'Silent_WhenPlanIsNotClosed'
)

$failures = New-Object System.Collections.Generic.List[string]

foreach ($m in $required) {
    if ($scan -notmatch [regex]::Escape($m)) {
        $failures.Add('MISSING BEHAVIOUR: no test method named ' + $m + '. The prompt pins all ten names; they are the contract this guardrail enforces, not a naming suggestion.')
    }
}

# A real xUnit attribute must be present - a file of plain methods compiles and runs nothing.
$attrs = ([regex]::Matches($scan, '\[\s*(?:Fact|Theory)\b')).Count
if ($attrs -lt $required.Count) {
    $failures.Add('TOO FEW xUnit ATTRIBUTES: found ' + $attrs + ' Fact/Theory attributes for ' + $required.Count + ' required behaviours. A method without one is never executed, so the behaviour is named but not tested.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== Enumerated behaviours not covered (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output ('All ' + $required.Count + ' enumerated behaviours are present, each with an xUnit attribute (' + $attrs + ' found).')
exit 0
