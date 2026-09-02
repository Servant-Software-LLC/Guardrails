# catches: a condition-8 silence control that CLAIMS to be recovered without reading anything. An
#          adversarial pass broke the first version of this guardrail with a file of ten correctly-named
#          methods, every body Assert.True(true), ZERO git access, and both commit hashes sitting in a
#          // comment - it scanned raw text, so prose satisfied it, and the amended prompt separately
#          tells the agent to write exactly such a comment. Following the comment instruction alone
#          passed. That is an echo-judge in deterministic clothing: it read the label, not the evidence.
#
#          Three changes close it. Comments are BLANKED before scanning, as guardrail 02 already does.
#          The hashes are required in the `<sha>:<path>` COLON FORM, which is the git-read shape the
#          prompt mandates and which prose has no reason to contain. And the MANIFEST side is required
#          - condition-8 silence is a claim about the writeScope union, so the test must read task 14's
#          task.json at 5bd29da, the one string a constructed fixture would never carry.
#
#          STRING LITERALS ARE NOT STRIPPED, deliberately: the evidence lives inside them.
#
# Required-present baseline (#478), measured against this subject at author time: all four required
#          literals occur 0 times - the file does not exist yet. All expected 0. The forbidden
#          Constructed_ name is a fail-on-present clause and is exempt.
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'tests/Guardrails.Core.Tests/ProducerCoverageTests.cs' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist. Every clause below would crash without it.')
    exit 1
}

$raw = Get-Content -LiteralPath $subject -Raw
# Blank comments so a comment naming the commits cannot satisfy an evidence clause. NOT string literals.
$scan = [regex]::Replace($raw, '(?m)^\s*///?.*$', '')
$scan = [regex]::Replace($scan, '(?m)//.*$', '')
$scan = [regex]::Replace($scan, '(?s)/\*.*?\*/', '')

$failures = New-Object System.Collections.Generic.List[string]

if ($scan -notmatch [regex]::Escape('Recovered_Silent_WhenThePathIsCoveredByATaskWriteScope')) {
    $failures.Add('THE RECOVERED CONDITION-8 CONTROL IS MISSING from ' + $subject + ' (outside comments). Condition 8 - no task declares the path - is exercised for real at 5bd29da, so its silence control is recovered, not built. Without it, an implementation that hard-codes covered=false passes every other test in this file.')
}

# EVIDENCE, not label: the git-read colon form of both commits.
if ($scan -notmatch [regex]::Escape('544f7d5:docs/plans/model-tiering-stage-2/')) {
    $failures.Add('THE FIRING HALF IS NOT READ FROM GIT in ' + $subject + ': no `544f7d5:docs/plans/model-tiering-stage-2/...` read outside a comment. A hash mentioned in prose is not a recovered control - read the bytes, as the prompt requires.')
}
if ($scan -notmatch [regex]::Escape('5bd29da:docs/plans/model-tiering-stage-2/')) {
    $failures.Add('THE SILENCE HALF IS NOT READ FROM GIT in ' + $subject + ': no `5bd29da:docs/plans/model-tiering-stage-2/...` read outside a comment. This clause exists because a file naming both commits ONLY in a comment previously passed this guardrail while asserting nothing and touching no git.')
}

# The MANIFEST side. Condition-8 silence is a claim about the writeScope union, not about the gate script.
if ($scan -notmatch '14-land-ssot-schema-deltas') {
    $failures.Add('THE MANIFEST SIDE OF THE PAIR IS NOT READ in ' + $subject + '. Condition 8 turns on whether a TASK OWNS THE PATH, so the silence half must read 5bd29da''s wave-02 task 14-land-ssot-schema-deltas/task.json, whose writeScope is exactly ["docs/plans/02-schemas-and-contracts.md"]. Reading only the gate script proves the witness is absent at both commits and says nothing about the thing that actually changed between them.')
}

if ($scan -match 'Constructed_Silent_WhenThePathIsCovered') {
    $failures.Add('THE CONDITION-8 CONTROL IS STILL LABELLED Constructed_ in ' + $subject + '. That label was correct only while the corpus was believed to contain no exercise of condition 8. It does contain one - 5bd29da - so a constructed fixture here understates real evidence.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The condition-8 control is not the recovered pair (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ''
    Write-Output 'IF THE PAIR GENUINELY CANNOT BE RECOVERED here - a shallow clone, git unavailable in this sandbox - escalate with needsHuman naming what you could not read. Do NOT supply the Recovered_ name without the evidence behind it: section 11 prohibition 6 forbids choosing a label to satisfy a guardrail, and this guardrail requires that name.'
    exit 1
}

Write-Output 'The condition-8 silence control is RECOVERED: it reads both commits in git colon form, reads task 14''s manifest at 5bd29da, and carries no constructed label.'
exit 0
