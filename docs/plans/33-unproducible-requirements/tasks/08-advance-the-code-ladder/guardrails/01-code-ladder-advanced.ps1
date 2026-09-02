# catches: a code-ladder edit that leaves the next author a trap. Three ways it goes wrong and each has
#          bitten this codebase: the reservation block still lists GR2060 (now allocated, so the block
#          lies about what is free); GR2070 arrives with no REASON line, which invites the next author to
#          spend a code whose check has no positive control; or the wrong next-free marker is edited.
#
# THE TWO-MARKER TRAP is why the last clause exists. DiagnosticCodes.cs carries TWO next-free markers and
#          only one is live. The one near line 1026 is CURRENT. The one near line 565 is a QUOTED
#          HISTORICAL marker naming GR2047 - a record of what was true then, not an instruction. Reading
#          it as authoritative has already misled both a human and a guardrail on this codebase, so this
#          check asserts the historical marker is UNCHANGED as well as that the live one advanced.
#
# Required-present baselines (#478), measured on master @67859c7 against DiagnosticCodes.cs:
#          'CURRENT next-free code: GR2071'  0  - expected; this task writes it
#          'GR2070 - ...33-unproducible...'  0  - expected; this task writes it
#          'CURRENT next-free code: GR2047'  1  - NONZERO with a named reason: a REGRESSION PIN on the
#                                                 historical marker, asserted to still be there. The PIN
#                                                 IS THE MARKER TEXT, NOT THE BARE CODE, and that is a
#                                                 correction an adversarial pass made to this file: bare
#                                                 'GR2047' measures 4, because MalformedRoutingGuidance
#                                                 is a LIVE constant with that value (DiagnosticCodes.cs
#                                                 :591). A bare-code clause is satisfied by the constant
#                                                 alone, so the whole historical block could be DELETED
#                                                 and this guardrail would still pass - the trap the
#                                                 header above spends a paragraph on was not guarded.
$ErrorActionPreference = 'Continue'

$codes = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Loading/DiagnosticCodes.cs' }
if (-not (Test-Path -LiteralPath $codes)) {
    Write-Output ('PRECONDITION: ' + $codes + ' does not exist.')
    exit 1
}

$raw = Get-Content -LiteralPath $codes -Raw
$failures = New-Object System.Collections.Generic.List[string]

# The live marker advanced.
if ($raw -notmatch 'CURRENT next-free code:\s*GR2071') {
    $failures.Add('THE LIVE NEXT-FREE MARKER DID NOT ADVANCE to GR2071 in ' + $codes + '. GR2060 is now allocated and GR2070 is held by name, so the next code available to allocate is GR2071.')
}

# The historical marker is NOT collateral damage.
if ($raw -notmatch 'CURRENT next-free code: GR2047') {
    $failures.Add('THE HISTORICAL MARKER WAS EDITED: the quoted marker text "CURRENT next-free code: GR2047" no longer appears in ' + $codes + '. The marker near line 565 is a QUOTED HISTORICAL record naming GR2047, not a live instruction - editing it corrupts a historical note. Only the live marker near line 1026 advances.')
}

# GR2060 has LEFT the reservation block: it is a shipped constant now.
$reservationLine = [regex]::Match($raw, '(?m)^\s*//\s*GR2060\s')
if ($reservationLine.Success) {
    $failures.Add('GR2060 IS STILL RESERVED BY NAME in ' + $codes + '. Task 4 allocated it as a shipped constant, so its reservation line must be REMOVED - a block that lists an allocated code tells the next author it is free.')
}

# GR2070 has ARRIVED, with a reason rather than a bare reservation.
if ($raw -notmatch '(?m)^\s*//\s*GR2070\s') {
    $failures.Add('GR2070 IS NOT HELD BY NAME in ' + $codes + '. Add it to the reservation block - it is DECLINED, not free, and an unrecorded code is one the next design re-proposes from scratch.')
} else {
    $blk = [regex]::Match($raw, '(?m)^\s*//\s*GR2070\s[\s\S]{0,400}')
    if ($blk.Value -notmatch '33-unproducible-requirements' -or $blk.Value -notmatch '(?i)declin|never fired|positive control') {
        $failures.Add('GR2070 IS RESERVED WITHOUT ITS REASON in ' + $codes + '. A bare "reserved" invites the next author to spend the code; the line must point at docs/plans/33-unproducible-requirements.md section 6.3 and say the design exists and the evidence did not - it has never fired on a real defect at any commit in this repository.')
    }
}

# No constant may TAKE the value - held, not allocated. Comments blanked so the reservation stays legal.
$scan = [regex]::Replace($raw, '(?m)^\s*//.*$', '')
if ($scan -match '=\s*"GR2070"') {
    $failures.Add('GR2070 HAS BEEN ALLOCATED as a constant in ' + $codes + '. It is HELD BY NAME. Section 11 prohibition 2.')
}

# The SSOT must agree - the code wins, and the doc follows in the same change-set.
$ssot = 'docs/plans/02-schemas-and-contracts.md'
if (Test-Path -LiteralPath $ssot) {
    $doc = [regex]::Replace((Get-Content -LiteralPath $ssot -Raw), '(?s)<!--.*?-->', '')
    if ($doc -notmatch 'GR2071') {
        $failures.Add('THE SSOT DOES NOT RECORD next-free GR2071 (section 14.10 of ' + $ssot + '). The code wins and the doc follows in the same change-set; a doc that states a stale next-free is how a code gets allocated twice.')
    }
    if ($doc -notmatch 'GR2070') {
        $failures.Add('THE SSOT DOES NOT RECORD GR2070 as reserved by name (section 14.10 of ' + $ssot + ').')
    }
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The diagnostic-code ladder was not advanced correctly (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output 'Code ladder advanced: GR2060 allocated and out of the reservation block, GR2070 held with its reason, next-free GR2071, historical marker intact, SSOT agrees.'
exit 0
