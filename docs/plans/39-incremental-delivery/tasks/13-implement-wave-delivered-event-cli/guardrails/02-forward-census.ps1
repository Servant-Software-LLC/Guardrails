# catches: a DECLARED-EXEMPT census row quietly ceasing to exist. Its sibling red census excuses
#          these names from being Failed (a correct implementation leaves them green) but NOT from
#          existing; this is the other half of that bargain — every pinned behaviour, exempt or
#          not, observed Passed in the runner's own TRX once the implementation has landed.
#
#          FORWARD polarity, and its boundary stated: a forward census cannot see a hollow body
#          (a hollow test passes). What it CAN see is a test that was never written, or one that
#          no longer runs.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'EveryCliDecorator_ForwardsTheEvent',
    # A-N6 (review of 1a809bce): the two renderers print the delivery. Task 11's 03 census pins both red.
    'ConsoleRunObserver_PrintsTheDeliveredWaveAndCommit',
    'LiveRunObserver_PrintsTheDeliveredWaveAndCommit'
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
    $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($nodes.Count -lt 1) {
        # The zero-match hole (#455/#248): with nothing executed the TRX carries no <Results>
        # element, so the dotted navigation yields $null and @($null).Count is 1 — an unfiltered
        # .Count check would evaluate 1 -lt 1 and never fire. Hence the Where-Object above.
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~WaveDeliveredCliForwardingTests' matched NO tests. A zero-match filter exits 0 and certifies nothing — fix the filter or the class name."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Passed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Passed'."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Census: all $($pinned.Count) pinned behaviour(s) observed Passed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}