# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          on a value the test itself constructed, anything that never invokes the validator). It
#          PASSES against the current code and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit certifies the file honest and a name-based coverage floor
#          certifies it covered (#375). One entry per enumerated behaviour, each observed Failed in
#          the runner's OWN TRX - never merely discovered by name, which a hollow body satisfies.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=ModelTieringStage3&FullyQualifiedName~PinAndTierCoexistTests'

# THE MANIFEST: each enumerated behaviour -> the method name the ACTION PROMPT PINNED for it.
# Prompt-to-manifest agreement is NOT mechanically enforced - checked by hand against
# action.prompt.md when this was authored.
# The model-pin-only entry is the load-bearing one: TierResolver.cs:139 is `Runner is not null ||
# Model is not null`, so a model pin ALONE kills the tier. A "both required" reading of DoR §13.2's
# slash would silently drop exactly that case.
$manifest = [ordered]@{
    'GR2053 fires on a RUNNER pin coexisting with action.tier' = 'WarnsWhenRunnerPinAndTierCoexist'
    'GR2053 fires on a MODEL pin alone coexisting with action.tier' = 'WarnsWhenModelPinAndTierCoexist'
    'GR2053 is SILENT on a pin with no tier'                   = 'SilentWhenPinWithoutTier'
    'GR2053 is SILENT on a tier with no pin'                   = 'SilentWhenTierWithoutPin'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Falling through would
# print "every behaviour unbound", a confident wrong message aimed at the one artifact the retry agent
# IS allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds NOTHING.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult)
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name -
    # load-bearing here, where 'WarnsWhenRunnerPinAndTierCoexist' and
    # 'WarnsWhenModelPinAndTierCoexist' share a long tail.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the CURRENT code, not Failed. The validator does not emit GR2053 yet, so a test that does not fail here never invokes it - it asserts a tautology and certifies nothing. Drive the real PlanValidator and assert the diagnostic. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
