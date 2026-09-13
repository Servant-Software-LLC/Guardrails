# catches: a hollow test passing off as TDD red. `dotnet test` exits non-zero if ANY selected
#          test fails, so an Assert.True(true) body hides behind its genuinely-failing siblings
#          and the suite-level exit code certifies nothing about it. This binds every enumerated
#          behaviour to a PINNED test method name and requires each to be observed Failed in the
#          runner's own TRX — never stdout, never --list-tests name discovery, both of which a
#          hollow body satisfies exactly as well as a real one (#375).
#
#          BOUNDARY, stated because a green census must not be over-read: this proves each test is
#          COUPLED TO THE CODE PATH (it fails when the wiring is absent), NOT that its assertion
#          is correct. An invoking-then-hollow test is red before, green after, and PASSES this.
#          Closing that needs mutation testing.
#
# real-seam: Scheduler + RunCommand -> SuppliedDrain  bucket=C
#          (#382) These tests drive the PRODUCTION Scheduler through its real factory and
#          `guardrails run` through CommandFactory.BuildRootCommand, and assert an effect only
#          the production path emits. Declared so the proof is locatable rather than recognised
#          by accident.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'TaskBoundary_DrainsFilesStagedWhileTheRunIsExecuting',
    'RunStart_DrainsFilesStagedAfterTheRunHalted',
    'SettledTasksAreNotReRunBySupplying',
    'TaskBoundary_WritesTheSuppliedProvenanceIntoRunJson',
    'Drain_AnnouncesThroughTheRealObserverPipeline'
)

# DECLARED RED-CENSUS EXEMPTION (review 2026-09-11) — ARunThatSuppliesNothing_IsUnchanged.
#   STRUCTURAL REASON: against the UNWIRED code nothing drains, so a run that supplies nothing
#   is trivially unchanged and the test passes. It is the never-weaker requirement and a
#   CORRECT implementation leaves it green; demanding 'Failed' would red a correct plan. The
#   row was previously dropped SILENTLY, which made the genuinely-wrong drop at task 19 read
#   as the same deliberate choice.
#
# The last two rows are NEW (review finding 9). Without them the parts were unit-tested in
# isolation and the PRODUCTION path was never asserted to USE them: a drain that commits the
# file but never writes supplied[] and never raises the event passed tasks 04, 06, 08, 09, 15,
# 16, both plan-root gates and the whole suite. Section 5a calls provenance "the actual
# defence"; it was load-bearing on nothing.

$results = Join-Path $env:TEMP ("gr40-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    # Class-scoped, never the bare plan-wide trait (#455): a task-level filter keyed on the trait
    # asserts the state of every test in the plan instead of this pair's own.
    & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~SuppliedBoundaryWiringTests" `
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
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~SuppliedBoundaryWiringTests' matched NO tests. A zero-match filter exits 0 and certifies nothing — fix the filter or the class name."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $results_nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Failed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Failed'. There are no stubs here — the production types already exist and what is missing is the CALL, so a behaviour that passes against the UNWIRED code is either hollow or already wired."
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
