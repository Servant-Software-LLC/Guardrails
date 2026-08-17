# catches: a test-author task that wrote TAUTOLOGIES - tests that pass against the unpopulated stub,
#          proving nothing about the usage axis. The stub is real and specific: ClaudeResult.Usage
#          and PromptResult.Usage are DECLARED but never assigned, so every behavioural test must be
#          red. If they all pass, either the parsing was implemented here (task 13's job) or the
#          tests assert nothing.
#
#          Note the ONE case that legitimately passes against the stub: "absent usage => null" is
#          already true of an unpopulated member. That is exactly why this check requires a NON-zero
#          exit rather than any-failure - a suite consisting only of the null case would go green
#          here and is precisely the shallow suite worth rejecting.
# INVERSE guardrail: a NON-zero `dotnet test` exit is SUCCESS here. The sibling 01-build-passes has
# already established that the solution compiles, so a non-zero exit now unambiguously means the
# tests RAN and FAILED (#155). No #179 re-emit: the failures are the intended outcome.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=TierResolution&FullyQualifiedName~AttemptUsageTokensTests'
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# INVERSE polarity => ZERO-MATCH GUARD FIRST (#455): a crashed or never-started test host also exits
# non-zero. Keyed on the EXECUTED count (Passed+Failed); "Total:" counts [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests (wrong class name, or the class-level [Trait(`"Category`", `"TierResolution`")] is missing), is malformed, or every matched test is [Skip]ped."
    exit 1
}

if ($testExit -eq 0) {
    Write-Output ""
    Write-Output "every AttemptUsageTokensTests test PASSED against ClaudeResult.Usage / PromptResult.Usage members that are DECLARED but never populated - so the suite asserts nothing. MOST LIKELY CAUSE: the only behaviour encoded is the absent-usage-yields-null case, which is trivially true of an unassigned member. Encode the cases that need real parsing: the FULL input total (input_tokens + cache_creation_input_tokens + cache_read_input_tokens - a naive input_tokens read understates volume by ~1250x on real output), partial fields, last-result-wins, and malformed-usage-does-not-throw."
    exit 1
}
exit 0
