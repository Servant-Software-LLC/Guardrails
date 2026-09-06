# catches: a registry entry that is PRESENT but does not FIRE on the shape it names - or one that
#          fires only on the spelling it was written against, which a respelling walks straight
#          through. The --filter names this task pair's OWN test class, never a plan-wide trait
#          alone (#455). The EIGHT pinned tests include a deliberate NEGATIVE control - the
#          try{...exit N}finally{...} cleanup idiom #608b must NOT reject, which three of this plan's
#          own guardrails use. Design 38 SS11 names a token-keyed entry as the way this plan ships
#          looking finished; there is deliberately no #449 entry at all (SS5).
#          Re-emits the assertion/exception lines at the END so they reach the retry tail (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # a non-zero dotnet exit is DATA here, not an error
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)

$filter = 'FullyQualifiedName~BannedPatternRegistryTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = @()
    $emit = $false
    foreach ($line in $out) {                              # BLOCK capture, not a line allowlist (#608)
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $emit = $true }
        elseif ($line -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40            # bound so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - the runner's output format may have changed; inspect the full log above)" }
    Write-Output "BannedPatternRegistryTests failing - one or more of the four new GR2037 entries is missing, malformed, does not fire on its shape, or fires only on the known spelling (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}
exit 0
