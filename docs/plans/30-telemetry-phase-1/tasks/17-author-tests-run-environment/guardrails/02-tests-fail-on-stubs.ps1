# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a RunEnvironment the test itself constructed, any assertion that never calls Probe; on the
#          round-trip half, an assertion that the run exited zero or that state/run.json exists). It
#          PASSES against the stub tree and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit would certify the file honest (#375). One entry per enumerated
#          behaviour, each observed Failed in the runner's OWN TRX - never merely discovered by name,
#          which a hollow body satisfies exactly as a comment satisfies a token floor.
#
#          The hollow shape is unusually tempting on this pair, because three of the four probe facts
#          (host, OS, CPU count, memory) are trivially obtainable inside the test itself: a test that
#          reads Environment.MachineName and asserts it equals Environment.MachineName is green,
#          reads as coverage, and asserts nothing about the probe at all. On the round-trip test the
#          equivalent is asserting only ExitCodes.Success - already true on this tree, forever.
#
# TWO INVOCATIONS, ONE FAILURE LIST. This task authors test classes in TWO DIFFERENT PROJECTS -
#          RunEnvironmentTests in Guardrails.Core.Tests (the probe) and RunEnvironmentJournalTests in
#          Guardrails.Integration.Tests (the probe -> RunJournal -> run.json round trip). The second
#          class is the whole reason the pair was widened: the Core tests stop at the first hop and
#          would keep passing if nothing ever persisted the record, and task 18's own prompt names the
#          failure - a stamp placed on the wrong side of the second RunJournal.LoadOrCreate is
#          "silently lost". Censusing only the Core project would leave that class authored with no red
#          half, which is the same hole the CENSUS pair (tasks 23/24) was restructured to close.
#          A single `dotnet test` over the solution would run every other suite in both projects and
#          turn an unrelated red into this task's failure; a single project run would silently certify
#          half the pair. So each project is run with its OWN class-scoped filter, its OWN culture pin
#          and its OWN executed-count guard, and the results ACCUMULATE (#179) into one list dumped at
#          the end - so ONE attempt learns about both halves instead of discovering the second only
#          after fixing the first. Task 18's forward gate runs the same two classes in the same two
#          projects; the two guardrails are the red and green ends of one pair.
#
#          Each --filter names one of THIS pair's OWN test classes, never a plan-wide trait (#455). This
#          plan introduces no trait at all, so both are shape 3 - the class term alone.
#          MEASURED 2026-09-01, not carried forward: 'RunEnvironmentTests' was checked against all 200
#          distinct class names declared in Guardrails.Core.Tests and 'RunEnvironmentJournalTests'
#          against all 152 in Guardrails.Integration.Tests - helper classes INCLUDED, which is the
#          conservative superset, since an FQN substring filter does not know which classes carry tests.
#          Plus every other class this plan authors. Each term is a substring of none of them, and none
#          of them is a substring of either, so both filters are discriminating. (Earlier guardrails in
#          this plan quote 195 and 197 for the Core figure; those were measured at their own authoring
#          time. The COUNT is decoration that ages - the load-bearing claim is the zero-collision one,
#          and a reviewer re-measuring should re-check THAT, not the number.)
#          In particular RunEnvironmentJournalTests does NOT
#          contain RunEnvironmentTests, so the two cannot cross-select even if they shared an assembly -
#          and they do not: the Core filter never reaches the Integration project or the reverse.
#          NO ALTERNATION is needed or used: the two classes live in different PROJECTS, so each gets a
#          single-class filter of its own. If one ever does need an alternation, VSTest takes a BARE
#          pipe - an escaped one matches nothing, exits 0 and reports zero tests, which is a silent
#          green the executed-count check below exists to catch.
#
# TWO DIFFERENT REASONS FOR RED, and the distinction matters when diagnosing a failure:
#            Core          - RunEnvironmentProbe.Probe throws NotImplementedException, so any test that
#                            CALLS it fails.
#            Integration   - nothing stamps the environment onto the journal at all yet (task 18 adds
#                            the recorder and the call site), so a real run completes normally and
#                            state/run.json comes back with a null environment.
#          Neither is a COMPILE failure: JournalDocument.Environment already exists (task 03), so
#          nothing in either file names a type that is absent. A test that does not compile is a mistake
#          to fix, not the intended TDD red - and it shows up here as a missing TRX, which the
#          precondition below diagnoses as such rather than blaming the tests.
#
# NO EXEMPTIONS. Every one of the five behaviours goes through code that cannot yet answer it, so a
#          correct test is red for all five - there is no "already true before the implementation lands"
#          row here, unlike the pairs in this plan whose subject member merely exists unpopulated.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The five names below were read side by side with this task's
#          action.prompt.md tables, which pin each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
#          it - kept anyway, and pinned once per invocation so neither run can inherit a culture the
#          other set (#455), so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$runs = @(
    [ordered]@{
        Tag     = 'core'
        Project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
        Filter  = 'FullyQualifiedName~RunEnvironmentTests'
        # THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
        Manifest = [ordered]@{
            'the probe records host, OS and CPU count'                     = 'TheProbeRecordsHostOsAndCpuCount'
            'the probe records total memory (the unified-memory figure)'   = 'TheProbeRecordsTotalMemory_ForTheUnifiedMemoryComparison'
            'the probe records the concurrency it is GIVEN, not the cores' = 'TheProbeRecordsTheEffectiveConcurrency_NotTheConfiguredOne'
            'the probe records the versions given and nulls the rest'      = 'TheProbeRecordsTheVersionsItIsGiven_AndNullsItIsNotGiven'
        }
        NotRed  = "RunEnvironmentProbe.Probe throws NotImplementedException unconditionally, so a test that CALLS it cannot pass. Green here means the test never called it - most likely it read Environment.MachineName, Environment.ProcessorCount or GC.GetGCMemoryInfo() itself and asserted about its own value, which passes today and forever. Call RunEnvironmentProbe.Probe and assert on the record it returns."
    }
    [ordered]@{
        Tag     = 'integration'
        Project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
        Filter  = 'FullyQualifiedName~RunEnvironmentJournalTests'
        Manifest = [ordered]@{
            'after a real run, run.json carries a non-null environment host' = 'AfterARealRun_RunJsonCarriesANonNullEnvironmentHost'
        }
        NotRed  = "Nothing stamps the environment onto the journal on this tree - task 18 adds the recorder on RunJournal and the call site in RunCommand - so a real run leaves state/run.json with a null environment and an honest test MUST fail. Green here means the test asserted something already true today: that the run exited zero, or that state/run.json exists. Drive a real run through CommandFactory.BuildRootCommand (the shape RunEndTelemetryIngestTests uses), then read the document BACK OFF DISK with JournalReader.Read(RunJournal.PathFor(planDir)) and assert its Environment is non-null and its Environment.Host is non-empty. Never assert against a RunJournal the test is holding or a RunEnvironment it constructed - the claim under test is that the value reached the FILE."
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

    # PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (a COMPILE error
    # in the authored file, the test host failing to start, a wrong project path, or a malformed
    # --filter, which exits 0 SILENTLY). Diagnose THAT. Falling through would print "every behaviour
    # unbound", a confident wrong message aimed at the one artifact a retry agent is allowed to edit.
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
Write-Output "Per-test red census: all $total enumerated behaviours - four in RunEnvironmentTests (Core) and one in RunEnvironmentJournalTests (Integration) - are bound to a pinned test observed Failed against the stub tree."
exit 0
