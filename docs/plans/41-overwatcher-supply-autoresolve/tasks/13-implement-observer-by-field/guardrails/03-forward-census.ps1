# catches: a pinned or DECLARED-EXEMPT census row quietly ceasing to exist. Task 12's red census
#          excuses the two negative controls from being Failed but NOT from existing; this is the other
#          half of that bargain — every pinned behaviour, exempt or not, observed Passed in the runner's
#          OWN TRX now that the implementation landed. Without it, an attempt could turn 02 green by
#          deleting the rows it cannot satisfy: 02 only requires that the tests it DOES run pass.
#          Each behaviour is checked in the SUITE it belongs to, because the two projects prove
#          different halves and a test that migrated between them proves less than it claims.
#
#          FORWARD polarity, and its boundary stated: a forward census cannot see a hollow body (a
#          hollow test passes here too). What it CAN see is a test deleted, renamed, stripped of the
#          plan trait, moved to the other project, or left [Skip]ped.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$suites = @(
    @{
        Name    = 'Core'
        Project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
        Filter  = 'Category=OverwatchSupply&FullyQualifiedName~SuppliedObserverEventTests'
    },
    @{
        Name    = 'Integration'
        Project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
        Filter  = 'Category=OverwatchSupply&(FullyQualifiedName~SuppliedObserverCliForwardingTests|FullyQualifiedName~ObserverForwardingSweepTests)'
    }
)

$pinned = @(
    @{ Suite = 'Core';        Name = 'Event_CarriesThePathsTheCommitAndTheSupplier' },
    @{ Suite = 'Core';        Name = 'EveryDecorator_ForwardsTheEventWithTheSupplier' },
    @{ Suite = 'Core';        Name = 'TheTwoArgumentMember_IsReplacedNotOverloaded' },
    @{ Suite = 'Integration'; Name = 'ConsoleRunObserver_ForwardsTheEventWithTheSupplier' },
    @{ Suite = 'Integration'; Name = 'LiveRunObserver_RendersTheSupplierInTheLiveTable' },
    @{ Suite = 'Integration'; Name = 'NoUi_PrintsTheSuppliedResourcesLineNamingTheSupplier' },
    @{ Suite = 'Integration'; Name = 'EveryCliDecorator_DeclaresAndForwardsTheEventWithTheSupplier' },
    @{ Suite = 'Integration'; Name = 'OverwatcherSupply_ReachesTheEventsJsonlRowAndTheNoUiLine' },
    @{ Suite = 'Integration'; Name = 'EveryTransparentDecorator_DeclaresEveryIRunObserverMember_WithItsExactParameterList' },
    # Task 12's declared red-census exemptions — the two negative controls. Green before this task and
    # green after: NullObserver and a non-declaring decorator must still hear nothing.
    @{ Suite = 'Core';        Name = 'ADecoratorThatDropsTheEvent_IsCaught' },
    @{ Suite = 'Core';        Name = 'NullObserver_DoesNotDeclareTheEvent_BecauseItsContractIsToSwallowEverything' }
)

$results = Join-Path $env:TEMP ("gr41-t13-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null   # --results-directory is NOT cleared between runs

try {
    $observed = @()

    foreach ($suite in $suites) {
        $suiteDir = Join-Path $results $suite.Name
        New-Item -ItemType Directory -Path $suiteDir -Force | Out-Null

        & dotnet test $suite.Project -c Debug --nologo `
            --filter $suite.Filter `
            --logger "trx;LogFileName=census.trx" --results-directory $suiteDir 2>&1 | Out-String | Write-Output

        $trx = Get-ChildItem -Path $suiteDir -Filter '*.trx' -File | Select-Object -First 1
        if (-not $trx) {
            # PRECONDITION, not an unbound-behaviour report: "the run did not happen" is a different
            # fact from "the tests did not behave as required".
            Write-Output "PRECONDITION: no TRX was produced for the $($suite.Name) suite — that run did not happen (a build break, or a bad filter). Fix that first; this is NOT a statement about the pinned behaviours."
            exit 1
        }

        # Dotted navigation: the TRX carries a default xmlns, so SelectNodes('//UnitTestResult') returns
        # nothing without a namespace manager — a silent zero-result census.
        [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
        # @($null).Count is 1 when zero tests ran (no <Results> element), so the Where-Object is what
        # lets the guard below fire at all.
        $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })
        if ($nodes.Count -lt 1) {
            Write-Output "PRECONDITION: the filter '$($suite.Filter)' matched NO tests in the $($suite.Name) TRX. A zero-match filter exits 0 and certifies nothing — check the class names and the Category=OverwatchSupply trait."
            exit 1
        }

        $observed += $nodes | ForEach-Object {
            [pscustomobject]@{ Suite = $suite.Name; TestName = [string]$_.testName; Outcome = [string]$_.outcome }
        }
    }

    $failures = @()
    foreach ($entry in $pinned) {
        $matching = @($observed | Where-Object {
            $_.Suite -eq $entry.Suite -and $_.TestName -cmatch ('\.' + [regex]::Escape($entry.Name) + '(\(|$)')
        })
        if ($matching.Count -lt 1) {
            $failures += "[$($entry.Name)] NOT FOUND in the $($entry.Suite) TRX — task 12 pinned this behaviour to a test of that name, in that project. It was deleted, renamed, moved, or it lost the Category=OverwatchSupply trait. Restore it; do not re-point this census."
            continue
        }
        foreach ($node in $matching) {
            if ($node.Outcome -ne 'Passed') {
                $failures += "[$($node.TestName)] outcome was '$($node.Outcome)', expected 'Passed'."
            }
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Forward census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Forward census: all $($pinned.Count) pinned behaviour(s) observed Passed across both suites."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
