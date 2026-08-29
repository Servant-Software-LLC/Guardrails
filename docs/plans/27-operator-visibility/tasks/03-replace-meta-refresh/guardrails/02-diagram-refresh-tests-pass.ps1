# catches: an implementation whose behavior deviates from the tests THIS task owns - the during-run
#          page still carries a meta refresh, the poll interval is missing / still eager, the final
#          page keeps polling forever, or the file:// fallback notice is absent. The --filter names
#          this task's OWN test class, never the plan-wide trait alone - a trait-only filter asserts
#          the state of every test in the plan, so this task could not go green until a task that
#          DEPENDS on it has run (a deadlock validate/graph --check cannot see, #455).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
#          scope: LOCAL (no sidecar) - it asserts "the meta refresh IS replaced", which cannot be true
#          before this task's own action has run, so it fails the #125 union-safe test and must not be
#          tagged scope:"integration" (#250).
#
# COLLAPSED task, so there is no TDD-red half: this task authors DiagramRefreshTests and turns it
# green in one action. The anti-tautology cover is guardrail 03 (the retired assertions are the ONLY
# permitted edit to the two existing test files) plus guardrail 04 (those files' suites still pass) -
# together they stop the cheapest wrong implementation, which is to delete the coverage instead of
# changing the renderer.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=BacklogSlate&FullyQualifiedName~DiagramRefreshTests'
# FILTER DISCRIMINATION (dotnet.md 4.3): 'DiagramRefreshTests' was measured against every one of the
# 282 distinct *Tests class names under tests/ and matches NONE of them - the nearest neighbours are
# HtmlDiagramRendererTests, OnTheFlyDiagramTests and ContainerDiagramTests, none of which contains it
# as a substring, and no class this plan itself authors does either.
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "DiagramRefreshTests failing - the during-run page still reloads the whole document, GR_LIVE_POLL_MS is absent or under 5000, the final page still carries a poll, or the gr-live-offline fallback notice is missing (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the class this task owns (DiagramRefreshTests, trait Category=BacklogSlate, in tests/Guardrails.Core.Tests/Graph/)."
    exit 1
}
exit 0
