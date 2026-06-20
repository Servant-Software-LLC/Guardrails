# catches: the skill update left the worked example still teaching the deleted triad - the
#          example-breakdown reference must show writeScope and must NOT present captureHashes /
#          restoreOnRetry as the emitted task.json mechanism. Scoped to the one reference file.
$ex = ".claude/skills/plan-breakdown/references/example-breakdown.md"
if (-not (Test-Path $ex)) {
    Write-Output "$ex does not exist"
    exit 1
}
$text = Get-Content $ex -Raw
if ($text -notmatch 'writeScope') {
    Write-Output "example-breakdown.md does not show writeScope - the worked example was not switched to the new mechanism"
    exit 1
}
if ($text -match 'captureHashes' -or $text -match 'restoreOnRetry') {
    Write-Output "example-breakdown.md still references the deleted triad (captureHashes/restoreOnRetry) - the worked example must use writeScope instead"
    exit 1
}
exit 0
