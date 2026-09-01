# catches: an anchor test that does not actually hold against the tree it anchors. This is the ONE
#          artifact in this plan whose value is entirely repo-lifetime rather than run-lifetime: section
#          9's Risk 6 hazard is "a seventh site added later by someone who has not read this document",
#          and the three earlier drafts of this check were all plan-folder guardrails that evaporate when
#          the run ends.
#
#          It is GREEN ON ARRIVAL by construction, and that is correct rather than a weakness: stages 3,
#          4 and 5 have already produced the state it anchors, so there is no red half to demand. The
#          anti-tautology burden therefore sits on guardrail 03, which checks that the file enumerates a
#          SET and asserts no bare count - a distinction a passing anchor test cannot make about itself.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179).
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED (a German-culture box prints 'gesamt:' and no
# 'Total:'), which would invert the guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Discriminating (#455 companion (a)): 'ExecutedDefinitionHashAnchorTests' is contained by nothing else.
# It also does NOT contain 'ExecutedDefinitionHashTests' - the 'Anchor' segment sits between - so stage
# 1's class is not swept in, which matters because that class must stay green here and is not this
# stage's business.
$suites = @(
    @{ Project = 'tests/Guardrails.Core.Tests'
       Filter  = 'FullyQualifiedName~ExecutedDefinitionHashAnchorTests'
       Hint    = 'The anchor test reads src/ as TEXT and asserts the enumerated SET of surviving TaskDefinitionHash.Compute call sites. A failure here means either the anchor is wrong (it names a file or member that is not there) or the tree is wrong (stages 3-5 left a call site behind, or introduced one). Read the failure message: a set-based anchor names the offending site, which is the whole reason section 9 forbids a bare count. This file is your deliverable, so unlike every other stage you MAY fix the anchor - but check the tree first, because a wrong anchor written to match a wrong tree is exactly the tautology the count form invited.' }
)

# ACCUMULATE (#478): one distinguishable message per suite, dumped once at the end.
$failures = @()

foreach ($suite in $suites) {
    # NO -v q on a TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
    # leaving only the [FAIL] line for the re-emit below to find - defeating #179 by the flag alone
    # (#462).
    $out = & dotnet test $suite.Project --filter $suite.Filter --nologo 2>&1
    $testExit = $LASTEXITCODE                              # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }

    # EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero
    # with no summary at all, so checking the exit code first reports its real error instead of blaming
    # the filter - a confident misdiagnosis pointing at the one artifact a retry agent may NOT edit here.
    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 40                        # bound the block so it fits the ~60-line tail
        Write-Output ""
        Write-Output "=== $($suite.Project) failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "$($suite.Project) is red under filter '$($suite.Filter)'. $($suite.Hint)"
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
    # or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed); 'Total:' would also count
    # [Skip]ped tests, so a fully-skipped selection would clear a Total-keyed guard.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += "$($suite.Project) exited 0 but executed ZERO tests under filter '$($suite.Filter)' - this guardrail certified nothing. The filter matched no tests, is malformed, or every match is [Skip]ped."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== $($failures.Count) suite(s) not green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Anchor test green: the enumerated call-site set and the four shape anchors all hold against src/."
exit 0
