# catches: this task's agent opening a turn budget against a segment that does not contain the stub it is
#          supposed to fill. `01-author-tests-tier-classification-audit` authors
#          tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs as throwing skeletons, and
#          this task's whole instruction is "fill real logic over them". If that contribution did not land -
#          a dropped hunk at the segment merge, a task that settled without writing the file - the agent
#          finds no stub, reasonably invents the whole type from scratch, and produces something the
#          authored tests were never written against. This runs at taskBase, BEFORE the attempt loop, so
#          that costs one cheap check instead of a hundred turns and a confusing red.
#
#          The three member clauses are not decoration: they are the CONTRACT this task's prompt says the
#          stub carries. A file that exists but declares a different surface is the same failure as an
#          absent one, and it is the likelier of the two after an AI-merge.
#
# POSITIVE and MONOTONE-SAFE: every clause is assert-PRESENT. A task-level preflight re-runs per attempt
# against a segment that only grows, so a "not yet present" clause here would flip false the moment an
# unrelated file landed.
#
# MEASURED BASELINE 2026-08-24: all four clauses are 0 on the WAVE's entry tree, because the file does not
# exist there at all. That is the right measurement for a wave gate and the wrong one for this check: a
# task preflight is evaluated in the CONSUMER's segment, where its ancestor has already merged, so green is
# the expected and correct state here (#478's positive-precondition exception). It is red exactly when the
# delivery failed.
$ErrorActionPreference = 'Continue'

$path = 'tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs'
if (-not (Test-Path $path -PathType Leaf)) {
    # PRECONDITION: the subject is gone, so every clause below would scan a null.
    Write-Output "$path is not in this task's segment - 01-author-tests-tier-classification-audit's stub did not reach it. Do NOT write the type from scratch: the authored tests were written against a specific member surface, and a re-invented one will not satisfy them. This is a delivery failure upstream."
    exit 1
}

# Comments stripped: every prompt in this wave mandates doc comments, so a member dropped by an AI-merge
# whose comment survived would otherwise read here as delivered.
$text = Get-Content -Raw -Path $path
$text = ($text -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''

$failures = @()
$required = @(
    @('class\s+TierClassificationAudit\b', 'the type itself'),
    @('IsTieringConfigured', 'IsTieringConfigured - the graceful-skip gate, and the member whose absence would leave this task guessing whether the skip exists at all'),
    @('ClassifiableSubjects', 'ClassifiableSubjects - the anti-vacuity census the tests assert on before every "no findings" assertion'),
    @('record\s+TierClassificationFinding', 'the TierClassificationFinding record - the shape every assertion in TierClassificationAuditTests reads')
)

foreach ($clause in $required) {
    # Case-SENSITIVE, like every other scan in this wave: PowerShell's -match family is case-INsensitive,
    # which is how a clause ends up satisfied by something it was not written for.
    if ($text -cnotmatch $clause[0]) {
        $failures += "$path does not declare $($clause[1]) (/$($clause[0])/)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== stub delivery: $($failures.Count) member(s) of the contract are missing from this segment ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The stub is 01-author-tests-tier-classification-audit's deliverable and this task fills it IN PLACE. A surface that differs from the one the authored tests were written against cannot be made to pass by implementing harder."
    exit 1
}
exit 0
