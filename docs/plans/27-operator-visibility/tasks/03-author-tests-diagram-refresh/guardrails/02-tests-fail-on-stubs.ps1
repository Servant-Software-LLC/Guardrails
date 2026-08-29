# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, any assertion that never drives the real renderer).
#          It PASSES against the current tree and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit certifies the file honest (#375). One entry per enumerated
#          behaviour in this task's action prompt, each observed Failed in the runner's OWN TRX -
#          never merely discovered by name, which a hollow body satisfies exactly as a comment
#          satisfies a token floor.
#
#          THIS IS THE POINT OF THIS TASK. Before the 03/04 split, one collapsed task authored these
#          tests AND the renderer change, and NO guardrail pinned any of the five behaviours - the
#          names below appeared ONLY in an action prompt. Positive control, measured 2026-08-29 on
#          the pre-split tree: `git grep -F <name> -- docs/plans/27-operator-visibility` returned the
#          prompt and nothing else, for all five; `GR_LIVE_POLL_MS` and `gr-live-offline` appeared in
#          a guardrail only inside a FAILURE-MESSAGE STRING, never in an assertion. So the cheapest
#          wrong implementation was: retire the three permitted http-equiv assertions, LEAVE THE META
#          REFRESH IN PLACE, author one green test in a class named DiagramRefreshTests carrying the
#          trait - every guardrail passes, #523 never done, plan green. This census is what makes
#          that fail.
#
# THE DECLARED EXEMPTION - one enumerated behaviour is checked in the OPPOSITE polarity, and it is
# NOT silently dropped. `SourceSha256AndEmbeddedSource_AreUnchangedByTheLiveUpdateChanges` PASSES
# against the unmodified renderer, for a STRUCTURAL reason: it is a regression pin on behaviour that
# already holds (measured on the current tree - HtmlDiagramRenderer.cs emits
# `<!-- guardrails:graph v1 source-sha256=__SOURCE_SHA256__ -->` as the FIRST template line and
# embeds the source verbatim in `<script type="text/plain" id="graph-source">`), not evidence of
# #523. Demanding it be red would be demanding the task break working behaviour. So it is censused
# for `Passed` in the SAME TRX, in $mustPass below, rather than being removed from the manifest and
# left to prose. Its residual is declared honestly at the bottom of this header.
#
# NAMED RESIDUALS, not gaps this closes:
#  1. The census proves each test is COUPLED to the code path (it fails when the behaviour is
#     absent), NOT that its assertion is CORRECT. An invoking-then-hollow test - one that really
#     calls Render and then asserts something trivially true of the returned string - would be red
#     today and green after, and passes this check. Closing that needs mutation testing; until then
#     it is a human read at /guardrails-review.
#  2. The $mustPass clause is a GREEN-polarity census and is therefore the weaker half by
#     construction: a test named SourceSha256AndEmbeddedSource_AreUnchangedByTheLiveUpdateChanges
#     whose body is Assert.True(true) satisfies it. The red half cannot be gamed that way (a hollow
#     test passes, and this check demands Failed); the green half can. Same residual task 04's
#     neighbour-coverage census declares.
#  3. The red half demands Failed, NOT "Failed for the right reason". Four bodies of
#     `Assert.Fail("todo")` satisfy every clause here. This is a real hole and it is left open
#     DELIBERATELY, because it is LOUD rather than silent: such tests can never go green, so task 04
#     cannot satisfy its own forward check, cannot edit the file (out of its writeScope), and halts
#     at needsHuman with a named test - it does NOT ship #523 undone behind a green plan, which is
#     the blocker this split exists to close. Closing it too would take a third, source-shape
#     guardrail asserting each pinned method's body drives `HtmlDiagramRenderer.Render(` - anchored
#     on the dotted CALL, not the bare name (#76), and shipping a committed two-sided sample pair
#     under tasks/03-author-tests-diagram-refresh/samples/ (#468). That is a deliberate scope call
#     for the plan owner, not an oversight: it trades a third check and a sample pair for turning a
#     loud downstream halt into an immediate one.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so unlike dotnet.md 4.3 the
# guard does not depend on it - keep it anyway so the logged summary is readable and the pair stays
# copy-pasteable. NO -v q anywhere: pointless here (nothing is re-emitted) and it propagates onto
# forward checks by cloning (#462).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=BacklogSlate&FullyQualifiedName~DiagramRefreshTests'   # SAME string as the pair's forward half (task 04)

