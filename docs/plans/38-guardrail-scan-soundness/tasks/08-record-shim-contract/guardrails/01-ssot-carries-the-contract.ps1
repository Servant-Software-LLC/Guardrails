# catches: a contract change that shipped in code and not in the SSOT - the document every other
#          document defers to still describing `exit 0` as sufficient for a guardrail pass, months after
#          it stopped being true. This is the change-set rule: the contract moves WITH the code (#608).
# DOCUMENTATION target: exempt from the two-sided sample pair (#468). Compensating controls: the
#          <!-- --> strip below (a required clause over a .md false-PASSES on a commented-out line -
#          measured elsewhere in this repo as a two-token contract check flipping exit 1 to exit 0 on one
#          appended TODO), the whitespace normalization (a required PHRASE wraps in prose - #428 applied
#          to this guardrail itself), and the SECTION anchoring below.
# Measured baseline (#478): both literals count 0 in the SSOT on the starting tree (verified 2026-09-06
#          on master a3f3e977).
$ErrorActionPreference = 'Stop'

# Collapse whitespace before matching a PHRASE (#428, applied to this guardrail itself). A required
# phrase is prose, and prose WRAPS - a markdown document naturally carries a required phrase across a
# line break, and a single-line literal can never match that. Matching the rendered form rather than the
# stored form is precisely the defect this plan exists to document - and the first
# version of this guardrail committed it, false-REDding the honest probe. Normalizing is remedy 2 of the
# three-line rule the task is asked to write down.
function ConvertTo-Flat([string]$text) { return [regex]::Replace($text, '\s+', ' ') }

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
$flatProse = ConvertTo-Flat $prose

# The contract belongs in the section that already describes guardrail execution, not appended anywhere
# in a 2000-line document. Measured: an appended TODO line satisfied the un-anchored form (exit 0).
$secStart = [regex]::Match($prose, '(?m)^## 4\. Guardrails\s*$')
$secEnd   = [regex]::Match($prose, '(?m)^## 5\.')
if (-not $secStart.Success -or -not $secEnd.Success -or $secEnd.Index -le $secStart.Index) {
    Write-Output "PRECONDITION: could not locate the '## 4. Guardrails' ... '## 5.' section in $subject. This guardrail binds to those headings; if the document was restructured, reconcile the two rather than deleting the binding."
    exit 1
}
$section     = $prose.Substring($secStart.Index, $secEnd.Index - $secStart.Index)
$flatSection = ConvertTo-Flat $section

if ($flatProse -notmatch [regex]::Escape('exit 97')) {
    $problems.Add("[$subject] never records 'exit 97'. The shim reserves that code for 'the guardrail aborted before reaching its own verdict', and the harness records it as a FAILURE - a reserved exit code that lives only in the source is a contract nobody can read (#608). ('exit 97' counts 0 today; the document's only 97-shaped strings are issue references such as #97/#197/#597.)")
}
elseif ($flatSection -notmatch [regex]::Escape('exit 97')) {
    $problems.Add("[$subject] mentions 'exit 97' but NOT inside the '## 4. Guardrails' section, which is where this document already describes guardrail execution and its verdict. A contract fact appended elsewhere is one nobody reading about guardrails will find.")
}

if ($flatProse -notmatch [regex]::Escape('harness-owned shim')) {
    $problems.Add("[$subject] does not record that a .ps1 guardrail is invoked through a harness-owned shim rather than directly. Task 02 changed how EVERY script guardrail in the product is invoked; the SSOT is where that is stated, in those words.")
}
if ($flatProse -notmatch [regex]::Escape('38-guardrail-scan-soundness')) {
    $problems.Add("[$subject] does not cite docs/plans/38-guardrail-scan-soundness.md. The contract states WHAT; the design of record holds WHY, including the measured divergence - a contract entry with no pointer to its rationale gets re-litigated.")
}

if ($problems.Count -gt 0) {
    Write-Output "=== SSOT does not carry the shim contract ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "SSOT records the shim, the reserved exit code 97, and a pointer to the design of record."
exit 0
