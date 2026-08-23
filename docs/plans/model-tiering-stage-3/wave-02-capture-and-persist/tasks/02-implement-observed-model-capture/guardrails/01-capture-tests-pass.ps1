# catches: an implementation that buys the new capture at the cost of something the two files already
#          guaranteed. Three classes are selected, deliberately:
#            - ObservedModelCaptureTests   - this task's own deliverable (the four red behaviours go green)
#            - ClaudeStreamParserTests     - the TOLERANT-parse contract. Widening Feed's `type` guard is
#                                            the one edit that can make a garbage line throw instead of
#                                            being skipped, and nothing else in this wave would see it.
#            - ClaudePromptRunnerArgsTests - the STRUCTURAL backing for the prompt's "do NOT force
#                                            --model" prohibition (#221). It already asserts --model is
#                                            ABSENT when the route names none, so an unconditional flag
#                                            added to "know" the model reds this task instead of shipping.
#          Both pre-existing classes are GREEN on the entry tree and this task's change must keep them so;
#          they are regression guards, not goldens this task might legitimately have to re-bake (#193).
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the summary line the zero-match guard reads is LOCALIZED (#455)
$filter = "FullyQualifiedName~ObservedModelCaptureTests|FullyQualifiedName~ClaudeStreamParserTests|FullyQualifiedName~ClaudePromptRunnerArgsTests"

# NO -v q on a TEST command (#179) - it suppresses the Error Message/Expected/Actual block entirely.
$out = dotnet test tests/Guardrails.Core.Tests --nologo --filter $filter 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# FORWARD polarity: the exit-code check comes FIRST, so a test host that never ran is reported as a
# failure rather than misdiagnosed as a bad filter (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the capture is not done, or it regressed a contract the two edited files already held. A ClaudeStreamParserTests failure means the widened Feed guard broke tolerant parsing; a ClaudePromptRunnerArgsTests failure means argv changed - do not force --model."
    exit 1
}

# ZERO-MATCH GUARD (#455): a --filter that selects nothing exits 0. Keyed on the EXECUTED count
# (Passed + Failed), never Total - Total counts [Skip]ped tests, so a fully-skipped selection would
# otherwise certify this task green.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the filter selected nothing, so this guardrail certified nothing. Expected ObservedModelCaptureTests, ClaudeStreamParserTests and ClaudePromptRunnerArgsTests to run."
    exit 1
}
exit 0
