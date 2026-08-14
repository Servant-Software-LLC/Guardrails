# catches: a review that exists but is a skeleton — missing a required section, or quoting
#          a greeting that is not the one on disk
if (-not (Test-Path 'out/review.md')) {
    Write-Output 'out/review.md does not exist in the workspace'
    exit 1
}
$review = Get-Content 'out/review.md' -Raw
foreach ($required in @('# Greeting review', '## Greeting', '## Verdict')) {
    if ($review -notlike "*$required*") {
        Write-Output "out/review.md is missing required section '$required'"
        exit 1
    }
}
$greeting = (Get-Content 'out/greeting.txt' -Raw).Trim()
if ($review -notlike "*$greeting*") {
    Write-Output "out/review.md does not quote the greeting verbatim ('$greeting')"
    exit 1
}
exit 0
