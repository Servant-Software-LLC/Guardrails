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

$filter = "Category=ScanSoundness&FullyQualifiedName~GuardrailAbortTests"
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr38-census-" + [guid]::NewGuid().ToString("N"))
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
        Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests. GuardrailAbortTests was never authored, or its name/trait does not match. A census over an empty set certifies nothing."
        exit 1
    }

    # The behaviours that MUST be red: the abort is undetected on the current tree.
    $mustFail = @(
        'AbortedGuardrail_IsNotAPass',
        'AbortedGuardrail_ReasonNamesTheAbort_NotTheFirstStdoutLine',
        'AbortedGuardrail_SurfacesTheInterpreterErrorText'
    )
    # DECLARED EXEMPTION (Step 2 rule 5): these three are GREEN on a correct current tree - exit 1
    # already fails, exit 0 already passes, and stderr noise is already ignored. Demanding them red
    # would demand a correct implementation fail. They are the REGRESSION GUARD: design 38 SS3.3
    # records two plausible shims that turned `exit 1` into exit 0, which is a worse defect than the
    # one being fixed. They must EXECUTE and PASS.
    $mustExecute = @(
        'ExitOneGuardrail_StillFails',
        'ExitZeroGuardrail_StillPasses',
        'GuardrailWritingToStderr_ThenExitingZero_StillPasses'
    )

    foreach ($name in $mustExecute) {
        $hit = $results | Where-Object { $_.testName -like "*$name*" } | Select-Object -First 1
        if (-not $hit) {
            $problems.Add("[$name] NOT EXECUTED - declared-exempt from the red census but still required to RUN. No test with this method name is in the TRX. It is the regression guard for design 38 SS3.3; author it.")
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
            $problems.Add("[$name] outcome '$($hit.outcome)', expected 'Failed'. It passes against a tree where an aborted guardrail is still recorded as a PASS, so it is not coupled to the code path it claims to test.")
        }
    }

    if ($problems.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Per-test red census ($($problems.Count) problem(s) of $($results.Count) executed) ==="
        $problems | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Red census: 3 abort behaviours bound and observed Failed; 3 declared-exempt regression guards executed and Passed. $($results.Count) test(s) ran."
    exit 0
}
finally {
    Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue
}
