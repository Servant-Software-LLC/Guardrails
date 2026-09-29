# catches: a HOLLOW test in LiteLockTests - named for the behaviour, body a tautology (Assert.True(true),
#          an assertion over a value the test built itself, anything that never drives the Lite script
#          or module under test). It PASSES against the stubs (exit 99 / throw 'stub') and hides behind
#          its genuinely-failing siblings, so a suite-level non-zero exit would certify the file honest
#          (#375). One entry per enumerated behaviour, each observed Failed in the runner's OWN TRX -
#          never merely discovered by name, which a hollow body satisfies. The --filter names THIS
#          pair's own class(es), never the bare plan-wide trait (#455).
# does NOT catch: a test that can NEVER pass (#530) - red is this gate's success condition - nor an
#          INVOKING-then-hollow test (drives the script, then asserts nothing about its output). The
#          prompt's pinned assertions are the control for the latter; the census proves coupling to the
#          code path, not assertion correctness.
# DECLARED EXEMPTIONS: none - every pinned test must be red on the stub tree.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$project = 'tests/Guardrails.Integration.Tests'
$filter  = 'Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteLockTests'   # SAME string as the paired implementation task's forward half

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'lock writes plan + per-task definition hashes equal to Core' = 'Lock_WritesPlanAndPerTaskDefinitionHashesMatchingCore'
    'a second lock without -Force is refused (exit 1) and leaves the file unchanged' = 'Lock_RefusesWhenALockAlreadyExists'
    '-Force overwrites an existing lock' = 'Lock_Force_OverwritesAnExistingLock'
    '-Verify on an unchanged plan exits 0' = 'Verify_UnchangedPlan_ExitsZero'
    '-Verify after a task guardrail edit exits 1 naming that task' = 'Verify_AfterEditingATaskGuardrail_ExitsOneNamingThatTask'
    '-Verify after only a plan-level gate edit exits 1 naming <plan>' = 'Verify_AfterEditingOnlyAPlanLevelGate_ExitsOneNamingPlan'
    '-Verify with no lock file exits 1 with reason no-lock' = 'Verify_WithNoLockFile_ExitsOneWithReasonNoLock'
    'lock on a missing plan dir exits 64' = 'Lock_MissingPlanDir_ExitsWithUsageCode64'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr-lite-census-$PID"
Remove-Item -LiteralPath $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
# No -v q on a test command (#462). --no-build: guardrail 01 compiled the project.
$out = dotnet test $project --filter $filter --no-build --nologo --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - no results directory / no TRX means the run never happened (host failed to start,
# missing build, malformed --filter which exits 0 silently). Diagnose THAT, never "every behaviour
# unbound" (#455). Test-Path FIRST: Get-ChildItem on a MISSING path with -Recurse falls back to walking
# the parent (all of %TEMP%) and hangs - measured; hence the guard and -LiteralPath.
if (-not (Test-Path -LiteralPath $resultsDir)) {
    Write-Output "PRECONDITION: no test results directory ($resultsDir) - the test host never ran (build missing, host failed to start, or a malformed --filter). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}
$trx = Get-ChildItem -LiteralPath $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, missing build, or a malformed --filter). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation (the TRX has a default xmlns). The Where-Object is load-bearing: with zero tests the
# TRX has NO <Results> element and @($null).Count is 1, so the bare form never fires (proven, #375).
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179/#478): one distinguishable message per unbound behaviour.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name    = $manifest[$behaviour]
    # -cmatch: C# names are case-sensitive; the (\(|$) tail admits a [Theory] row, not a longer sibling.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, misnamed, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the STUB tree, not Failed. A test that does not fail against the stub never drives the script/module under test, so it certifies nothing. ('NotExecuted' = [Fact(Skip=...)] or a pwsh-missing skip.)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) pinned behaviours are not proven RED on the stubs ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0