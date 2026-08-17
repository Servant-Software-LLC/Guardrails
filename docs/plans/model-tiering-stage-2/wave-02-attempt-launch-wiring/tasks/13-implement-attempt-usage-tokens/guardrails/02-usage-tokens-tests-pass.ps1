# catches: a usage implementation whose behaviour deviates from the tests THIS task pair owns - in
#          particular one that reads usage.input_tokens alone (understating volume ~1250x on real
#          output), or that returns AttemptUsage { 0, 0 } for an absent usage object instead of null,
#          which would make a costless local provider report "0 tok" rather than its real volume.
#          The --filter names this pair's OWN test class, never the plan-wide trait alone (#455):
#          a trait-only filter here would assert the state of every class in the plan, so this task
#          could not go green until tasks it does NOT depend on had run - a deadlock validate and
#          graph --check cannot see.
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
# The SAME $filter string as the pair's inverse check, copied verbatim so the two halves cannot drift.
$filter = 'Category=TierResolution&FullyQualifiedName~AttemptUsageTokensTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone (#462).
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
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
    Write-Output ""
    Write-Output "AttemptUsageTokensTests failing - the usage axis does not behave to DoR 12.4. Two failures worth naming because their cause is not obvious from the assertion alone: if the INPUT TOTAL is off, InputTokens must be input_tokens + cache_creation_input_tokens + cache_read_input_tokens, not input_tokens alone. If the ABSENT case is failing, a result event with no usage object must yield NULL, not AttemptUsage { 0, 0 } - zero is a claim that nothing was consumed."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the tests this task pair actually owns."
    exit 1
}
exit 0
