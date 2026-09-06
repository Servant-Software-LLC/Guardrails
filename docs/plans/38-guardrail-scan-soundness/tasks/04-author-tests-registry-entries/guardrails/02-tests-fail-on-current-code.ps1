# catches: a HOLLOW red. A suite-level non-zero exit fires if ANY selected test fails, so an
#          Assert.True(true) body passes on the current tree hiding behind its genuinely-failing
#          siblings. This is the PER-TEST CENSUS (#375): every enumerated behaviour is bound to a
#          PINNED test method name and its outcome is read out of the runner's own TRX - never stdout
#          (#248), never --list-tests name discovery, which a hollow body satisfies exactly as a
#          comment satisfies a token floor.
# Boundary, stated because a green census must not be over-read: this proves each test is COUPLED to
#          the code path (it fails while the behaviour is absent), NOT that its assertion is correct.
#          An invoking-then-hollow test is red here, green after, and passes.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # a non-zero dotnet exit is DATA here, not an error
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The accumulator is created BEFORE its first use. That ordering is the literal defect this plan
# exists to fix (#608, docs/plans/35-.../02-tests-fail-on-stubs.ps1) - do not "tidy" it back down.
$problems = New-Object System.Collections.Generic.List[string]

$filter = "FullyQualifiedName~BannedPatternRegistryTests"
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr38b-census-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $trxDir | Out-Null
try {
    $log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `
        --filter $filter --nologo --logger "trx;LogFileName=census.trx" --results-directory $trxDir 2>&1 | Out-String
    Write-Output $log

    $trx = Join-Path $trxDir 'census.trx'
    if (-not (Test-Path -LiteralPath $trx)) {
        Write-Output "PRECONDITION: no TRX at $trx - the test run did not happen (the host failed to start, or the project failed to build). This is NOT a report about unbound behaviours."
        exit 1
    }

    [xml]$xml = Get-Content -LiteralPath $trx -Raw
    # #455: with zero executed tests the TRX carries no <Results>, the dotted navigation yields $null,
    # and @($null).Count is 1 - so filter the nulls out or the guard below can never fire.
    $results = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($results.Count -lt 1) {
        Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests. BannedPatternRegistryTests did not run, or its name does not match. A census over an empty set certifies nothing."
        exit 1
    }

    # The behaviours that MUST be red: none of the three registry entries exists yet, so every
    # firing control finds no diagnostic and the curated-set assertion sees 3 where it expects 6.
    # There is deliberately NO #449 entry - it is not expressible without rejecting the doctrine's own
    # canonical union guardrail (design 38 SS5).
    $mustFail = @(
        'Entry608a_ContinuePreference_FiresGr2037',
        'Entry608a_StopPreference_IsClean_NoGr2037',
        'Entry608b_GuardrailNotEndingOnExit_FiresGr2037',
        'Entry608b_GuardrailEndingOnExit_IsClean_NoGr2037',
        'Entry608b_TryFinallyCleanupIdiom_IsClean_NoGr2037',
        'Entry561_CommentStripBeforeLiteralNeutralize_FiresGr2037',
        'Entry561_LiteralNeutralizeFirst_IsClean_NoGr2037',
        'Registry_IsExactlyTheCuratedSet_NotWhateverAccumulated'
    )
    # No declared exemption on this task: every pinned behaviour is genuinely absent today.
    $mustExecute = @()

    foreach ($name in $mustExecute) {
        $hit = $results | Where-Object { $_.testName -like "*$name*" } | Select-Object -First 1
        if (-not $hit) {
            $problems.Add("[$name] NOT EXECUTED - declared-exempt from the red census but still required to RUN. No test with this method name is in the TRX. Author it.")
        }
        elseif ($hit.outcome -ne 'Passed') {
            $problems.Add("[$name] outcome '$($hit.outcome)', expected 'Passed'. This behaviour already holds on the current tree, so a failure means the test asserts something other than what it claims.")
        }
    }

    foreach ($name in $mustFail) {
        $hit = $results | Where-Object { $_.testName -like "*$name*" } | Select-Object -First 1
        if (-not $hit) {
            $problems.Add("[$name] NOT BOUND - no test with this method name executed. The prompt pins this name; author it, or this behaviour has no red.")
        }
        elseif ($hit.outcome -ne 'Failed') {
            $problems.Add("[$name] outcome '$($hit.outcome)', expected 'Failed'. It passes against a tree where the three GR2037 entries do not exist yet, so it is not coupled to the code path it claims to test.")
        }
    }

    if ($problems.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Per-test red census ($($problems.Count) problem(s) of $($results.Count) executed) ==="
        $problems | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Red census: all 7 registry behaviours bound to a pinned test and observed Failed. $($results.Count) test(s) ran."
    exit 0
}
finally {
    Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue
}
