# catches: a HOLLOW parity test - named for its GR code, body never invoking Lite or never comparing it
#          with Core. It PASSES against the stub Graph.psm1 (returns @()) and hides behind its failing
#          siblings, so a suite-level non-zero exit would certify the file honest (#375). One entry per
#          pinned behaviour, each observed in the runner's OWN TRX - never merely discovered by name.
# DECLARED EXEMPTIONS (Expect='Executed'), with the reason a CORRECT test is green on the stub tree:
#          'ValidBaseline_HasNoGraphCodes', 'ScopeValue_IsTrimmedAndCaseInsensitive' and
#          'EmptyWriteScope_IsNotGr2041' each assert that Lite AND Core report NO graph code. Once task 04's
#          real validate.ps1 dispatcher is on this task's base (it is not a dependency, so that depends on
#          merge order), the stub Graph.psm1's @() makes Lite agree with Core - the correct outcome.
#          Demanding red would demand a correct test fail. They stay IN the manifest and must still RUN.
# does NOT catch: a test that can NEVER pass (#530), or an invoking-then-hollow test (runs Lite, asserts
#          nothing about the codes). The pinned parity recipe - including Assert.False(load.HasErrors) on
#          every test so a fixture that breaks the LOAD cannot pose as "no graph codes" - is the
#          compensating control; task 06's tests-pass is where an unpassable test would surface.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateGraphTests'   # SAME string as task 06's tests-pass

# Pinned in action.prompt.md; measured baseline at authoring time: 0 hits for "LiteValidateGraphTests"
# anywhere under tests/, as expected for a test-author task.
$manifest = [ordered]@{
    'valid baseline has no graph codes (exempt)'           = @{ Name = 'ValidBaseline_HasNoGraphCodes'; Expect = 'Executed' }
    'GR2001 unknown dependency'                            = 'Gr2001_UnknownDependency'
    'GR2007 dependency cycle'                              = 'Gr2007_DependencyCycle'
    'GR2010 duplicate stableId'                            = 'Gr2010_DuplicateStableId'
    'GR2011 invalid stableId format'                       = 'Gr2011_InvalidStableId'
    'GR2019 writeScope with a .. segment'                  = 'Gr2019_ParentSegment'
    'GR2019 rooted writeScope'                             = 'Gr2019_RootedPath'
    'GR2021 bad scope on a task guardrail sidecar'         = 'Gr2021_InvalidScopeOnTaskGuardrail'
    'GR2021 bad scope on a plan guardrail sidecar'         = 'Gr2021_InvalidScopeOnPlanGuardrail'
    'scope value is trimmed + case-insensitive (exempt)'   = @{ Name = 'ScopeValue_IsTrimmedAndCaseInsensitive'; Expect = 'Executed' }
    'GR2041 writeScope absent'                             = 'Gr2041_MissingWriteScope'
    'writeScope [] is valid (exempt)'                      = @{ Name = 'EmptyWriteScope_IsNotGr2041'; Expect = 'Executed' }
    'graph diagnostic is error severity, exit 1'           = 'GraphDiagnostic_IsErrorSeverityAndExitsOne'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
# No -v q: nothing is re-emitted here, and the flag propagates onto forward checks by cloning (#462).
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# Test-Path FIRST, and -LiteralPath: when the results dir does not exist (the run never happened),
# `Get-ChildItem <missing> -Filter *.trx -Recurse` does NOT fail - it treats the missing leaf as a filter and
# recursively walks the PARENT (all of %TEMP%), which measured as an indefinite hang at authoring time.
$trx = $null
if (Test-Path -LiteralPath $resultsDir -PathType Container) {
    $trx = Get-ChildItem -LiteralPath $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
}
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, the project was not built, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })   # @($null).Count is 1 - keep the filter
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped (pwsh missing on this box?). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $entry   = $manifest[$behaviour]
    $name    = if ($entry -is [string]) { $entry }   else { $entry.Name }
    $expect  = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from LiteValidateGraphTests, misspelled, or not selected by the filter)"
        continue
    }
    if ($expect -eq 'Executed') {
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (see this file's header) and did NOT execute - an exempt row still has to run. Is it [Skip]ped, or is pwsh missing?"
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen against the STUB Graph.psm1, not Failed. A mutation test that passes when Lite reports no graph code never compares Lite with Core. ('NotExecuted' = skipped.)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) pinned behaviours are not proven on the stub ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
