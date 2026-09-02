# catches: a knowledge-skill entry that records GR2060 shipping but not that GR2070 is HELD. The second
#          fact is the one that decays: a code absent from the knowledge skill is one the next design
#          re-proposes from scratch, spends, and only then discovers had been designed and declined for
#          want of a positive control. It also catches a stale next-free line - and the GR10xx/GR20xx
#          ladders advance independently, so a line stating only one of them is half a fact.
#
# DOCUMENTATION TARGET: exempt from the sample pair (#468); PRECEDENT substitute is the skill's own
#          existing GR-code entries, whose form these clauses mirror.
#
# Required-present baselines (#478), measured on master @67859c7 against this subject: GR2060 0,
#          GR2070 0, GR2071 0 - both honest, and both fire. But the GR2060 clause was NOT: the literal
#          measures 3 in this document, so a bare-token clause was GREEN ON ARRIVAL and could never fire,
#          certifying task 11's headline deliverable while the document still said GR2060 was RESERVED.
#          All three hits (L1241, L1373, L1420) make exactly that stale claim, so the clause below is now
#          a NEGATIVE assertion on the stale phrasing - the thing that actually has to change - rather
#          than a presence check on a token that was already there. Same for the held-marker clause: the
#          alternation declin|held|never fired measures 8 across this document and was never censused at
#          all, so it is now scoped to the GR2070 LINE.
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { '.claude/skills/guardrails-domain-knowledge/SKILL.md' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist.')
    exit 1
}

$doc = [regex]::Replace((Get-Content -LiteralPath $subject -Raw), '(?s)<!--.*?-->', '')
$failures = New-Object System.Collections.Generic.List[string]

if ($doc -match 'GR2060[^.
]{0,140}(?i)(reserved|not built|designed but)') {
    $failures.Add('THE KNOWLEDGE SKILL STILL CALLS GR2060 RESERVED in ' + $subject + '. It SHIPPED in task 4. This is a negative assertion on the stale phrasing rather than a presence check, because GR2060 already appears 3 times in this document - a bare presence clause was green before task 11 started and certified nothing. Update those statements and add the producer-coverage invariant: a guardrail may only require content some task in the plan can actually produce.')
}
if ($doc -notmatch 'GR2070') {
    $failures.Add('GR2070 IS NOT RECORDED AS HELD in ' + $subject + '. This is the fact most likely to be lost: it was DESIGNED AND DECLINED because it has never fired on a real defect at any commit in this repository. A code absent from the knowledge skill is one the next design re-proposes from scratch.')
} else {
    $g70 = [regex]::Match($doc, '(?m)^.*GR2070.*$')
    if (-not $g70.Success -or $g70.Value -notmatch '(?i)declin|held|never fired') {
        $failures.Add('GR2070 IS MENTIONED BUT NOT MARKED HELD in ' + $subject + '. A bare mention reads as an available code. Say it was declined ON THAT LINE, and give the bar for revisiting it: a defect, at a commit. Scoped to the GR2070 line deliberately: the words declined/held/never-fired occur 8 times elsewhere in this document, so a whole-document alternation is satisfied the instant GR2070 appears anywhere and can never fire.')
    }
}
if ($doc -notmatch 'GR2071') {
    $failures.Add('THE NEXT-FREE CODE IS NOT UPDATED to GR2071 in ' + $subject + '. The GR10xx and GR20xx ladders advance independently, so state which ladder this is - a line naming only one of them is half a fact, which is how this skill once claimed a pair of codes long after both were taken.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The knowledge skill does not record the ladder change (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output 'The knowledge skill records GR2060 and the producer-coverage invariant, GR2070 as held-not-allocated, and next-free GR2071.'
exit 0
