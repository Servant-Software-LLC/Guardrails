# catches: the ONE regression that would invalidate section 13's whole "stages 2 and 3 need no tests/** path"
#          argument - a change that moved the RETRY framing's emitted BYTES. The two shipped suites
#          hard-pin AppendSalvageSection's output:
#            RetryPolicySalvageAdviceTests  - the patch bullet must be FIRST, `git show "<ref>:<path>"`
#                                             verbatim, "EVERYTHING" banned, no git diff/git apply
#                                             invocation, the `git -C` failure shape named
#            RetrySalvageTests              - the literal heading "## Prior attempt work is salvageable",
#                                             the ref name, the protected-artifact suppression
#          A defaulted `SalvageFraming framing = SalvageFraming.Retry` keeps them passing UNTOUCHED.
#          An implementation that reworded the Retry branch, or made `framing` required, or filtered
#          the RETRY path's staged set instead of passing restrictToScope: null, breaks them here.
#
# WHY IT IS ITS OWN GUARDRAIL and not left to the terminal gate: at the terminal gate this failure
#          would be attributed to the terminal task, on a fully-merged HEAD, after every other task has
#          spent its budget. Here it is attributed to the change that caused it, on the attempt that
#          made it, and the retry feedback names the suite.
#
# WHAT THIS DOES *NOT* NEED TO CHECK: that the two suite FILES were not edited. They are outside this
#          task's writeScope, and the harness's own deterministic write-scope check (SSOT section 3.4) runs
#          after the action and before this guardrail - an edit to either fails the attempt before this
#          file executes. That check IS the "tests-untouched protected-artifact guardrail" plan 31 section 9
#          asks every implementation stage to carry; it is a harness mechanism, not a script. This
#          guardrail covers the half the write-scope check cannot see: the bytes moving with the files
#          untouched.
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED - pin the culture BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$suites = @(
    @{ Project = 'tests/Guardrails.Core.Tests'
       Filter  = 'FullyQualifiedName~RetryPolicySalvageAdviceTests'
       Label   = 'RetryPolicySalvageAdviceTests'
       Hint    = "This suite pins AppendSalvageSection's ORDER and wording. A failure here means the Retry framing's bytes moved: restore them and put your new wording behind SalvageFraming.Escalation instead." },
    @{ Project = 'tests/Guardrails.Integration.Tests'
       Filter  = 'FullyQualifiedName~RetrySalvageTests'
       Label   = 'RetrySalvageTests'
       Hint    = "This suite calls PreserveAttemptToRef DIRECTLY and exercises the RETRY path. A failure here usually means restrictToScope was made required, or the retry call site now passes a scope instead of null - plan 31 section 3.4 divergence 3 requires the retry path to stay byte-identical." }
)

# ACCUMULATE: one message per broken suite, dumped once.
$failures = @()

foreach ($suite in $suites) {
    # NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the
    # re-emit below exists to surface, defeating #179 by the flag alone (#462).
    $out = & dotnet test $suite.Project --nologo --filter $suite.Filter 2>&1
    $testExit = $LASTEXITCODE
    $out | ForEach-Object { Write-Output $_ }

    # EXIT CODE FIRST on a forward check (#455): a test host that never ran exits NON-zero with no
    # summary at all, so checking the exit code first reports its real error instead of blaming the
    # filter and sending a retry agent to rename a correctly-named class.
    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 20
        Write-Output ""
        Write-Output "=== $($suite.Label) failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "$($suite.Label) is RED. $($suite.Hint)"
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does not mean the suite passed - a filter that matches
    # nothing also exits 0, and this suite is the whole evidence for the zero-edits claim, so a
    # vacuous green here is the worst outcome available. Key on the EXECUTED count (Passed + Failed);
    # 'Total:' would also count [Skip]ped tests.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += "$($suite.Label): exit 0 but ZERO tests executed - the filter matched nothing or the test host did not run, so the zero-edits claim is uncertified. Check the class still exists at its shipped path."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== shipped salvage suites: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Fix the implementation so the Retry framing emits today's bytes. Do NOT edit either suite - both are outside this task's writeScope, and their passing untouched is what makes stages 2 and 3 legitimately test-free (plan 31 section 3.3, section 8)."
    exit 1
}
Write-Output "Both shipped salvage suites pass untouched - the Retry framing's bytes did not move."
exit 0
