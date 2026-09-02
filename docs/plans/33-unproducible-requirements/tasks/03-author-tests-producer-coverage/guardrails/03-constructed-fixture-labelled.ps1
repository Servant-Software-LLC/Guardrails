# catches: the condition-8 silence fixture relabelled Recovered to match its neighbours. That rename is
#          prohibition 6 of the plan's section 11, and it tells exactly the lie this plan was rewritten
#          to remove: a RECOVERED control is read from git and proves the check fires on a real defect;
#          a CONSTRUCTED one is hand-built and proves a condition SUPPRESSES. Condition 8 has zero
#          exercises in all 850 committed scripts, so its fixture MUST be constructed - and say so.
#
# Required-present baseline (#478): the required literal occurs 0 times at author time (the file does
#          not exist). The forbidden literal is a fail-on-present clause and is exempt from measurement
#          - a ban that is green on arrival is a correct ban.
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'tests/Guardrails.Core.Tests/ProducerCoverageTests.cs' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist. Every clause below would crash without it.')
    exit 1
}

$raw = Get-Content -LiteralPath $subject -Raw
$failures = New-Object System.Collections.Generic.List[string]

# REQUIRED: the constructed fixture keeps its honest name.
if ($raw -notmatch [regex]::Escape('Constructed_Silent_WhenThePathIsCoveredByATaskWriteScope')) {
    $failures.Add('THE CONSTRUCTED FIXTURE IS MISSING OR RENAMED: no method named Constructed_Silent_WhenThePathIsCoveredByATaskWriteScope in ' + $subject + '. Condition 8 has zero exercises in the corpus, so without this test an implementation that hard-codes covered=false passes every other test in the file.')
}

# FORBIDDEN: the same fixture wearing the Recovered label.
if ($raw -match 'Recovered_Silent_WhenThePathIsCovered') {
    $failures.Add('THE CONSTRUCTED FIXTURE HAS BEEN RELABELLED Recovered_ in ' + $subject + '. A recovered control is read from git and proves the check FIRES on a real defect; this one is hand-built and proves a condition SUPPRESSES. Section 11 prohibition 6 forbids this rename by name.')
}

# The distinction must be written down where the next reader meets it, not merely encoded in a name.
if ($raw -notmatch '(?i)construct') {
    $failures.Add('THE CONSTRUCTED/RECOVERED DISTINCTION IS NOT EXPLAINED in ' + $subject + '. Say in a comment on that test that it is constructed, and why that is legitimate for a SILENCE claim when it would not be for a FIRING one.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The constructed silence fixture is not honestly labelled (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output 'The condition-8 silence fixture is present, named Constructed_, and its rationale is written down.'
exit 0
