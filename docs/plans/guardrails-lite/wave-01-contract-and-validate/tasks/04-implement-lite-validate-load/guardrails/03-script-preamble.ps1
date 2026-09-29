# catches: a Lite script that drops the kernel preamble the design sheet pins for every scripts/lite file -
#          `#Requires -Version 7`, `Set-StrictMode -Version Latest`, `$ErrorActionPreference = 'Stop'` -
#          or carries it only commented out. Without StrictMode/Stop a typo'd variable reads as $null and a
#          failing cmdlet continues, so the validator silently reports FEWER diagnostics than Core.
# Why no test carries this (#468): StrictMode and ErrorActionPreference change behaviour only on a LATENT
#          fault (an uninitialised variable, a non-terminating error). The parity fixtures exercise correct
#          code paths, so they pass identically with or without the preamble; the property is unobservable
#          through them. Source shape is the only place it is visible.
# Subject: GR_SUBJECT (or argv[0]) replaces the WHOLE default list when set - that is how the committed
#          samples/ pair is verified (`guardrails samples verify`, #559).
# Measured baseline (#478) at authoring time: each clause matches 0 times on the real tree - neither
#          scripts/lite/validate.ps1 nor scripts/lite/validate/Load.psm1 exists yet (Test-Path -> False).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subjects = if ($env:GR_SUBJECT) { @($env:GR_SUBJECT) }
            elseif ($args.Count -gt 0) { @($args[0]) }
            else { @('scripts/lite/validate.ps1', 'scripts/lite/validate/Load.psm1') }

$failures = @()
foreach ($f in $subjects) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) {
        $failures += "$f does not exist - the implementation must replace the stub at this exact path"
        continue
    }
    # Only the OPENING of the file counts: the preamble must precede any real code.
    $head = (Get-Content -LiteralPath $f -TotalCount 15) -join "`n"
    # Line-anchored and uncommented: '# Set-StrictMode ...' starts with '#', so it cannot satisfy ^\s*Set-StrictMode.
    if ($head -cnotmatch '(?m)^#Requires\s+-Version\s+7\b') {
        $failures += "$f does not open with '#Requires -Version 7' (Lite is PowerShell 7 only)"
    }
    if ($head -notmatch '(?m)^\s*Set-StrictMode\s+-Version\s+Latest\b') {
        $failures += "$f does not open with an uncommented 'Set-StrictMode -Version Latest'"
    }
    if ($head -notmatch "(?m)^\s*\`$ErrorActionPreference\s*=\s*['""]Stop['""]") {
        $failures += "$f does not open with an uncommented `$ErrorActionPreference = 'Stop'"
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== Lite script preamble missing ($($failures.Count) problem(s)) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output "Put the three preamble lines at the top of each file (first 15 lines, uncommented)."
    exit 1
}
exit 0
