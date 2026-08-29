# catches: an implementation whose behavior deviates from the tests THIS task pair owns - a next-probe
#          that takes MAX instead of MIN (so a three-hour reset hint buys a three-hour sleep), a
#          negative wait from an already-passed reset instant, a wait that overshoots its own ceiling,
#          an unbounded loop that never refuses to wait again, or a reason string that says "paused"
#          without saying WHEN. The --filter names this pair's OWN test class, never the plan-wide
#          trait alone - a trait-only filter asserts the state of every test in the plan, so this task
#          could not go green until a task that DEPENDS on it has run (a deadlock validate/graph
#          --check cannot see, #455). This plan has five other test classes under the same trait.
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=BacklogSlate&FullyQualifiedName~BarrierWaitTests'   # copied VERBATIM from the pair's inverse half (task 05)
# --no-build is safe here: guardrail 01 already built Guardrails.sln, which includes this test project.
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
    Write-Output "BarrierWaitTests failing - BarrierWait is not implemented to the spec those tests pin (nextProbe = min(resetInstant, now + probeInterval), a 30-minute default, never a past probe, clamped to and bounded by the ceiling, and a reason that names the next-probe time). The test file is OUT of this task's write scope: implement to it."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0 (MEASURED on this project: 'FullyQualifiedName~ActionTierProvenanceTests
# &Category!=TierResolution' printed "No test matches ..." and exited 0). Key on the EXECUTED count
# (Passed+Failed; "Total:" would also count [Skip]ped tests), never on "No test matches ..."
# (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the tests this task pair actually owns (class BarrierWaitTests in tests/Guardrails.Core.Tests/Providers/, trait Category=BacklogSlate)."
    exit 1
}
exit 0
