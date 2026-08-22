# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          on a value the test itself constructed, anything that never invokes the validator). It
#          PASSES against the current code and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit certifies the file honest and a name-based coverage floor
#          certifies it covered (#375). One entry per enumerated behaviour, each observed Failed in
#          the runner's OWN TRX - never merely discovered by name, which a hollow body satisfies.
# Culture pin: the census reads TRX schema tokens (not localized), so the guard does not depend on it -
# kept anyway so the logged summary is readable and the pair stays copy-pasteable.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=ModelTieringStage3&FullyQualifiedName~TieringRegistryWarningTests'

# TWO MANIFESTS, NOT ONE - and this split is a BLOCKER fix found by an independent adversarial pass.
#
# The first draft demanded EVERY enumerated behaviour be observed `Failed`. Three behaviours in this
# wave are SILENCE assertions ("the code is absent"), and a negative assertion CANNOT be red before the
# feature exists: `Assert.DoesNotContain(GR2051, …)` passes today, necessarily. So tasks 02 and 04 had
# no honest implementation at all.
#
# The halt was not the damage. The census MESSAGE was: "'SilentWhenTieringNotConfigured' is Passed on
# the CURRENT code, not Failed … it asserts a tautology … Drive the real PlanValidator and assert the
# diagnostic." That is wrong about a correct test, and it instructs the agent to CONVERT the Invariant-7
# silence test into a positive one. It then goes red, the census goes green, task 03 makes it pass, and
# the wave ships GREEN with Invariant 7 guarded by a test asserting the opposite of its name. Nothing
# downstream sees it: the exit gate runs build + suite, and the suite is green.
#
# Prompt-to-manifest agreement is NOT mechanically enforced (validate is blind to a hashtable read
# through Where-Object) - checked by hand against action.prompt.md.

# POSITIVE behaviours: the feature does not exist, so these MUST be observed Failed. This is the real
# anti-tautology proof and it is unchanged.
$mustBeRed = [ordered]@{
    'GR2051 fires when the default pointer names a costly block'       = 'WarnsWhenCostlyBlockIsDefault'
    'GR2051 fires when the default pointer names a routing-less block' = 'WarnsWhenRoutinglessBlockIsDefault'
    'GR2052 fires when a costly block also declares routing'           = 'WarnsWhenCostlyBlockDeclaresRouting'
    'GR2052 and GR2048 compose, neither masking the other'             = 'ComposesWithUnservableTier'
}

# SILENCE behaviours: correctly GREEN today and green after. The census asserts only that the test
# EXISTS and RAN - which still catches an absent test and a [Fact(Skip=...)], the two failure modes it
# can see.
#
# HONEST RESIDUAL, stated because a reader will otherwise assume more: this cannot prove a silence test
# is REAL. A hollow `Assert.True(true)` named SilentWhenTieringNotConfigured passes it exactly as a
# genuine `Assert.DoesNotContain` does. A red baseline cannot exist for a negative assertion, so the
# anti-tautology proof for these three would have to come from MUTATION (make the validator over-fire
# and watch the silence test go red), which is out of scope for this plan. Invariant 7's protection
# here rests on human review of the test body, and the Step 6 report says so.
$mustRunGreen = [ordered]@{
    'GR2051 is SILENT when tiering is not configured (Invariant 7)' = 'SilentWhenTieringNotConfigured'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Falling through would
# print "every behaviour unbound" - a confident wrong message aimed at the one artifact the retry agent
# IS allowed to edit, which is the #455 misdiagnosis trap one level down.
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

# -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
# The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
function Find-Recorded([string]$name) {
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    return @($recorded | Where-Object { $_.testName -cmatch $pattern })
}

foreach ($behaviour in $mustBeRed.Keys) {
    $name = $mustBeRed[$behaviour]
    $hits = Find-Recorded $name
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the CURRENT code, not Failed. This is a POSITIVE behaviour: the validator does not emit this code yet, so a test that does not fail here never invokes it - it asserts a tautology. Drive the real PlanValidator and assert the diagnostic. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

foreach ($behaviour in $mustRunGreen.Keys) {
    $name = $mustRunGreen[$behaviour]
    $hits = Find-Recorded $name
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    $skipped = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' })
    if ($skipped.Count -gt 0) {
        $failures += "$behaviour -> '$name' is NotExecuted ([Fact(Skip=...)]) - a skipped silence test protects nothing. Remove the Skip."
        continue
    }
    $red = @($hits | Where-Object { $_.outcome -eq 'Failed' })
    if ($red.Count -gt 0) {
        $failures += "$behaviour -> '$name' FAILED on the current code. This is a SILENCE behaviour and it must be GREEN today: the code it asserts absent is not emitted yet, so a correct Assert.DoesNotContain passes. A red here means the test asserts the code is PRESENT - do not 'fix' it by making it positive; that would delete the only Invariant-7 protection in this plan."
    }
}

$total = $mustBeRed.Count + $mustRunGreen.Count
if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test census: $($failures.Count) of $total enumerated behaviours are not in their expected state ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
