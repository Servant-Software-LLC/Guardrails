# catches: a HOLLOW test — named for the behaviour, body a tautology — which PASSES against the empty
#          default-interface body and hides behind its genuinely-failing siblings, so a suite-level
#          non-zero exit certifies the whole file honest (#375). One entry per enumerated behaviour,
#          each observed Failed in the runner's OWN TRX.
#          It also catches the name-only forwarding guard this plan exists to retire: with the
#          three-argument member added, a census that compares only member NAMES is GREEN on arrival,
#          so it shows up here as a pinned behaviour that was not Failed.
#
#          TWO PROJECTS, deliberately (design 41 §6 / plan review): this task's evidence is split
#          across Guardrails.Core.Tests and Guardrails.Integration.Tests, and a single project path
#          would silently miss half of it while reporting success over the half it ran. Each suite
#          carries its OWN zero-match precondition, and every pinned behaviour names the suite it must
#          be found in — a test written into the wrong project is a finding, not a pass.
#
# INVERSE polarity: a NON-zero exit is this check's success, so it does NOT re-emit failure detail
# (#179/§4.2), and each suite's zero-match guard runs BEFORE its outcome is trusted (§4.3) — a test host
# that never started also exits non-zero, which is this check's success condition.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guards below read is LOCALIZED (#455)

# Class terms are MANDATORY; the plan trait is the conjunct (#455). Measured: each class name matches
# exactly ONE class declaration under tests/**, and 'SuppliedObserverEventTests' is NOT a substring of
# 'SuppliedObserverCliForwardingTests'. The alternation is parenthesised with a BARE '|': VSTest treats
# a backslash as its escape character, and '\|' is rejected as an invalid condition — zero tests, exit
# 0, a silent green. Task 13 copies both $filter strings VERBATIM.
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
    @{ Suite = 'Integration'; Name = 'EveryTransparentDecorator_DeclaresEveryIRunObserverMember_WithItsExactParameterList' }
)

# DECLARED RED-CENSUS EXEMPTIONS. Each is asserted to EXIST below, and task 13's forward census requires
# each observed Passed.
#   ADecoratorThatDropsTheEvent_IsCaught
#   NullObserver_DoesNotDeclareTheEvent_BecauseItsContractIsToSwallowEverything
#     STRUCTURAL REASON: both are self-contained NEGATIVE controls. They assert that a type declaring
#     NEITHER form hears nothing — which is true against the empty default body added by this task and
#     stays true after task 13. A correct test has nothing to be red about, and demanding Failed would
#     force a test coupled to the stub instead of to the swallow it documents.
$mustExist = @(
    @{ Suite = 'Core'; Name = 'ADecoratorThatDropsTheEvent_IsCaught' },
    @{ Suite = 'Core'; Name = 'NullObserver_DoesNotDeclareTheEvent_BecauseItsContractIsToSwallowEverything' }
)