# FILTER DISCRIMINATION (dotnet.md 4.3): re-measured 2026-08-29 against every one of the 285 distinct
# *Tests class names under tests/ - 'DiagramRefreshTests' is a substring of NONE of them. The nearest
# neighbours are HtmlDiagramRendererTests, OnTheFlyDiagramTests and ContainerDiagramTests (the only
# three containing 'Diagram' at all), and no class this plan itself authors contains it either
# (ServeDiagramTests, ModelInRowTests). Once this task authors exactly one class with that name the
# filter selects exactly it.
#
# THE TRAIT TERM IS LOAD-BEARING AND IS NOT A NO-OP HERE: tests/Guardrails.Core.Tests ALREADY carries
# Category=BacklogSlate on SampleVerifierTests (6 occurrences, landed by plan 26), so the trait alone
# selects a foreign class. The conjunction is what makes this filter this task pair's own (#455) -
# and it is also why the trait must actually be on the authored tests: without it the conjunction
# selects ZERO and the precondition below fires.

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
# Cross-checked BY HAND against tasks/03-author-tests-diagram-refresh/action.prompt.md (Group A) -
# the prompt<->manifest agreement is NOT mechanically enforced (measured on plan 24: validate exits 0
# either way).
#
# BASELINE (#478), measured on the current tree before this task runs: all four names occur 0 times
# under tests/, so every clause below is honestly RED on arrival - none is pre-satisfied.
$mustFail = [ordered]@{
    'the during-run page performs NO whole-document reload'          = 'DuringRunPage_HasNoMetaRefresh_SoPanZoomAndScrollSurvive'
    'a named poll constant exists live and is absent when settled'   = 'LivePoll_IsPresentDuringTheRun_AndAbsentOnTheFinalSettledPage'
    'the poll interval is bounded below at 5000ms'                   = 'LivePollInterval_IsAtLeastFiveSeconds_ForADagThatChangesAtTaskBoundaries'
    'an unpollable file:// page says so, hidden until a poll fails'  = 'FileViewFallback_IsPresentAndHidden_SoAnUnpollablePageSaysItIsNotLive'
}

# THE DECLARED EXEMPTION, censused in the GREEN polarity (see the header). Same TRX, same loop shape,
# `-ne 'Passed'` instead of `-ne 'Failed'`.
$mustPass = [ordered]@{
    'the provenance line and embedded source are pinned against chrome drift' = 'SourceSha256AndEmbeddedSource_AreUnchangedByTheLiveUpdateChanges'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION 1 - the ONE legitimate early-exit shape. No TRX at all means the run never happened
# (host failed to start, wrong project path). Diagnose THAT. Falling through would print "every
# behaviour unbound", a confident wrong message aimed at the one artifact the retry agent is allowed
# to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, or a wrong project path). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# PRECONDITION 2 - THE ZERO-RECORD GUARD, and the `| Where-Object { $_ }` in it is load-bearing.
# MEASURED 2026-08-29, and this is a live first-attempt path for THIS task, not a hypothetical:
# `dotnet test tests/Guardrails.Core.Tests --filter 'Category=BacklogSlate&FullyQualifiedName~DiagramRefreshTests'`
# against the tree this task is HANDED (DiagramRefreshTests.cs does not exist yet) exits 0, prints
# "No test matches the given testcase filter", and STILL WRITES A TRX - one carrying no <Results>
# element at all. On such a TRX `@($xml.TestRun.Results.UnitTestResult)` is a ONE-element array
# holding $null, so `.Count` is 1 and the un-guarded `-lt 1` test is DEAD: the census falls straight
# through and reports "4 of 4 behaviours are not proven RED", pointing the retry agent at tests that
# may be perfectly fine. Filtering out the $null makes Count 0 and the guard fire. Measured, all
# three cases: <Results/> empty -> old 1 / new 0; no <Results> element -> old 1 / new 0; one real
# record -> old 1 / new 1 (unchanged, so the fix costs nothing on the healthy path).
$xml      = [xml](Get-Content $trx.FullName -Raw)   # DOTTED navigation - the TRX has a default xmlns,
                                                    # so SelectNodes('//UnitTestResult') finds nothing.
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing (dotnet test exits 0 SILENTLY in that case and still writes this empty TRX), or every match is [Skip]ped out of execution. Most likely DiagramRefreshTests.cs does not exist yet, the class is named something else, or the tests are missing [Trait(`"Category`", `"BacklogSlate`")] - the filter needs BOTH terms. This is NOT a finding about the tests' CONTENT: do NOT rewrite assertions to fix it."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()

foreach ($behaviour in $mustFail.Keys) {
    $name = $mustFail[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, misspelled, or missing the [Trait(`"Category`", `"BacklogSlate`")] the filter needs)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the CURRENT tree, not Failed. HtmlDiagramRenderer.cs emits <meta http-equiv=`"refresh`" content=`"3`"> during a run and contains the tokens GR_LIVE_POLL_MS and gr-live-offline NOWHERE (measured: 0 occurrences in src/ and tests/), so a test for any of these that PASSES is asserting something the current code already does - most likely it checked only the ABSENCE half of a contrast, or built the string it then inspected instead of calling Render. Drive HtmlDiagramRenderer.Render and assert on what it returns. ('NotExecuted' = [Fact(Skip=...)], which is deletion with extra steps.)"
    }
}

foreach ($behaviour in $mustPass.Keys) {
    $name = $mustPass[$behaviour]
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran. This is the ONE pin the prompt marks as already-green, and it is required precisely BECAUSE it is green: it is the only thing that would notice the next task moving the provenance line or re-encoding the embedded source, which would make graph --check report every plan in the repo stale. It is exempt from the RED census, not from the file."
        continue
    }
    $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notGreen.Count -gt 0) {
        $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the CURRENT tree, not Passed. This pin asserts behaviour the UNMODIFIED renderer already has (the provenance comment is the first line; the source is embedded verbatim in id=`"graph-source`"), so a red here means the TEST is wrong, not the renderer. Fix the test."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test census: $($failures.Count) of $($mustFail.Count + $mustPass.Count) enumerated behaviours are not proven ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
