# catches: this task's agent opening a turn budget with nothing to satisfy. Its entire deliverable is
#          "write the fourteen clauses the anchor test pins", and that test is
#          03-author-tests-review-net-doctrine's contribution. If it did not reach this segment, the agent
#          writes a probe against a prompt that says "read the anchor file first - it is the contract",
#          finds no such file, and either guesses or halts - and its own tests-pass guardrail then reports
#          a zero-match filter, which reads as a naming problem rather than a delivery one. This runs at
#          taskBase, BEFORE the attempt loop, and says the true thing in one cheap check.
#
#          The second clause matters as much as the first: an anchor class that does not reference the
#          skill path is not pinning this skill, and a probe written to satisfy it would go green over a
#          document nobody is checking.
#
# POSITIVE and MONOTONE-SAFE: both clauses are assert-PRESENT. A task-level preflight re-runs per attempt
# against a segment that only grows, so a "not yet present" clause would flip false the moment an unrelated
# file landed.
#
# MEASURED BASELINE 2026-08-24: both clauses are 0 on the WAVE's entry tree, because the file does not
# exist there at all. That is the right measurement for a wave gate and the wrong one for this check: a
# task preflight is evaluated in the CONSUMER's segment, where its ancestor has already merged, so green is
# the expected and correct state here (#478's positive-precondition exception). It is red exactly when the
# delivery failed.
$ErrorActionPreference = 'Continue'

$path = 'tests/Guardrails.Core.Tests/ModelTiering/ModelAppropriatenessDoctrineAnchorTests.cs'
if (-not (Test-Path $path -PathType Leaf)) {
    # PRECONDITION: the subject is gone, so every clause below would scan a null.
    Write-Output "$path is not in this task's segment - 03-author-tests-review-net-doctrine's anchor test did not reach it. That file IS this task's contract: it holds the fourteen clauses, verbatim, that the prose must carry. Do not write the probe from this prompt's copy of the list alone and hope; this is a delivery failure upstream."
    exit 1
}

# Comments stripped for the class clause. NOT for the path clause: the skill path legitimately appears in
# a string literal, which is exactly where it should be.
$raw = Get-Content -Raw -Path $path
$stripped = ($raw -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''

$failures = @()
# Case-SENSITIVE, like every other scan in this wave.
if ($stripped -cnotmatch 'class\s+ModelAppropriatenessDoctrineAnchorTests\b') {
    $failures += "$path does not declare ModelAppropriatenessDoctrineAnchorTests - the file is present but is not the anchor test, so this task's tests-pass guardrail would select nothing and certify nothing"
}
if ($raw -cnotmatch '\.claude/skills/guardrails-review/SKILL\.md') {
    $failures += "$path does not name .claude/skills/guardrails-review/SKILL.md - the anchors are not pointed at the skill this task edits, so satisfying them would prove nothing about the document that shipped"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== anchor delivery: $($failures.Count) problem(s) with the contract this task must satisfy ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