$results = Join-Path $env:TEMP ("gr41-t12-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null   # --results-directory is NOT cleared between runs

try {
    $observed = @()
    $anyNonZeroExit = $false

    foreach ($suite in $suites) {
        $suiteDir = Join-Path $results $suite.Name
        New-Item -ItemType Directory -Path $suiteDir -Force | Out-Null

        # No -v q on a test command (#462) — the two halves of this pair stay copy-pasteable only if
        # neither carries it.
        $out = & dotnet test $suite.Project -c Debug --nologo `
            --filter $suite.Filter `
            --logger "trx;LogFileName=census.trx" --results-directory $suiteDir 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { $anyNonZeroExit = $true }
        Write-Output $out

        # PER-SUITE zero-match guard, keyed on the EXECUTED count (Passed + Failed). "Total:" would also
        # count [Skip]ped tests, so a fully-skipped suite would read as red.
        $ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
                ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
        if ($ran -lt 1) {
            Write-Output "ZERO tests executed in the $($suite.Name) suite — the TDD-red proof certified nothing there. The --filter '$($suite.Filter)' matched no tests, is malformed, every match is [Skip]ped, or the test host failed to start (read the log above). This is NOT a tautology finding: do NOT rewrite the tests."
            exit 1
        }

        $trx = Get-ChildItem -Path $suiteDir -Filter '*.trx' -File | Select-Object -First 1
        if (-not $trx) {
            Write-Output "PRECONDITION: no TRX was produced for the $($suite.Name) suite — that run did not happen (a build break, or a bad filter). This is NOT a statement about the pinned behaviours."
            exit 1
        }

        # Dotted navigation, never SelectNodes('//UnitTestResult'): the TRX carries a default xmlns and
        # an XPath query without a namespace manager returns NOTHING — a silent zero-result census.
        [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
        # @($null).Count is 1 when zero tests ran (no <Results> element at all), so the Where-Object is
        # what lets the count guard below fire at all.
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
        # Anchored on '.<Name>' followed by '(' or end-of-string: testName is fully qualified and a
        # [Theory] row appends its data. -cmatch, not -match: a differently-cased sibling must not
        # satisfy the entry, and the anchor stops a LONGER sibling name from satisfying a shorter one.
        $matching = @($observed | Where-Object {
            $_.Suite -eq $entry.Suite -and $_.TestName -cmatch ('\.' + [regex]::Escape($entry.Name) + '(\(|$)')
        })
        if ($matching.Count -lt 1) {
            $elsewhere = @($observed | Where-Object { $_.TestName -cmatch ('\.' + [regex]::Escape($entry.Name) + '(\(|$)') })
            if ($elsewhere.Count -gt 0) {
                $failures += "[$($entry.Name)] ran in the $($elsewhere[0].Suite) suite, but this behaviour belongs in $($entry.Suite). Guardrails.Core.Tests references Guardrails.Core alone — a CLI decorator cannot even be named there — so moving a test between the projects changes what it can prove."
            }
            else {
                $failures += "[$($entry.Name)] NOT FOUND in the $($entry.Suite) TRX — the prompt pins this behaviour to a test of that name; it was never executed."
            }
            continue
        }
        # EVERY matching row, not just the first: a [Theory] whose rows disagree is a half-bound
        # behaviour, and -First 1 would report whichever row the runner happened to list.
        foreach ($node in $matching) {
            if ($node.Outcome -ne 'Failed') {
                # 'NotExecuted' is how a skipped test spells itself — -ne 'Failed' catches it too.
                $failures += "[$($node.TestName)] outcome was '$($node.Outcome)', expected 'Failed'. A behaviour that passes against the empty three-argument default is not TDD red — it is hollow, or (for a forwarding census) it still compares member NAMES instead of parameter lists, which the added overload satisfies for free."
            }
        }
    }

    # The DECLARED exemptions are exempt from the RED requirement, not from EXISTING. A negative control
    # that is never written is not "green because correct" — it is absent, and absence is how a
    # never-weaker guarantee quietly stops being asserted anywhere.
    foreach ($entry in $mustExist) {
        $matching = @($observed | Where-Object {
            $_.Suite -eq $entry.Suite -and $_.TestName -cmatch ('\.' + [regex]::Escape($entry.Name) + '(\(|$)')
        })
        if ($matching.Count -lt 1) {
            $failures += "[$($entry.Name)] NOT FOUND in the $($entry.Suite) TRX. It is DECLARED-EXEMPT from the red census (a negative control is green by construction), NOT exempt from existing. Keep it, migrated to the three-argument call."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Red census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    if (-not $anyNonZeroExit) {
        Write-Output "every suite exited 0 — with nine pinned behaviours observed Failed that should be impossible; read the logs above before trusting either signal."
        exit 1
    }

    Write-Output "Red census: all $($pinned.Count) pinned behaviour(s) observed Failed across both suites; both declared-exempt controls present."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
