# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          about the fixture the test itself just wrote to disk, any assertion that never calls Census;
#          on the CLI half, an `exit == 0` check or an output assertion satisfied by an empty string).
#          It PASSES against the stub tree and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit would certify the file honest (#375). One entry per enumerated
#          behaviour, each observed Failed in the runner's OWN TRX - never merely discovered by name,
#          which a hollow body satisfies exactly as a comment satisfies a token floor.
#
#          The sharpest hollow shape THIS pair invites: Core behaviours 6 and 7 are about FAULT
#          TOLERANCE ("is skipped, not fatal" / "is a reported no-op"), and the cheapest way to write
#          either is to assert that nothing threw. Against a stub that throws NotImplementedException
#          that assertion is red for the wrong reason today and green forever after, whatever Census
#          does with the folder. Both rows are in the manifest below for the same reason as the other
#          five: they must call Census and assert on UnreadableDefinitions / SkippedFolders by NAME.
#
# TWO INVOCATIONS, ONE FAILURE LIST. This task authors test classes in TWO DIFFERENT PROJECTS -
#          AttributionCensusTests in Guardrails.Core.Tests and TelemetryCensusCommandTests in
#          Guardrails.Integration.Tests - because the CLI half is authored HERE, red, rather than by the
#          task that writes the verb. That is the whole point of the pair: a test authored by the task
#          it tests has no red half, so nothing ever observes it failing and a hollow assertion is
#          indistinguishable from a real one. Censusing only the Core project would restore exactly that
#          hole for the two CLI behaviours.
#          A single `dotnet test` over the solution would run every other suite in both projects and
#          turn an unrelated red into this task's failure; a single project run would silently certify
#          half the pair. So each project is run with its OWN class-scoped filter, its OWN culture pin
#          and its OWN executed-count guard, and the results ACCUMULATE (#179) into one list dumped at
#          the end - so ONE attempt learns about both halves instead of discovering the second only
#          after fixing the first. This mirrors task 24's forward gate, which runs the same two classes
#          in the same two projects; the two guardrails are the red and green ends of one pair.
#
#          Each --filter names one of THIS pair's OWN test classes, never a plan-wide trait (#455). This
#          plan introduces no trait at all, so both are shape 3 - the class term alone.
#          MEASURED 2026-09-01, not carried forward: 'AttributionCensusTests' was checked against all
#          200 distinct class names declared in Guardrails.Core.Tests and 'TelemetryCensusCommandTests'
#          against all 152 in Guardrails.Integration.Tests - helper classes INCLUDED, the conservative
#          superset, since an FQN substring filter does not know which classes carry tests. Plus every
#          other class this plan authors. Each term is a substring of none of them, so both filters are
#          discriminating. In particular TelemetryCensusCommandTests does not overlap the shipped
#          TelemetryCommandTests / TelemetryCommandWiringTests, which are longer-established names that
#          do not CONTAIN it. (The Core project's filter never reaches the Integration assembly and vice
#          versa, so the two cannot cross-select.)
#          NO ALTERNATION is needed or used: the two classes live in different PROJECTS, so each gets a
#          single-class filter of its own. If one ever does need an alternation, VSTest takes a BARE
#          pipe - an escaped one matches nothing, exits 0 and reports zero tests, which is a silent
#          green this guardrail's executed-count check below exists to catch.
#
# TWO DIFFERENT REASONS FOR RED, and the distinction matters when diagnosing a failure:
#            Core          - TelemetryAttributionCensus.Census throws NotImplementedException, so any
#                            test that CALLS it fails.
#            Integration   - `telemetry census` is not a registered verb yet (task 24 registers it), so
#                            the real root command cannot reach it and cannot print anything.
#          Neither is a COMPILE failure: nothing in either file names a type that does not exist on this
#          tree. A test that does not compile is a mistake to fix, not the intended TDD red - and it
#          would show up here as a missing TRX, which the precondition below diagnoses as such.
#
# NO EXEMPTIONS in this pair, and that is a deliberate statement rather than an omission. Every one of
#          the nine behaviours goes through code that cannot yet answer it, so a correct test is red for
#          all nine - there is no reflection-only or already-satisfied row here of the kind tasks 01,
#          07, 09, 11, 13 and 19 each had to declare.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The nine names below were read side by side with this task's
#          action.prompt.md tables, which pin each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend
#          on it - kept anyway, and pinned once per invocation so neither run can inherit a culture the
#          other set (#455), so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$runs = @(
    [ordered]@{
        Tag     = 'core'
        Project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
        Filter  = 'FullyQualifiedName~AttributionCensusTests'
        # THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
        Manifest = [ordered]@{
            'a task-grain sentinel row is correct by construction'      = 'ATaskGrainSentinelRow_CountsAsCorrectByConstruction'
            'a script action attempt is correct by construction'        = 'AScriptActionAttempt_CountsAsCorrectByConstruction'
            'a prompt attempt with no provenance is the recording gap'  = 'APromptAttemptWithNoProvenance_CountsAsARecordingGap'
            'a prompt attempt naming a model counts in no category'     = 'APromptAttemptWithProvenance_CountsInNoCategory'
            'the three categories sum to the total naming no model'     = 'TheThreeCategoriesSumToTheTotalNamingNoModel'
            'one malformed task.json is skipped, not fatal'             = 'AMalformedTaskJson_IsSkipped_NotFatal'
            'a plan folder with no journal is a reported no-op'         = 'APlanFolderWithNoJournal_IsAReportedNoOp'
        }
        NotRed  = "TelemetryAttributionCensus.Census throws NotImplementedException unconditionally, so a test that does not fail against it never calls Census - it asserts a tautology and certifies nothing. Call TelemetryAttributionCensus.Census(<a real plan folder written to a temp directory>) and assert on the returned AttributionCensusResult."
    }
    [ordered]@{
        Tag     = 'integration'
        Project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
        Filter  = 'FullyQualifiedName~TelemetryCensusCommandTests'
        Manifest = [ordered]@{
            'the census verb is reachable from the real root command'   = 'TelemetryVerbCensus_IsReachableFromTheCommandFactory'
            'the verb prints the three-way split'                       = 'Census_PrintsTheThreeWaySplit'
        }
        # No backticks anywhere in this string: in a double-quoted PowerShell string a backtick is the
        # ESCAPE character, so a markdown-style quoted token would silently become a control character.
        NotRed  = "The verb 'telemetry census' is not registered on this tree - task 24 registers it - so the root CommandFactory.BuildRootCommand builds cannot reach it and cannot print anything. A test that PASSES here therefore never observed the verb: it asserted on something else (an exit code that is already what it expects, or an output check an empty string satisfies). Invoke through CommandFactory.BuildRootCommand with the literal argv tokens 'telemetry','census',<folder>, and assert on what the verb PRINTS - for Census_PrintsTheThreeWaySplit, over a fixture whose four numbers are all DIFFERENT, so a verb printing one figure three times cannot pass."
    }
)

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, across BOTH projects, so ONE
# attempt learns every gap.
$failures = @()
$total    = 0

