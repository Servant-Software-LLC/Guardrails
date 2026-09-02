# catches: a condition-8 silence control that is HAND-BUILT when a real one exists in git. An earlier
#          draft of this plan asserted condition 8 had zero exercises in the corpus and told the task to
#          construct a synthetic fixture. The run that implemented it found the exercise and falsified
#          the claim: at 5bd29da the witness is still absent from the SSOT while 14-land-ssot-schema-
#          deltas declares that exact path in its writeScope, so 544f7d5 -> 5bd29da is a RECOVERED
#          fires/silent pair on ONE artifact - same script, same witness, same path, differing only in
#          whether a task owns the file. A constructed fixture in place of that understates real
#          evidence, and a constructed one wearing the Recovered label is the lie section 3.4 caught.
#          Both directions are wrong; this guardrail checks the one that is now possible.
#
# Required-present baseline (#478), measured against this subject at author time: the required test name
#          occurs 0 times and 5bd29da occurs 0 times - the file does not exist yet. Both expected 0.
#          The forbidden Constructed_ name is a fail-on-present clause and is exempt from measurement.
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'tests/Guardrails.Core.Tests/ProducerCoverageTests.cs' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist. Every clause below would crash without it.')
    exit 1
}

$raw = Get-Content -LiteralPath $subject -Raw
$failures = New-Object System.Collections.Generic.List[string]

# REQUIRED: the condition-8 control is the recovered one.
if ($raw -notmatch [regex]::Escape('Recovered_Silent_WhenThePathIsCoveredByATaskWriteScope')) {
    $failures.Add('THE RECOVERED CONDITION-8 CONTROL IS MISSING from ' + $subject + '. Condition 8 - no task declares the path - is exercised for real at 5bd29da, so its silence control is recovered, not built. Without it, an implementation that hard-codes covered=false passes every other test in this file.')
}

# REQUIRED: it reads the real commit, which is what makes it recovered rather than a fixture with a name.
if ($raw -notmatch '5bd29da') {
    $failures.Add('THE SILENCE CONTROL DOES NOT READ 5bd29da in ' + $subject + '. A test named Recovered_ that does not read the commit it claims to recover is a constructed fixture wearing the wrong label - the exact fault this plan exists to prevent, pointed the other way. Read it from git, as tests 1 and 2 do.')
}

# FORBIDDEN: the withdrawn constructed label.
if ($raw -match 'Constructed_Silent_WhenThePathIsCovered') {
    $failures.Add('THE CONDITION-8 CONTROL IS STILL LABELLED Constructed_ in ' + $subject + '. That label was correct only while the corpus was believed to contain no exercise of condition 8. It does contain one - 5bd29da - so a constructed fixture here understates real evidence. Recover the pair instead.')
}

# The pair must be explained where the next reader meets it, not merely encoded in two commit hashes.
if ($raw -notmatch '544f7d5') {
    $failures.Add('THE PAIR IS INCOMPLETE in ' + $subject + ': 5bd29da appears but 544f7d5 does not. The two commits together are the evidence - the ONLY difference between them is whether a task owns the file - and either one alone proves far less.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The condition-8 control is not the recovered pair (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output 'The condition-8 silence control is RECOVERED: it reads 5bd29da, pairs with 544f7d5, and carries no constructed label.'
exit 0
