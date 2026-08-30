# catches: making the build green by DELETING the problem instead of fixing it. With Role required and
#          un-defaulted (guardrail 02), the eight test fixtures below stop compiling — and the cheapest
#          way to a green build is to delete them, empty their construction helper, or [Skip] them out.
#          01-build-passes cannot tell that apart from the real fix: a tree with the fixtures deleted
#          compiles perfectly. This asserts the POSITIVE — each file still exists AND still sets Role
#          itself — so the only way through is to have actually edited all eight.
#
#          The list is exact rather than a glob because it IS the compiler's own answer: these are the
#          eight test files that construct a PromptInvocation, six of them through a target-typed
#          factory (`private static PromptInvocation Invocation(...) => new()`) that a grep for
#          "new PromptInvocation" does not match. A wildcard over tests/ would pass over a tree where
#          all eight were deleted and some unrelated test happened to mention Role.
$ErrorActionPreference = 'Continue'

$fixtures = @(
    'tests/Guardrails.Core.Tests/ClaudePromptRunnerArgsTests.cs',
    'tests/Guardrails.Core.Tests/ClaudePromptRunnerStreamLogTests.cs',
    'tests/Guardrails.Core.Tests/PromptDenialFailFastTests.cs',
    'tests/Guardrails.Core.Tests/ToolGrantInjectionTests.cs',
    'tests/Guardrails.Core.Tests/ModelTiering/AttemptUsageTokensTests.cs',
    'tests/Guardrails.Core.Tests/ModelTiering/ObservedModelCaptureTests.cs',
    'tests/Guardrails.Integration.Tests/FakeClaudeRunTests.cs',
    'tests/Guardrails.Integration.Tests/RetrySalvageTests.cs'
)

$missing = @()
$unset = @()

foreach ($relative in $fixtures) {
    $path = Join-Path $env:GUARDRAILS_WORKSPACE $relative
    if (-not (Test-Path $path)) {
        $missing += $relative
        continue
    }

    if ((Get-Content $path -Raw) -notmatch 'Role\s*=\s*PromptRole\.') {
        $unset += $relative
    }
}

if ($missing.Count -gt 0 -or $unset.Count -gt 0) {
    Write-Output "=== The eight PromptInvocation test fixtures were not all fixed ==="
    foreach ($m in $missing) { Write-Output "  DELETED/MOVED : $m" }
    foreach ($u in $unset)   { Write-Output "  NO Role SET   : $u" }
    Write-Output ""
    Write-Output "Every one of these constructs a PromptInvocation and must set Role = PromptRole.Action."
    Write-Output "Deleting, emptying or skipping a fixture is not a fix - it makes the build green by removing the evidence."
    exit 1
}

Write-Output "All 8 PromptInvocation test fixtures are present and set Role."
exit 0