foreach ($run in $runs) {
    $project = $run.Project
    $filter  = $run.Filter
    $total  += $run.Manifest.Count

    # PRECONDITION - a missing project is not a finding about the tests, and continuing would report the
    # other half as if this half had been checked.
    if (-not (Test-Path $project)) {
        Write-Output "PRECONDITION: $project not found - this guardrail censuses this task's tests in BOTH projects and cannot run without it."
        exit 1
    }

    # Per-invocation culture pin (#455) - never inherited from the previous iteration.
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'

    $resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID-$($run.Tag)"
    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

    Write-Output ""
    Write-Output "=== $project --filter $filter ==="
    $out = dotnet test $project --filter $filter --nologo `
           --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
    $out | ForEach-Object { Write-Output $_ }

    # PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
    # start, a COMPILE error in the authored file, wrong project path, or a malformed --filter, which
    # exits 0 SILENTLY). Diagnose THAT. Falling through would print "every behaviour unbound", a
    # confident wrong message aimed at the one artifact a retry agent is allowed to edit.
    $trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) {
        Write-Output "no .trx under $resultsDir - the test run did not happen for $project (the authored test file did not COMPILE, the test host failed to start, the project path is wrong, or the --filter is malformed, which exits 0 with no results). If it is a compile error, FIX IT: the tests must compile and fail, not fail to compile. This is NOT a finding that the tests are hollow: do NOT rewrite them to make this message go away."
        exit 1
    }

    # DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
    # The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
    # navigation yields $null, and @($null).Count is 1 - so the bare @(...) form would make the guard
    # below evaluate 1 -lt 1 and NEVER FIRE.
    $xml      = [xml](Get-Content $trx.FullName -Raw)
    $recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($recorded.Count -lt 1) {
        Write-Output "the TRX for $project records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
        exit 1
    }

    foreach ($behaviour in $run.Manifest.Keys) {
        $name = $run.Manifest[$behaviour]
        # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
        # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
        $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
        $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
        if ($hits.Count -lt 1) {
            $failures += "[$($run.Tag)] $behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter '$filter')"
            continue
        }
        $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
        if ($notRed.Count -gt 0) {
            $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "[$($run.Tag)] $behaviour -> '$name' is $seen on the STUB tree, not Failed. $($run.NotRed) ('NotExecuted' = [Fact(Skip=...)].)"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $total enumerated behaviours are not proven RED on the stub ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output ""
Write-Output "Per-test red census: all $total enumerated behaviours - seven in AttributionCensusTests (Core) and two in TelemetryCensusCommandTests (Integration) - are bound to a pinned test observed Failed against the stub tree."
exit 0
