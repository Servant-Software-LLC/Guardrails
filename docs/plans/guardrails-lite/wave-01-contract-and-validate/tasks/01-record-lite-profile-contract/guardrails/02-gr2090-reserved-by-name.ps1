# catches: GR2090 left unreserved (the next allocator takes it and Lite's validate.ps1 then emits a code
#          that means something else in the harness), GR2090 ALLOCATED as a constant (a constant claims
#          the harness emits it - it never does; only scripts/lite/validate.ps1 does), or the live
#          next-free marker still pointing at GR2090 / duplicated.
# WHY A SOURCE-SHAPE CHECK, not a test (#468 demotion order): a reservation COMMENT and the next-free
#          marker comment have NO runtime proxy - a running program cannot see either. Comments are
#          therefore deliberately NOT stripped here: the comment IS the deliverable.
#          (DiagnosticCatalogueTests.TheNextFreeMarkerNamesACodeThatIsActuallyFree only proves the marker
#          is ahead of the constants' high-water mark - it would stay green with the marker at GR2090,
#          since GR2090 is not a constant. Hence clause 3 here.)
# Committed pair (#468/#559): ../samples/02-gr2090-reserved-by-name.valid.cs  -> exit 0
#                             ../samples/02-gr2090-reserved-by-name.invalid.cs -> exit 1 (allocated as a constant)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = if ($args.Count -gt 0 -and $args[0]) { $args[0] } elseif ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Loading/DiagnosticCodes.cs' }

# PRECONDITION - the only early exit.
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output "$subject not found - cannot check the GR2090 reservation"
    exit 1
}
$lines = @(Get-Content -LiteralPath $subject)
$raw   = Get-Content -LiteralPath $subject -Raw

$failures = @()

# baseline counts on the untouched tree - MEASURED 2026-09-29 against src/Guardrails.Core/Loading/DiagnosticCodes.cs:
#   '//' line carrying GR2090 + 'RESERVED BY NAME' + LiteUnsupportedFeature     0
#   = "GR2090"  (forbidden)                                                     0
#   live '// CURRENT next-free code:' markers                                   1  (reads GR2090 - the value this task changes)

# 1. The reservation comment: ONE '//' line naming the code, the reservation and the name together.
$reservation = @($lines | Where-Object {
    $_ -match '^\s*//' -and $_.Contains('GR2090') -and $_.Contains('RESERVED BY NAME') -and $_.Contains('LiteUnsupportedFeature')
})
if ($reservation.Count -lt 1) {
    $failures += "no '//' comment line naming GR2090, 'RESERVED BY NAME' and LiteUnsupportedFeature together - reserve GR2090 by name for Guardrails Lite (#823), the way GR2083 is reserved"
}

# 2. Forbidden: GR2090 declared as a constant value.
if ($raw -cmatch '=\s*"GR2090"') {
    $failures += "GR2090 is declared as a constant value - it must be RESERVED BY NAME in a comment, not allocated; the harness never emits it"
}

# 3. Exactly one live next-free marker, and it names GR2091.
$markers = [regex]::Matches($raw, '(?m)^\s*// CURRENT next-free code: (GR\d+)')
if ($markers.Count -ne 1) {
    $failures += "expected exactly one line starting '// CURRENT next-free code:', found $($markers.Count)"
}
elseif ($markers[0].Groups[1].Value -ne 'GR2091') {
    $failures += "the live next-free marker names $($markers[0].Groups[1].Value) - with GR2090 reserved for Lite it must name GR2091"
}

if ($failures.Count -gt 0) {
    Write-Output "=== GR2090 is not correctly reserved in $subject ($($failures.Count) problem(s)) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "GR2090 reserved by name for LiteUnsupportedFeature; next-free marker names GR2091"
exit 0
