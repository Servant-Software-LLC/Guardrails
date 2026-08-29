# catches: an implementation whose behavior deviates from the tests TASK 03 authored - the during-run
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
# THIS IS THE FORWARD HALF OF A TDD PAIR. It used to be a collapsed task that authored
# DiagramRefreshTests AND turned it green in one action, and its header used to cite "guardrail 04
# (those files' suites still pass)" as half of the anti-tautology cover. BOTH claims are now dealt
# with, and the second one honestly:
#
#   The cited guardrail 04 NEVER EXISTED. Measured 2026-08-29 -
#   `git ls-tree -r --name-only HEAD -- .../tasks/03-replace-meta-refresh/` returns exactly
#   01-build-passes, 02-diagram-refresh-tests-pass, 03-neighbour-diagram-coverage-survives, so this
#   was not a renumbering casualty: the folder has never held a fourth check. The two sibling tasks
#   that DO carry a `04-*-neighbours-still-pass.ps1` (05-raise-attempt-route-resolved and
#   07-render-model-in-row-and-index) are the likely source of the pattern-match. Rather than
#   re-point the citation at a check that does not do what it was credited with, the CHECK IT
#   DESCRIBED HAS BEEN BUILT: guardrail 03 in this folder now runs a per-suite exit-code assertion
#   alongside its survivor census, which is exactly "those files' suites still pass". The reference
#   below is therefore real, not relabelled.
#
# The cover is now three real things, each of which exists and can be pointed at:
#
#   1. UPSTREAM, task 03's per-test red census - tasks/03-author-tests-diagram-refresh/guardrails/
#      02-tests-fail-on-stubs.ps1. It required each of the four behaviours below to be observed
#      Failed in the runner's own TRX BEFORE this task ran. That is what makes "they pass now"
#      evidence of anything at all: a test proven red then green is coupled to the code path.
#   2. tests/Guardrails.Core.Tests/Graph/DiagramRefreshTests.cs is OUTSIDE this task's writeScope.
#      The harness's post-action git diff check rejects an edit to it, so this task cannot make its
#      own judge easier. Before the 03/04 split that file was IN scope and this check was gameable
#      by rewriting the tests it selects.
#   3. Guardrail 03 in THIS folder - the green-polarity census over the three neighbouring test
#      files this task may edit, plus a per-suite exit-code check. That is what stops the other
#      cheap wrong implementation: deleting coverage instead of changing the renderer.
#
# RESIDUAL, stated rather than implied: the census upstream proves each test is COUPLED to the code
# path, not that its ASSERTION is correct. An invoking-then-hollow test would be red before and
# green here and satisfy both halves. That is a human read at /guardrails-review.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=BacklogSlate&FullyQualifiedName~DiagramRefreshTests'
# FILTER DISCRIMINATION (dotnet.md 4.3): 'DiagramRefreshTests' was measured against every one of the
# 285 distinct *Tests class names under tests/ (re-measured 2026-08-29) and matches NONE of them - the
# nearest neighbours are HtmlDiagramRendererTests, OnTheFlyDiagramTests and ContainerDiagramTests,
# none of which contains it as a substring, and no class this plan itself authors does either. It is
# the SAME filter string as the pair's red half (task 03's census), by construction.
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
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. The class is DiagramRefreshTests (trait Category=BacklogSlate) in tests/Guardrails.Core.Tests/Graph/, authored by task 03 - if it is missing entirely, task 03's output did not reach your worktree, which is a dependency-delivery problem and NOT something to fix by writing the tests yourself: that file is outside your write scope. Escalate with needsHuman."
    exit 1
}
exit 0
