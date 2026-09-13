# catches: an EMPTY second test class. This task authors TWO classes and the first census (02-…)
#          filters on ~WaveDeliveredEventTests, which does not select this one — so before this
#          file existed, a single `[Fact] public void Placeholder() => Assert.True(true);` in
#          WaveDeliveredCliForwardingTests.cs compiled (01 OK), was invisible to the census
#          (02 OK), and then satisfied task 13's ONLY guardrail: MEASURED `Failed: 0, Passed: 1`,
#          zero-match guard content, exit 0, with src/Guardrails.Cli/ untouched.
#
#          It runs against Guardrails.Integration.Tests, the ONLY test project referencing
#          Guardrails.Cli. Guardrails.Core.Tests references Guardrails.Core alone — 0 `using
#          Guardrails.Cli` across its files against 156 there — and says so in its own
#          PlanSource/PlanSourceWiringTests.cs:21.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'EveryCliDecorator_ForwardsTheEvent'
)

$results = Join-Path $env:TEMP ("gr39-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    # Class-scoped, never the bare plan-wide trait (#455).
    & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~WaveDeliveredCliForwardingTests" `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not an unbound-behaviour report: "the run did not happen" is a different
        # fact from "the tests did not behave as required".
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). Fix that first; this is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $results_nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($results_nodes.Count -lt 1) {
        # The zero-match hole (#455/#248): with nothing executed the TRX carries no <Results>
        # element, so the dotted navigation yields $null and @($null).Count is 1 — an unfiltered
        # .Count check would evaluate 1 -lt 1 and never fire. Hence the Where-Object above.
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~WaveDeliveredCliForwardingTests' matched NO tests. A zero-match filter exits 0 and certifies nothing — fix the filter or the class name."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $results_nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Failed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Failed'."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Census: all $($pinned.Count) pinned behaviour(s) observed Failed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}