# catches: a merged wave HEAD from which wave 1's work has VANISHED - and until this file existed,
#          that HEAD exited the wave GREEN. Both other gates in this folder (a whole-solution build and
#          the unfiltered Core suite) pass perfectly on the tree the wave STARTED from, so between them
#          they carried zero positive evidence that anything was delivered. An independent adversarial
#          pass demonstrated it by running them on the untouched baseline: exit 0 and exit 0.
#
#          GR2028 is satisfied by the build/suite pair - a whole-repo re-run is the ungameable half. But
#          the catalogue is explicit that a contribution-present check is ADDITIVE, layered on top, and
#          this wave had the sufficient half and none of the additive half. These are the three greps
#          that make the terminal boundary able to tell "wave 1 succeeded" from "wave 1 evaporated" -
#          which is the one thing only this gate is positioned to see.
#
# LOCAL - no `scope` key (GR2059/#459), like its siblings: a wave-root guardrail runs exactly once, on
# the merged HEAD at its own wave's exit, and the per-union set is the task folders plus the PLAN root.
$ErrorActionPreference = 'Continue'
$failures = @()

# MEASURED BASELINE 2026-08-22 against the untouched tree: every clause below is 0 / absent. This gate
# is correctly RED before the wave runs, which is precisely what its siblings were not.
$codes = @{ 'GR2051' = 'NonRoutableBlockIsDefault'; 'GR2052' = 'CostlyBlockRoutingInert'; 'GR2053' = 'PinAndTierCoexist' }

$declFile = 'src/Guardrails.Core/Loading/DiagnosticCodes.cs'
$emitFile = 'src/Guardrails.Core/Loading/PlanValidator.cs'
foreach ($f in @($declFile, $emitFile)) {
    if (-not (Test-Path $f)) { Write-Output "$f not found - the wave's own subject is missing"; exit 1 }
}
$decl = Get-Content -Raw -Path $declFile
$emit = Get-Content -Raw -Path $emitFile

foreach ($code in $codes.Keys) {
    $name = $codes[$code]
    # Declared, at the reserved number (task 01's deliverable, surviving the merge).
    if ($decl -notmatch ('\b' + [regex]::Escape($name) + '\s*=\s*"' + $code + '"')) {
        $failures += "$declFile does not declare $name = `"$code`" - task 01's allocation is not on the merged wave HEAD"
    }
    # EMITTED, not merely declared (tasks 03/05). A constant nobody references is an allocation, not a
    # warning - anchored on the DiagnosticCodes.<Name> use, so a comment mentioning it does not count.
    if ($emit -notmatch ('DiagnosticCodes\.' + [regex]::Escape($name) + '\b')) {
        $failures += "$emitFile never references DiagnosticCodes.$name - $code is allocated but nothing emits it, so the warning does not exist at runtime"
    }
}

# Both test classes present (tasks 02/04). The unfiltered suite proves the tests PASS; nothing else
# proves they EXIST, and a suite with the files deleted is green.
foreach ($cls in @('TieringRegistryWarningTests', 'PinAndTierCoexistTests')) {
    $hit = @(Get-ChildItem -Path 'tests/Guardrails.Core.Tests' -Filter '*.cs' -Recurse -ErrorAction SilentlyContinue |
             Select-String -Pattern ('class\s+' + $cls + '\b') -List)
    if ($hit.Count -lt 1) {
        $failures += "no file under tests/Guardrails.Core.Tests declares class $cls - the wave's tests are not on the merged HEAD"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-1 deliverables: $($failures.Count) missing from the merged HEAD ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output "The build and the suite both pass on a tree with none of this work in it, so a green from them is not evidence of delivery. Something dropped between the task segments and the wave merge."
    exit 1
}
exit 0
