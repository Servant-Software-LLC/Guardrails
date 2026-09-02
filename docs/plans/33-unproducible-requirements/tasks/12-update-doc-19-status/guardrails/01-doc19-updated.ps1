# catches: a doc-19 edit that either leaves Milestone A reading NOT BUILT (so the next reader re-designs
#          a shipped diagnostic) or rewrites D2 as a RETRACTION. The second is the subtle one and it is
#          why this guardrail exists rather than a plain presence check: the declined lint is evidence
#          FOR D2, not against it. A sentence that reads as a climb-down misrepresents the outcome and
#          would licence the next author to re-open a decision that held.
#
# DOCUMENTATION TARGET: exempt from the sample pair (#468); PRECEDENT substitute is the document's own
#          status-table idiom, which these clauses mirror rather than replace.
#
# Required-present baselines (#478), measured on master @67859c7 against this subject:
#          '33-unproducible-requirements'  0  - expected; this task writes the pointer
#          'designed and declined'         0  - expected; a bare 'declin' measures 2 and would be green
#                                               on arrival, which is why the clause pins the phrase
#          'NOT BUILT'                     1  - NONZERO, and this clause asserts it goes to 0 for the
#                                               Milestone A row specifically, so it is measured as the
#                                               thing being REMOVED rather than required
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'docs/plans/19-producer-coverage.md' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist.')
    exit 1
}

$doc = [regex]::Replace((Get-Content -LiteralPath $subject -Raw), '(?s)<!--.*?-->', '')
$failures = New-Object System.Collections.Generic.List[string]

# The Milestone A row must no longer read NOT BUILT.
# The HARNESS-half row specifically: doc 19 has two Milestone A rows and the FIRST is the skill half,
# which already reads SHIPPED. Matching plain 'Milestone A' selected the wrong row and the clause could
# never fire - found by RUNNING this guardrail, not by reading it (#478/#580).
$row = [regex]::Match($doc, '(?m)^.*Milestone A.*GR2060.*$')
if (-not $row.Success) {
    $failures.Add('THE MILESTONE A STATUS ROW IS MISSING from ' + $subject + '. It should point at this plan, not disappear.')
} elseif ($row.Value -match 'NOT BUILT') {
    $failures.Add('MILESTONE A STILL READS NOT BUILT in ' + $subject + '. GR2060 shipped in task 4; a status table that says otherwise is how a designed-and-built diagnostic gets designed a second time. The row line is: ' + $row.Value.Trim())
}

# The pointer at this plan.
if ($doc -notmatch '33-unproducible-requirements') {
    $failures.Add('NO POINTER AT THIS PLAN in ' + $subject + '. Both the status row and the D2 sentence must name docs/plans/33-unproducible-requirements.md so a reader of doc 19 can find what happened next.')
}

# D2 must be CORROBORATED, not retracted.
# 'declin' alone occurs 2 times in this document already (sections about a deliberately declined weaker
# check, and doc 18 declining GR2059), so a bare 'declin' clause is GREEN ON ARRIVAL and proves nothing.
# Measured 0 for the specific phrase this task must write.
if ($doc -notmatch '(?i)designed and declined') {
    $failures.Add('THE D2 SENTENCE IS MISSING from ' + $subject + '. D2 gains exactly one sentence recording that a lint for the derived-path shape was designed and DECLINED - the shape has never occurred in a form a lint could see.')
}
if ($doc -match '(?i)D2 (?:was|is) (?:wrong|incorrect|mistaken|superseded|reversed)') {
    $failures.Add('D2 HAS BEEN WRITTEN AS A RETRACTION in ' + $subject + '. D2 HELD. The declined lint is evidence FOR it: the shape has never occurred in a form a lint could see, so D2 is unchanged and now better evidenced. Do not soften or reverse it.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== Doc 19 was not updated correctly (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output 'Doc 19 points Milestone A at this plan and records the declined lint as corroboration of D2.'
exit 0
