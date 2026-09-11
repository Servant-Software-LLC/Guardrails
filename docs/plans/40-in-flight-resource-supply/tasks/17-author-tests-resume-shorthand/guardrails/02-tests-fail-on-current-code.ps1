# catches: a hollow test passing off as TDD red. `dotnet test` exits non-zero if ANY selected
#          test fails, so an Assert.True(true) body hides behind its genuinely-failing siblings
#          and the suite-level exit code certifies nothing about it. This binds every enumerated
#          behaviour to a PINNED test method name and requires each to be observed Failed in the
#          runner's own TRX — never stdout, never --list-tests name discovery, both of which a
#          hollow body satisfies exactly as well as a real one (#375).
#
#          BOUNDARY, stated because a green census must not be over-read: this proves each test is
#          COUPLED TO THE CODE PATH (it fails when the implementation is absent), NOT that its
#          assertion is correct. An invoking-then-hollow test is red on stubs, green after, and
#          PASSES this. Closing that needs mutation testing.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'Resume_StagesResetsAndResumesInOneInvocation',
    'Resume_ProducesTheIdenticalJournalAsTheThreeCommandPath',
    'Resume_ProducesTheIdenticalProvenanceRecordAsTheThreeCommandPath'
)

$results = Join-Path $env:TEMP ("gr40-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    # Class-scoped, never the bare plan-wide trait (#455): a task-level filter keyed on the trait
    # asserts the state of every test in the plan instead of this pair's own.
    & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~SupplyResumeShorthandTests" `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not an unbound-behaviour report: distinguish "the run did not happen" from
        # "the tests did not fail", or every behaviour is misreported as unbound.
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). Fix that first; this is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $results_nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($results_nodes.Count -lt 1) {
        # The zero-match hole (#455/#248): with nothing executed the TRX carries no <Results>
        # element, the dotted navigation yields $null, and @($null).Count is 1 — so an unfiltered
        # .Count check would evaluate 1 -lt 1 and never fire. Hence the Where-Object above.
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~SupplyResumeShorthandTests' matched NO tests. A zero-match filter exits 0 and certifies nothing — fix the filter or the class name."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $results_nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Failed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Failed'. A behaviour that passes against the stubs is not TDD red — it is either hollow or already implemented."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Red census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Red census: all $($pinned.Count) pinned behaviour(s) observed Failed against the stubs."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
