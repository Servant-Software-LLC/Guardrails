# catches: a merged HEAD that compiles but regressed behaviour somewhere this plan touched - the shim
#          changes how EVERY script guardrail is invoked, so the whole suite is the only honest end
#          check. LOCAL by design (no `scope` key): a whole-suite run is a TERMINAL POSTCONDITION and
#          would red-halt a correct partial union (#125/#165). Re-emits failure detail at the END (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)

$out = dotnet test Guardrails.sln --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    $detail = @()
    $emit = $false
    foreach ($line in $out) {                              # BLOCK capture, not a line allowlist (#608)
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $emit = $true }
        elseif ($line -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - the runner's output format may have changed; inspect the full log above)" }
    Write-Output "the full suite is red on the merged plan branch (see failure details above)"
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the terminal gate certified nothing. The test host may have failed to start, or every test is [Skip]ped (#455)."
    exit 1
}
exit 0
