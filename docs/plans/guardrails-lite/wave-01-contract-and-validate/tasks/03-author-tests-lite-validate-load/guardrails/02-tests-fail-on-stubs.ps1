# catches: a HOLLOW parity test - named for its GR code, body never invoking Lite or never comparing it
#          with Core (Assert.True(true), an assertion only on the fixture). It PASSES against the stubs
#          (validate.ps1 prints {"stub":true} and exits 99) and hides behind its failing siblings, so a
#          suite-level non-zero exit would certify the file honest (#375). One entry per pinned behaviour,
#          each observed Failed in the runner's OWN TRX - never merely discovered by name.
# does NOT catch: a test that can NEVER pass (#530) - red is this gate's success condition. The pinned
#          parity recipe (Core oracle, restricted code set, non-vacuity assert) is what keeps each test
#          passable; the implementation task's tests-pass is where an unpassable one would surface.
# does NOT catch: an INVOKING-then-hollow test (runs Lite, asserts only r != null) - red on the stubs,
#          green after. The per-code parity assertion pinned in the prompt is the compensating control.
# No declared exemptions: every row runs the STUB validate.ps1 (exit 99, no diagnostics), so every
#          correct test is red here.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateLoadTests'   # SAME string as task 04's tests-pass

# Pinned in action.prompt.md; measured baseline at authoring time: 0 of these names exist anywhere under
# tests/ (grep -rn "LiteValidateLoadTests" tests/ -> no hits), as expected for a test-author task.
$manifest = [ordered]@{
    'valid baseline: Lite exits 0, ok:true, no load codes'   = 'ValidBaseline_ExitsZeroWithNoLoadCodes'
    'GR1001 guardrails.json missing'                         = 'Gr1001_MissingConfig'
    'GR1001 task.json missing'                               = 'Gr1001_MissingTaskJson'
    'GR1002 guardrails.json unparseable'                     = 'Gr1002_InvalidConfigJson'
    'GR1002 guardrail sidecar unparseable'                   = 'Gr1002_InvalidSidecarJson'
    'GR1003 config missing version'                          = 'Gr1003_ConfigMissingVersion'
    'GR1003 task missing description'                        = 'Gr1003_TaskMissingDescription'
    'GR1004 no action file'                                  = 'Gr1004_NoActionFile'
    'GR1005 two action files'                                = 'Gr1005_TwoActionFiles'
    'GR1006 explicit action.path missing'                    = 'Gr1006_ActionPathNotFound'
    'GR1007 orphan sidecar'                                  = 'Gr1007_OrphanSidecar'
    'GR1009 tasks/ holds no task folder'                     = 'Gr1009_EmptyTasksDir'
    'GR2003 task with no guardrails'                         = 'Gr2003_TaskWithoutGuardrails'
    'GR2027 plan guardrail missing catches'                  = 'Gr2027_PlanGuardrailMissingCatches'
    'GR2027 task preflight missing catches'                  = 'Gr2027_TaskPreflightMissingCatches'
    'task guardrails/ is NOT catches-enforced'               = 'TaskGuardrailWithoutCatches_IsNotGr2027'
    'GR2029 retired integrationGate key'                     = 'Gr2029_RetiredIntegrationGate'
    'GR2035 duplicate check name'                            = 'Gr2035_DuplicateCheckName'
    'load errors suppress semantic codes (PlanProbe rule)'   = 'LoadErrors_SuppressSemanticCodes'
    'missing plan folder exits 64 (usage)'                   = 'MissingPlanFolder_ExitsUsage64'
    'output is one JSON object with diagnostics'             = 'Output_IsOneJsonObjectWithDiagnostics'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
# No -v q: nothing is re-emitted here, and the flag propagates onto forward checks by cloning (#462).
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - no TRX means the run never happened. Diagnose THAT, not "every behaviour unbound".
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

# Dotted navigation (default xmlns); Where-Object because a zero-result TRX has no <Results> and
# @($null).Count is 1 - without it this guard can never fire (measured, §4.4).
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped (pwsh missing on this box?). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name    = $manifest[$behaviour]
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'   # -cmatch: method names are case-sensitive
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from LiteValidateLoadTests, misspelled, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen against the STUB validate.ps1, not Failed. A parity test that does not fail when Lite prints {""stub"":true} and exits 99 never compares Lite with Core. ('NotExecuted' = skipped - is pwsh on PATH?)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) pinned behaviours are not proven RED on the stubs ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
