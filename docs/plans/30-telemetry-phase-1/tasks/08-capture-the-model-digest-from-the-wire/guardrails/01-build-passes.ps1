# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail so
#          a compile error is diagnosed as a compile error rather than as four failing behaviours. The
#          specific near-miss here is a threading edit half-done - a new `ref string? digest` parameter
#          added to ApplyChunk but not to its ApplyWholeCompletion twin, or added to the folds and not to
#          the StreamedTurn constructor - which breaks at COMPILE time, not at assertion time, and would
#          otherwise be reported as a test failure.
#
#          The Core TEST project is built rather than src/Guardrails.Core alone, deliberately: it builds
#          Guardrails.Core with it AND binds the authored tests against the edited runner, so a signature
#          drift that the production assembly alone would swallow still surfaces here.
#
# -v q is correct on a `dotnet build` and only there (#462).
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail builds the Core test project (which builds Guardrails.Core with it) and cannot run without it."
    exit 1
}

$log = & dotnet build $project --nologo -v q 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compilation errors (re-emitted so they land in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match 'error [A-Z]{2}\d+') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs does not compile. If you threaded a new digest parameter through the folds, thread it through BOTH ApplyChunk and ApplyWholeCompletion and through every call site and the accumulated turn - a half-threaded ref parameter is the usual cause here."
    exit 1
}

Write-Output "Core test project builds against the edited openai-compat runner."
exit 0
