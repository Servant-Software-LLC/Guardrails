# catches: a scripted loopback server whose self-test is HOLLOW. This task authors both the fixture and
#          its own test, so there is no stub tree to be red against and no TDD split - which means a
#          plain "the self-test class is green" check is a NAMING floor that `Assert.True(true)`
#          satisfies exactly (Probe B operator 21, the shape measured on a real security wave where a
#          covers-* floor exited 0 over five invariants pinned by nothing).
#
#          The cost of that is not local: tasks 09/10/11/12, 19/20 and 21/22 build EVERY assertion on
#          this fixture, and their writeScope excludes it. A hollow self-test ships a broken server into
#          three task pairs that cannot fix it and will each read the breakage as their own bug.
#
# So this is a PER-TEST CENSUS over the runner's OWN result file (TRX - never stdout, #248): each of
# the five pinned method names must have EXECUTED and PASSED. Unlike task 01's census there is no
# `Failed` half to require, because there is no stub tree here - so state the residual plainly rather
# than implying more: this proves each named scenario RAN and passed, not that its assertion is
# discriminating. That last step is a human read.
#
# Required-present baseline (#478): all five method names measured 0 on the starting tree - none of
# these tests exists yet.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr28-fakesrv-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $trxDir -Force | Out-Null

try {
    $log = & dotnet test $project --nologo `
        --filter 'FullyQualifiedName~FakeOpenAiServerTests' `
        --logger "trx;LogFileName=census.trx" `
        --results-directory $trxDir 2>&1 | Out-String

    Write-Output $log

    # PRECONDITION (early exit): no result file means the run did not happen at all - a crash, a
    # missing project, a filter selecting nothing. Diagnosing that as "hollow tests" would be a lie.
    $trx = Get-ChildItem -Path $trxDir -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $trx) {
        Write-Output "PRECONDITION: no TRX result file was produced. The test run did not happen (a crash, a missing project, or a filter that selected nothing) - this is NOT evidence about the five scenarios."
        exit 1
    }

    [xml]$xml = Get-Content -LiteralPath $trx.FullName -Raw

    # @($null).Count is 1, so a null pipeline would make an EMPTY result set look like one entry and
    # the zero-guard below would never fire. Filter the nulls out explicitly (#478).
    $results = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($results.Count -lt 1) {
        Write-Output "PRECONDITION: the TRX records ZERO test results. Nothing ran, so nothing is proven."
        exit 1
    }

    # One clause per scripted scenario, ACCUMULATED and dumped once - never an exit-1 chain that
    # reports a single gap per attempt and makes the agent rediscover the rest one retry at a time.
    $required = @(
        'NormalCompletion_IsReceivedOverTheLoopbackSocket',
        'ScriptedNotFound_ArrivesAs404',
        'ScriptedToolsRejection_ArrivesAs400',
        'AcceptedConnectionCount_ReportsWhatActuallyHappened',
        'ModelsEndpoint_CanBeScriptedToReturn404'
    )

    $failures = @()
    foreach ($name in $required) {
        $hit = $results | Where-Object { $_.testName -like "*$name*" } | Select-Object -First 1
        if (-not $hit) {
            $failures += "UNRUN SCENARIO: no test named '$name' executed. The fixture's scripted behaviours are what three later task pairs depend on; a scenario with no self-test is a scenario nobody has ever driven."
        }
        elseif ($hit.outcome -ne 'Passed') {
            $failures += "FAILING SCENARIO: '$name' reported '$($hit.outcome)'. The fixture cannot do what the later tasks will ask of it."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Fake-server self-test census failed ($($failures.Count) problem(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        Write-Output ""
        Write-Output "All five pinned scenarios must execute and pass. Each must drive a real HttpClient against the real socket and assert on the RESPONSE - not on a field the fixture set for itself."
        exit 1
    }

    Write-Output "Fake-server census passed: all 5 pinned scenarios executed and passed (of $($results.Count) results)."
    Write-Output ""
    Write-Output "NOTE - what this does NOT prove: that each self-test's assertion is DISCRIMINATING. With no stub tree there is no red half to require, so a test that drives the socket and asserts something trivially true would pass this census. That residual is a human read at review time."
    exit 0
}
finally {
    Remove-Item -LiteralPath $trxDir -Recurse -Force -ErrorAction SilentlyContinue
}
