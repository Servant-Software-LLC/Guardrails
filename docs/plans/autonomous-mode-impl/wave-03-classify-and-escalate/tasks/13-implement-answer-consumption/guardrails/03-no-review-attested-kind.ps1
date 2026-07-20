# catches: an AnswerFile.cs that (re)introduces a review-attested answer kind — FORBIDDEN in v1 (doc 12
#          §7.4/§7.5, Blocker 2 / #366): the review gate is NEVER resolvable by an answer file. A #176
#          NEGATIVE assertion (fail-on-present), scoped to the one file this task owns. (GR2026 stays quiet
#          on a fail-on-present keyword — post-#177 it flags only positive require-present coverage tokens.)
$file = "src/Guardrails.Core/Execution/AnswerFile.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist"
    exit 1
}
$c = Get-Content -Raw -Path $file
if ($c -match '(?i)review.?attested') {
    Write-Output "$file contains a 'review-attested' answer kind — FORBIDDEN in v1 (doc 12 §7.5 / #366): an answer file must never resolve the review gate. Remove it."
    exit 1
}
exit 0
