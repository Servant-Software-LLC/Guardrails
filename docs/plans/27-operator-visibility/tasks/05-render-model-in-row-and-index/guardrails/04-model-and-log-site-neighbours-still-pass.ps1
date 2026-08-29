# catches: buying this task's own green by breaking the two existing suites its edits sit on top of.
#          AttemptModelRenderingTests pins the SHARED disclosure wording (AttemptModelSummary), the
#          ConsoleRunObserver "[model] <task> attempt N: ..." line, and the fact that LiveRunObserver
#          DECLARES AttemptModelResolved rather than inheriting the interface's empty default - all
#          three are directly in this task's blast radius, and the third is the exact swallow-the-event
#          defect that made #524 possible. LogSiteExportTests pins the run-level index and task page
#          this task adds a column to: the link-vs-plain-text rule, the data-status attribute, the
#          inlined attempt output and export idempotence. Guardrail 02 runs only this pair's OWN class
#          and can see none of it, so without this a green task can ship a regressed log site and the
#          plan would not learn until the terminal gate - with the failure attributed to whatever ran
#          last (#175).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
#
# REGRESSION clause, green on arrival BY DESIGN - the declared #478 exception ("this existing thing
# still passes" is green before the task by definition). Neither file is in this task's writeScope, so
# unlike task 03's neighbour census there is no deletion risk to cover: the harness rejects an edit to
# them outright. A plain suite-pass is therefore sufficient here.
#
# scope: LOCAL (no sidecar). It asserts EXISTING suites still pass, which reads union-safe - but it is
# scoped to this task's own attempt deliberately, because attributing a log-site or model-disclosure
# regression to the task that edited those renderers is the entire value (#250).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
# dotnet.md 4.3 shape 2 (two classes, parenthesised alternation, BARE '|' - '\|' is VSTest's escape
# character and yields "Incorrect format for TestCaseFilter", ZERO tests, exit 0, a silent green).
# The plan-wide trait is ABSENT on purpose: these classes predate this plan
# (AttemptModelRenderingTests carries Category=ModelTieringStage3, LogSiteExportTests carries no
# trait), so conjoining Category=BacklogSlate would match ZERO tests and the guard below would fire.
# Both class names were measured against every one of the 282 distinct *Tests class names under
# tests/ and each matches only itself - the nearest neighbours (AttemptModelDisclosureTests,
# AttemptModelForwardingTests, LogSiteHaltBannerTests, OnTheFlyLogSiteTests) contain neither.
$filter = '(FullyQualifiedName~AttemptModelRenderingTests|FullyQualifiedName~LogSiteExportTests)'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "AttemptModelRenderingTests / LogSiteExportTests REGRESSED - the Model column or the attempt-route.log link changed the shared model wording, the console model line, or the exported log site's existing shape. These suites passed before this task ran and are OUTSIDE its write scope: fix LiveRunObserver.cs / LogSiteRenderer.cs / ConsoleRunObserver.cs, not the tests."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this regression guard certified nothing. The --filter '$filter' matched no tests or is malformed. Both classes live in tests/Guardrails.Integration.Tests (ModelTiering/AttemptModelRenderingTests.cs and LogSiteExportTests.cs); do NOT 'fix' this by conjoining a Category term."
    exit 1
}
exit 0
