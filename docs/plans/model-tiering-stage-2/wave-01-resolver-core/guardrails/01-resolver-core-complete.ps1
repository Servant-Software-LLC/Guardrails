# catches: a wave that merged green per-task but whose combined result does not hold - the selection
#          and precedence halves were built in separate segments and only meet here. LOCAL, not
#          scope:"integration" (#165/#125): this is a wave TERMINAL POSTCONDITION ("both halves of the
#          resolver are done"), which is false at any partial union by construction, so running it at
#          every union point would red-halt a correct run.
# Re-emits the failure DETAIL at the END so the WHY reaches the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)

# Named alternation over the two classes THIS WAVE owns - not the bare plan-wide trait. Later waves
# add more Category=TierResolution classes, and a trait-only filter here would silently widen to
# include them the moment they land (dotnet.md 4.3, shape 2: parenthesise, bare '|', no backslash).
$filter = 'Category=TierResolution&(FullyQualifiedName~TierResolverCandidateSelectionTests|FullyQualifiedName~TierResolverPrecedenceTests)'
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the resolver core is not complete - selection and/or precedence tests fail on the merged wave HEAD (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does not mean the wave is done.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this wave gate certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}
exit 0
