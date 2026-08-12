# catches: the contract change landing in CODE but not in the SSOT. Prompt item 3 is inside this task's
#          writeScope but was otherwise wholly unverified - the task could skip it and go green, breaking
#          the repo rule that a contract change and its SSOT line land in the SAME change.
# token choice: 'runnable command' occurs ZERO times in the SSOT today and is named verbatim in this
#          task's action prompt, so it discriminates (unlike 'inject', which already occurs 25 times).
$f = 'docs/plans/02-schemas-and-contracts.md'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f
if ($c -notmatch '(?i)runnable command') {
    Write-Output "$f does not state the normative rule - the harness must never present a RUNNABLE COMMAND the effective permission set does not grant. The contract change shipped in code without its SSOT line."
    exit 1
}
exit 0
