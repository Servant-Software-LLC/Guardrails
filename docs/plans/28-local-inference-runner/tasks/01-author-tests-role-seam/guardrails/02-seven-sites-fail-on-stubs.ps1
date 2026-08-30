# catches: a test-author task that pins fewer than all seven PromptInvocation sites, or pins one with a
#          HOLLOW body. A suite-level "dotnet test exits non-zero" fires if ANY selected test fails, so a
#          hollow Assert.True(true) PASSES on the stub tree and hides behind its genuinely-failing
#          siblings - measured on a real plan, where a covers-* floor exited 0 over a security test file
#          whose five invariants were pinned by Assert.NotNull (#375). This is the PER-TEST CENSUS: every
#          one of the seven behaviours is bound to a PINNED method name and must be observed Failed in
#          the runner's OWN result file (TRX), never in stdout (#248).
#
# WHY FOUR, NOT SEVEN, MUST FAIL: task 00's stub sets every site to PromptRole.Action, which is CORRECT
#          for three of them. So the three Action sites pass and the four non-Action sites fail. That
#          asymmetry is the discriminator proving the tests are bound to the real code path rather than
#          asserting a constant: a test that passed for all seven would be reading its own stub back.
#
# Required-present baseline (#478): every method name below is measured against the STARTING tree at
#          authoring time and appears 0 times - none of these tests exists yet. The forbidden-present
#          clause (a hollow body) is exempt from the census, as a ban green on arrival is a correct ban.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr28-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $trxDir -Force | Out-Null

try {
    $log = & dotnet test $project --nologo `
        --filter 'FullyQualifiedName~PromptRoleSeamTests' `
        --logger "trx;LogFileName=census.trx" `
        --results-directory $trxDir 2>&1 | Out-String

    Write-Output $log

    # PRECONDITION (early exit, #478): no result file means the run did not happen - a crash, a bad
    # filter, a missing test project. Diagnosing that as "unbound behaviours" would be a lie.
    $trx = Get-ChildItem -Path $trxDir -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $trx) {
        Write-Output "PRECONDITION: no TRX result file was produced. The test run did not happen at all (a crash, a missing project, or a filter that selected nothing) - this is NOT evidence about the seven behaviours."
        exit 1
    }

    [xml]$xml = Get-Content -LiteralPath $trx.FullName -Raw

    # @($null).Count is 1, so a null pipeline would make an EMPTY result set look like one entry and the
    # zero-guard below would never fire. Filter the nulls out explicitly (#478).
    $results = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($results.Count -lt 1) {
        Write-Output "PRECONDITION: the TRX records ZERO test results. Nothing ran, so nothing is proven."
        exit 1
    }

    # One clause per enumerated behaviour, ACCUMULATED and dumped once - never an exit-1 chain that
    # reports a single gap per attempt and makes the agent rediscover the rest one retry at a time.
    $mustFail = @(
        'GuardrailRunner_PassesGuardrailRole',
        'Overwatch_PassesAdvisoryRole',
        'NeedsHumanTriage_PassesAdvisoryRole',
        'CriticalityJudge_PassesAdvisoryRole'
    )
    $mustExist = @(
        'ActionRunner_PassesActionRole',
        'WaveBreakdownInvoker_PassesActionRole',
        'AiMergeResolver_PassesActionRole'
    ) + $mustFail

    $failures = @()

    foreach ($name in $mustExist) {
        $hit = $results | Where-Object { $_.testName -like "*$name*" } | Select-Object -First 1
        if (-not $hit) {
            $failures += "UNBOUND BEHAVIOUR: no test named '$name' ran. The plan's section 3.4 enumerates seven construction sites and each needs its own pinned test - a site with no test is a site nobody checked."
            continue
        }
        if ($mustFail -contains $name) {
            if ($hit.outcome -ne 'Failed') {
                $failures += "NOT RED: '$name' reported '$($hit.outcome)', expected 'Failed'. This site's stub is PromptRole.Action and the correct role is not Action, so a passing test here is not bound to the real code path - it is asserting a constant, and task 02 could satisfy it by doing nothing."
            }
        }
        else {
            if ($hit.outcome -ne 'Passed') {
                $failures += "UNEXPECTEDLY RED: '$name' reported '$($hit.outcome)', expected 'Passed'. The three Action sites are already correct under the stub; if this one fails, the test is not reading the site it names."
            }
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Per-test red census failed ($($failures.Count) problem(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        Write-Output ""
        Write-Output "All seven sites must be pinned by name; the four non-Action ones must be observed Failed against the stub."
        exit 1
    }

    Write-Output "Per-test census passed: all 7 sites pinned, the 4 non-Action sites observed Failed, the 3 Action sites observed Passed."
    Write-Output ""
    Write-Output "NOTE - what this does NOT prove: the census proves each test is COUPLED to the code path (it fails when the role is wrong), not that its assertion is CORRECT. An invoking-then-hollow test (var inv = Capture(); Assert.NotNull(inv);) would be red here and green after task 02, and would pass this check. Closing that needs mutation testing; until then it is a human read."
    exit 0
}
finally {
    Remove-Item -LiteralPath $trxDir -Recurse -Force -ErrorAction SilentlyContinue
}
