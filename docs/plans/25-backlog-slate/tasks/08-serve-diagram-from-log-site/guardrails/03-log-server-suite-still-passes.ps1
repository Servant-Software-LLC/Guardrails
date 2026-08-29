# catches: a routing change that buys the diagram route by breaking a route that already worked.
#          LogServerTests is the EXISTING end-to-end suite for the exact class this task edits - the
#          pointer-note root, the task page, /files, /file (including its refusal to escape the
#          attempt directory), /source, /sourcefile and POST /answer. This task's own pair
#          (guardrail 02) proves the NEW behaviour and cannot see any of that, so without this a
#          green task can ship a server whose existing surface has regressed, and the plan would not
#          learn until the terminal gate - with the failure attributed to whatever ran last (#175).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
#
# REGRESSION clause, green on arrival BY DESIGN - the declared #478 exception ("this existing thing
# still passes" is green before the task by definition). It is not a pre-satisfaction defect; the
# measurement that matters is that these tests pass on the untouched tree, which is the whole point.
#
# scope: LOCAL (no sidecar). It asserts an EXISTING suite still passes, which reads union-safe - but
# it is scoped to this task's own attempt deliberately, because attributing a LogServer regression to
# the task that edited LogServer.cs is the entire value (#250: do not tag it scope:"integration").
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
# The plan-wide trait is ABSENT here on purpose: LogServerTests predates this plan and carries no
# Category trait, so conjoining Category=BacklogSlate would match ZERO tests and the guard below
# would fire. The class term alone is dotnet.md 4.3 shape 3 - and 'LogServerTests' was measured
# against every *Tests class name under tests/ and matches only itself.
$filter = 'FullyQualifiedName~LogServerTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "LogServerTests REGRESSED - the new diagram/source routing broke an existing LogServer route. These tests passed before this task ran; the fix is in LogServer.cs, not in the tests (which are outside this task's write scope)."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this regression guard certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. LogServerTests lives in tests/Guardrails.Integration.Tests/LogServerTests.cs and carries no Category trait; do NOT 'fix' this by conjoining one."
    exit 1
}
exit 0
