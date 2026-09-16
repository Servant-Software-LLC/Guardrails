# catches: a brief that composes perfectly against a FAKE IPromptRunner but is broken through the real
#          one (passing-but-blind, #382). The measured case is CriticalityJudge: green against a fake,
#          throwing on the real ClaudePromptRunner's empty StreamLogPath, with a blanket catch turning
#          the crash into a safe default - so it escalated 100% of the time and nothing said so. Every
#          other row in this pair asserts brief TEXT against a fake; this one drives the REAL
#          ClaudePromptRunner over a fake CLI PROCESS and asserts an effect only the real runner emits
#          (the per-attempt stream log on disk, and a parsed verdict rather than the catch-and-safe-default).
# real-seam: Overwatch -> IPromptRunner  bucket=E
#
#          Run SEPARATELY from 01-tests-pass.ps1 on purpose: this failing while the others pass is a
#          different diagnosis (the INVOCATION is wrong, not the brief), and a guardrail that cannot be
#          enumerated as a proof is one a review has to recognise from accidental tells.
#
#          Placement is LOCAL - no `scope` key: the test cannot pass before this task's action has run,
#          so it would fail the union-safe test at an intermediate union (#125/#250). There is deliberately
#          no source-grep fallback: a regex asserting the test file contains `new ClaudePromptRunner(`
#          matches a commented-out line and certifies vocabulary (#468).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)

# Class AND method, so this guardrail's verdict is about the real-seam row alone. The plan-wide trait
# stays a conjunct (#455). The method name is unique in the solution; the class+method form additionally
# survives a future sibling class taking a similar method name.
$filter = 'Category=OverwatchSupply&FullyQualifiedName~OverwatchResourceSupplyBriefTests.ProposeResourceSupply_DrivesTheRealClaudePromptRunner_WritingItsStreamLogAndReturningARealVerdict'

# No -v q on a TEST command: it deletes the failure block the re-emit below exists to surface (#462).
$out  = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# EXIT CODE FIRST on a forward check (#455).
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== FAILURE detail (re-emitted at the END for the ~60-line retry tail, #179) ==="
    $inBlock = $false
    $emitted = 0
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*Failed\s+\S') { $inBlock = $true }
        elseif ($inBlock -and $line -match '^\s*(Passed!|Failed!|Skipped!|Passed\s+\S+\s+\[|Test Run)') { $inBlock = $false }
        if ($inBlock) { Write-Output $line; $emitted++ }
    }
    if ($emitted -eq 0) {
        Write-Output "(no per-test failure block in the runner output - the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)"
    }
    Write-Output ""
    Write-Output "The REAL-SEAM proof failed: Overwatch -> IPromptRunner (bucket E). The brief text rows may all be green - this row is about the INVOCATION the real ClaudePromptRunner receives. Check StreamLogPath is a real per-attempt path under the task log dir and never empty (the #382/#381 bug), the tool profile and turn ceiling are set explicitly, and the result is parsed rather than swallowed by a blanket catch into a safe default. Do NOT 'fix' this by substituting a fake IPromptRunner - that substitution is the blindness this guardrail exists to remove."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does not mean the proof ran. A mistyped method name in the filter
# matches nothing and exits 0, which would silently retire the only real-path check this task has. Key on
# the EXECUTED count (Passed + Failed); "Total:" would also count a [Skip]ped test, and a real-seam proof
# skipped is a real-seam proof that never ran.
$ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the real-seam proof certified NOTHING. The --filter '$filter' matched no test, is malformed, or the test is [Skip]ped. The pinned name is ProposeResourceSupply_DrivesTheRealClaudePromptRunner_WritingItsStreamLogAndReturningARealVerdict in OverwatchResourceSupplyBriefTests, carrying [Trait(""Category"", ""OverwatchSupply"")]."
    exit 1
}

Write-Output "Real-seam proof green: Overwatch -> IPromptRunner (bucket E) driven through the REAL ClaudePromptRunner over a fake CLI process ($ran test executed)."
exit 0
