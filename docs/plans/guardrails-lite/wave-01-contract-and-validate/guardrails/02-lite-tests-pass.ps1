# catches: a wave that went green task-by-task but whose tests do not pass TOGETHER on the merged HEAD -
#          chiefly the validation-parity theory, whose corpus is every validate-* fixture the three
#          validate tasks authored (only complete once all have merged), plus any Lite class a later task
#          silently regressed (each task's own tests-pass names only its own pair's class, #455). Also the
#          Core contract pin (LiteProfileContractTests), which lives in a different test project.
#          LOCAL wave-exit postcondition - a whole-category suite at a union would red-halt a correct partial
#          merge (#125). Re-emits failure detail at the END (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)

# Guardrail 01 built the solution (Debug); --no-build reuses that build.
$runs = @(
    @{ Project = 'tests/Guardrails.Integration.Tests'; Filter = 'Category=Lite' },
    @{ Project = 'tests/Guardrails.Core.Tests';        Filter = 'FullyQualifiedName~Guardrails.Core.Tests.Loading.LiteProfileContractTests' }
)

$failures = @()
foreach ($r in $runs) {
    $out = dotnet test $r.Project --filter $r.Filter --no-build --nologo 2>&1
    $testExit = $LASTEXITCODE                              # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }

    # EXIT CODE FIRST, guard second (#455).
    if ($testExit -ne 0) {
        $detail = @()
        $emit = $false
        foreach ($line in $out) {                          # BLOCK capture, not a line allowlist (#608)
            if ("$line" -match '^\s*Failed\s+\S' -or "$line" -match '^\s*Error Message:') { $emit = $true }
            elseif ("$line" -match '^(Passed!|Failed!)') { $emit = $false }
            if ($emit) { $detail += $line }
        }
        $failures += @{ What = "$($r.Project) --filter '$($r.Filter)' has failing tests"; Detail = @($detail | Select-Object -First 25) }
        continue
    }

    # ZERO-MATCH GUARD (#455): executed count (Passed+Failed), never Total: (counts skips) or a message.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += @{ What = "$($r.Project) --filter '$($r.Filter)' executed ZERO tests - no match, a malformed filter, or every test [Skip]ped (is pwsh on PATH?)"; Detail = @() }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    foreach ($f in $failures) {
        Write-Output "- $($f.What)"
        $f.Detail | ForEach-Object { Write-Output "    $_" }
    }
    Write-Output "wave-01 Lite tests do not pass together on the merged HEAD ($($failures.Count) run(s) failing)."
    exit 1
}
exit 0
