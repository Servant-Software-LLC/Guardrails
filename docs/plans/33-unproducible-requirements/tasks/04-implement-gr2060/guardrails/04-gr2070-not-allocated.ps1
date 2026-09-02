# catches: a run that reads the reservation block as a TODO and SPENDS GR2070. It is held by name, not
#          free (section 11, prohibition 2): the check it was designed for has never fired on a real
#          defect at any commit in this repository, so allocating the code would ship an unfalsified
#          lint into the ladder wearing a number that says it was earned.
#
# FORBIDDEN-PRESENT, so no baseline measurement is required (#478 exempts a ban): a ban that is green on
#          arrival is a correct ban. Measured anyway for the record: 0 constants hold that value today.
#          The clause deliberately scans for the VALUE in a constant assignment, not the bare token -
#          GR2070 appears legitimately in the reservation comment (task 8 adds it there) and in this
#          plan's prose, and banning the mention rather than the USE would forbid the record itself
#          (#470: anchor on a use, never a mention).
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Loading/DiagnosticCodes.cs' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist.')
    exit 1
}

$raw = Get-Content -LiteralPath $subject -Raw
# Blank comments: the reservation line NAMES GR2070 and must stay legal.
$scan = [regex]::Replace($raw, '(?m)^\s*//.*$', '')
$scan = [regex]::Replace($scan, '(?s)/\*.*?\*/', '')

# A constant TAKING the value is the ban. `const string X = "GR2070";` in any spacing.
if ($scan -match '=\s*"GR2070"') {
    Write-Output 'GR2070 HAS BEEN ALLOCATED: a DiagnosticCodes constant now takes the value "GR2070". That code is HELD BY NAME, not free - see docs/plans/33-unproducible-requirements.md section 6.3. The check it was designed for (a guardrail requiring a named argument whose declaring member no task may widen) has never fired on a real defect at any commit in this repository, and its own back-out trigger had already fired before it was declined. Take GR2071 if you genuinely need a new code, and update the next-free marker.'
    exit 1
}

Write-Output 'GR2070 is not allocated: no DiagnosticCodes constant takes that value.'
exit 0
