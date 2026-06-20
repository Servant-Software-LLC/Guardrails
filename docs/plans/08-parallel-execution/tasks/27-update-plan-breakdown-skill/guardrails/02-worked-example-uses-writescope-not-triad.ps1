# catches: the skill update left the worked example OR the SKILL.md procedure still teaching the deleted
#          triad. Both the example-breakdown reference AND SKILL.md must show writeScope and must NOT
#          present captureHashes / restoreOnRetry / tests-untouched as the emitted task.json mechanism
#          (the plan tears the triad out of the harness, so the skill that teaches it must not keep
#          doctrine describing it). 2nd review hardening:
#            - the triad-absence check now ALSO covers SKILL.md (not just example-breakdown.md);
#            - tests-untouched is added to the forbidden triad set (was only captureHashes/restoreOnRetry);
#            - writeScope must appear as an EMITTED FIELD ("writeScope": ...) inside a fenced json/jsonc
#              block in the worked example, not as bare prose (prose mentioning the word would otherwise
#              satisfy a loose grep while the emitted task.json still showed the triad).
#          Scoped to the two skill files this task owns.
$ex    = ".claude/skills/plan-breakdown/references/example-breakdown.md"
$skill = ".claude/skills/plan-breakdown/SKILL.md"
foreach ($f in @($ex, $skill)) {
    if (-not (Test-Path $f)) {
        Write-Output "$f does not exist"
        exit 1
    }
}

# (1) Triad-absence across BOTH files.
$forbidden = @('captureHashes', 'restoreOnRetry', 'tests-untouched')
foreach ($f in @($ex, $skill)) {
    $text = Get-Content $f -Raw
    $hits = @()
    foreach ($t in $forbidden) {
        if ($text -match [regex]::Escape($t)) { $hits += $t }
    }
    if ($hits.Count -gt 0) {
        Write-Output "$f still references the deleted triad [$($hits -join ', ')] - the plan tears the captureHashes/restoreOnRetry/tests-untouched triad out of the harness; the skill must teach writeScope (test-author owns the test files; the implementation task's writeScope EXCLUDES them) instead, with no remaining triad doctrine."
        exit 1
    }
}

# (2) writeScope must appear at all in BOTH files (the new mechanism is taught).
foreach ($f in @($ex, $skill)) {
    if ((Get-Content $f -Raw) -notmatch 'writeScope') {
        Write-Output "$f does not mention writeScope - the worked example / procedure was not switched to the new mechanism"
        exit 1
    }
}

# (3) In the worked example, writeScope must be an EMITTED FIELD inside a fenced json/jsonc block
#     ("writeScope": ...), not bare prose. Extract ```json / ```jsonc fenced blocks and require the field.
$exText = Get-Content $ex -Raw
$blockMatches = [regex]::Matches($exText, '(?s)```jsonc?\b(.*?)```')
$emittedFieldFound = $false
foreach ($m in $blockMatches) {
    if ($m.Groups[1].Value -match '"writeScope"\s*:') { $emittedFieldFound = $true; break }
}
if (-not $emittedFieldFound) {
    Write-Output "$ex does not emit writeScope as a task.json FIELD - the worked example must show `"writeScope`": [...] inside a fenced json/jsonc task.json block (not just prose). The point of the example is to show the emitted mechanism; a prose mention while the emitted task.json still used the triad is exactly the gap."
    exit 1
}

exit 0
