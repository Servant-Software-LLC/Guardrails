# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, any assertion that never drives the real renderer).
#          It PASSES against the current tree and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit certifies the file honest (#375). One entry per enumerated
#          behaviour in this task's action prompt (its Group A table), each observed Failed in the
#          runner's OWN TRX - never merely discovered by name, which a hollow body satisfies.
#
# THIS CENSUS IS THE ONLY THING THAT CATCHES THE attempt-route.log TRAP. `attempt-route.log` is
# ALREADY in the rendered task page today (AppendAttemptFiles inlines every file in an attempt
# directory as a <select> option plus a hidden <pre>), so a test asserting the page merely CONTAINS
# that filename is green on arrival - the #478 pre-satisfaction defect, wearing a test's clothes. The
# prompt therefore demands an <a> element AND a label naming the model question, and this census is
# what proves the authored test actually fails today rather than accidentally passing.
#
# GROUP B IS DELIBERATELY ABSENT FROM THE MANIFEST. The prompt's
# RunLevelIndex_StillCarriesTaskStatusAndDescription_SoTheModelColumnIsAdditive pin PASSES against the
# current tree by design - it is a regression pin, not evidence of #524. Censusing it would demand it
# be red, which would be a demand to break working behaviour. The census "lists the enumerated
# behaviours only; a test outside it is not the census's business" (catalogue, per-test red census).
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so unlike dotnet.md 4.3 the
# guard does not depend on it - keep it anyway so the logged summary is readable and the pair stays
# copy-pasteable. NO -v q anywhere: pointless here (nothing is re-emitted) and it propagates onto
# forward checks by cloning (#462).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=BacklogSlate&FullyQualifiedName~ModelInRowTests'   # SAME string as the pair's forward half (task 05)

# FILTER DISCRIMINATION (dotnet.md 4.3): 'ModelInRowTests' was measured against every one of the 282
# distinct *Tests class names under tests/ and matches NONE of them - in fact NO test class anywhere
# under tests/ contains the substring 'Row' at all, so this is maximally discriminating. The
# model-named neighbours (ActionModelOverrideTests, ModelsUsedSummaryTests, ObservedModelCaptureTests,
# AttemptModelRenderingTests, AttemptModelDisclosureTests) do not contain it, and neither does any
# plan-25 sibling class.

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
# Cross-checked BY HAND against tasks/04-author-tests-model-in-row/action.prompt.md (Group A) - the
# prompt<->manifest agreement is NOT mechanically enforced (measured on plan 24: validate exits 0
# either way).
$manifest = [ordered]@{
    'the run-level index declares a Model column'                        = 'RunLevelIndex_HasAModelColumn_BesideStatusAndDescription'
    'each task row carries ITS OWN model, not one page-wide value'       = 'RunLevelIndex_ShowsTheModelThatActuallyRan_PerTask'
    'a route/model mismatch is disclosed, not silently flattened'        = 'RunLevelIndex_DisclosesTheMismatch_WhenTheRouteRequestedADifferentModel'
    'a task with no recorded model does not inherit its neighbour''s'    = 'RunLevelIndex_MarksATaskWithNoRecordedModel_RatherThanRepeatingItsNeighbours'
    'the task page LINKS attempt-route.log by name, with a label'        = 'TaskPage_LinksAttemptRouteLogByName_WithALabelSayingWhatItAnswers'
    'the live table cell names the model, and both on a mismatch'        = 'LiveTableModelCell_NamesTheModel_AndDisclosesTheRouteMismatch'
    'the live table cell is a placeholder, never blank, with no model'   = 'LiveTableModelCell_RendersAPlaceholder_WhenNoModelIsRecorded'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, malformed --filter which exits 0 SILENTLY). Diagnose THAT. Falling through
# would print "every behaviour unbound", a confident wrong message aimed at the one artifact the retry
# agent is allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult)
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the CURRENT tree, not Failed. LogSiteRenderer.cs renders the word 'model' NOWHERE today and LiveRunObserver.ModelCell is a NotImplementedException stub, so a test for either that passes is asserting something the code already does - most likely it checked that the page contains the bare string 'attempt-route.log', which is already inlined as a <select> option. Assert the <a> element and its label, and drive ModelCell directly. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the current tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
