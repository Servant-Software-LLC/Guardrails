# catches: a contract change that shipped in code and not in the SSOT - the document every other
#          document defers to still describing `exit 0` as sufficient for a guardrail pass, months after
#          it stopped being true. This is the change-set rule: the contract moves WITH the code (#608).
# DOCUMENTATION target: exempt from the two-sided sample pair (#468). Compensating controls: the
#          <!-- --> strip below (a required clause over a .md false-PASSES on a commented-out line -
#          measured elsewhere in this repo as a two-token contract check flipping exit 1 to exit 0 on one
#          appended TODO), and the PRECEDENT check - `97` is the literal the shipped code uses, so the
#          document and the implementation are pinned to the same token.
# Measured baseline (#478): both literals count 0 in the SSOT on the starting tree (verified 2026-09-06
#          on master a3f3e977).
$ErrorActionPreference = 'Stop'

$problems = New-Object System.Collections.Generic.List[string]
$subject  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'docs/plans/02-schemas-and-contracts.md' }

if (-not (Test-Path -LiteralPath $subject -PathType Leaf)) {
    Write-Output "PRECONDITION: $subject does not exist - every clause below would be vacuous."
    exit 1
}

$raw   = Get-Content -Raw -LiteralPath $subject
$prose = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
if ($prose -match '<!--') {
    Write-Output "PRECONDITION: $subject contains an unterminated '<!--'. Stripping to EOF would delete the rest of the document, so no clause below can be trusted."
    exit 1
}

if ($prose -notmatch [regex]::Escape('exit 97')) {
    $problems.Add("[$subject] never records 'exit 97'. The shim reserves that code for 'the guardrail aborted before reaching its own verdict', and the harness records it as a FAILURE - a reserved exit code that lives only in the source is a contract nobody can read (#608). (A bare '97' occurs twice in this document already, so the literal 'exit 97' is what discriminates.)")
}
# Measured (#478): the bare word 'shim' already occurs once in this document (an unrelated back-compat
# mention), so a clause keyed on it is satisfied before the task runs. 'harness-owned shim' counts 0.
if ($prose -notmatch [regex]::Escape('harness-owned shim')) {
    $problems.Add("[$subject] does not record that a .ps1 guardrail is invoked through a harness-owned shim rather than directly. Task 02 changed how EVERY script guardrail in the product is invoked; the SSOT is where that is stated, in those words.")
}
if ($prose -notmatch [regex]::Escape('38-guardrail-scan-soundness')) {
    $problems.Add("[$subject] does not cite docs/plans/38-guardrail-scan-soundness.md. The contract states WHAT; the design of record holds WHY, including the measured divergence - a contract entry with no pointer to its rationale gets re-litigated.")
}

if ($problems.Count -gt 0) {
    Write-Output "=== SSOT does not carry the shim contract ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "SSOT records the shim, the reserved exit code 97, and a pointer to the design of record."
exit 0
