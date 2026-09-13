# catches: an EMPTY second test class. This task authors TWO classes and the first census
#          (02-…) filters on ~SuppliedObserverEventTests, which does not select this one — so
#          before this file existed, an empty SuppliedObserverCliForwardingTests.cs (or no file
#          at all) passed every guardrail on task 07, and task 09's only gate then ran a class
#          with nothing in it.
#
#          It runs against Guardrails.Integration.Tests, the ONLY test project that references
#          Guardrails.Cli. Guardrails.Core.Tests references Guardrails.Core alone, so the CLI
#          decorators are invisible there and this class cannot compile in that project — see
#          tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs, which says so.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'ConsoleRunObserver_ForwardsTheEvent',
    'LiveRunObserver_RendersTheEventInTheLiveTable',
    'NoUi_PrintsTheSuppliedResourcesLine',
    'EveryCliDecorator_DeclaresAndForwardsTheEvent'
)

$results = Join-Path $env:TEMP ("gr40-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    # Class-scoped, never the bare plan-wide trait (#455).
    & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~SuppliedObserverCliForwardingTests" `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not an unbound-behaviour report: distinguish "the run did not happen"
        # from "the tests did not fail", or every behaviour is misreported as unbound.
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). Fix that first; this is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $results_nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($results_nodes.Count -lt 1) {
        # The zero-match hole (#455/#248): with nothing executed the TRX carries no <Results>
        # element, the dotted navigation yields $null, and @($null).Count is 1 — so an
        # unfiltered .Count check would evaluate 1 -lt 1 and never fire. Hence the Where-Object.
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~SuppliedObserverCliForwardingTests' matched NO tests. A zero-match filter exits 0 and certifies nothing — fix the filter or the class name."
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