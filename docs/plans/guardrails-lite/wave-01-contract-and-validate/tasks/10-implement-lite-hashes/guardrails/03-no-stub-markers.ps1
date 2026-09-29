# catches: a stub left in place behind green tests - e.g. mark-reviewed.ps1 still `exit 99` / `"stub":true`
#          while a test that only exercises plan-hash.ps1 passes, or Hash.psm1 still exporting a
#          `throw 'stub'` function no test happens to call (a helper the next wave's lock/record will call).
# WHY A SOURCE-SHAPE CHECK (#468): "no stub marker survives" is a structural fact about the delivered
#          files, not a behaviour - a function nobody calls yet has no runtime proxy in THIS task's tests.
# Forbidden-present clauses: no baseline census needed (a ban green on arrival is a correct ban; here the
#          ban is RED on arrival by design, because task 09 wrote the stubs).
# Subject: argv[0] or GR_SUBJECT replaces the WHOLE subject list (#559); default = the three owned files.
# Committed pair: ../samples/03-no-stub-markers.valid.ps1 -> 0 ; ../samples/03-no-stub-markers.invalid.ps1 -> 1
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$override = if ($args.Count -gt 0 -and $args[0]) { $args[0] } elseif ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { $null }
$subjects = if ($override) { @($override) } else { @('scripts/lite/lib/Hash.psm1', 'scripts/lite/plan-hash.ps1', 'scripts/lite/mark-reviewed.ps1') }

$failures = @()
foreach ($f in $subjects) {
    if (-not (Test-Path -LiteralPath $f)) {
        $failures += "$f is missing - it must exist and be implemented"
        continue
    }
    # Strip '#' line comments and <# #> blocks so a comment that merely MENTIONS the old stub is legal.
    $raw  = Get-Content -LiteralPath $f -Raw
    $code = [regex]::Replace($raw, '(?s)<#.*?#>', '')
    $code = [regex]::Replace($code, '(?m)^\s*#.*$', '')
    if ($code -match '"stub"\s*:\s*true') { $failures += "$f still emits the stub marker '`"stub`":true'" }
    if ($code -match '(?m)\bexit\s+99\b')   { $failures += "$f still exits 99 (the stub exit code)" }
    if ($code -match "throw\s+['""]stub['""]") { $failures += "$f still has a throw 'stub' body" }
}

if ($failures.Count -gt 0) {
    Write-Output "=== stub markers remain ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "no stub markers remain in: $($subjects -join ', ')"
exit 0
