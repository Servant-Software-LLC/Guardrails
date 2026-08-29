# catches: an implementation whose behavior deviates from the tests THIS task pair owns - the diagram
#          still 404s, a guardrail/preflight href still does not resolve, or the fix reached green by
#          serving the whole logs/<runId>/ tree statically (which the pair's abuse pins reject).
#          The --filter names this pair's OWN test class, never the plan-wide trait alone - a
#          trait-only filter asserts the state of every test in the plan, so this task could not go
#          green until a task that DEPENDS on it has run (a deadlock validate/graph --check cannot
#          see, #455). It is the SAME $filter string task 07's red census used, copied verbatim, so
#          the two halves of the pair can never drift apart.
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
#          scope: LOCAL (no sidecar) - it asserts "the diagram IS served", which cannot be true before
#          this task's own action has run, so it fails the #125 union-safe test and must not be tagged
#          scope:"integration" (#250).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=BacklogSlate&FullyQualifiedName~ServeDiagramTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo 2>&1
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
    Write-Output "ServeDiagramTests failing - the log-site server does not serve logs/<runId>/diagram.html, a guardrail/preflight href the diagram authors still does not resolve, an existing route regressed, or the fix serves files it must not (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the class this task pair owns (ServeDiagramTests, trait Category=BacklogSlate, in tests/Guardrails.Integration.Tests/LogSite/)."
    exit 1
}
exit 0
