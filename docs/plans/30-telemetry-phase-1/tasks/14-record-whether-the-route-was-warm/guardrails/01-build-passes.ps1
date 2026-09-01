# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail so
#          a compile error is diagnosed as a compile error rather than as five failing behaviours.
#          The specific near-miss here is that this task's file is the THIRD edit to TaskExecutor.cs in
#          this plan (10-fold-the-digest-into-the-provenance and 12-record-the-turn-count both write it
#          first, and 12a-segment-the-attempt-durations after them), so an edit made against a remembered
#          shape rather than the merged one breaks at COMPILE time - and a rename of the private
#          BuildProvenance method would NOT: the tests reach it by reflection, which binds at runtime, so
#          a rename compiles cleanly and shows up as five null-reference failures in guardrail 02.
#          Building the Core TEST project (not just Guardrails.Core) is deliberate: it builds the
#          production assembly with it AND the authored tests against it, so a member renamed out from
#          under a test is caught here rather than reported as a behaviour failure.
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
    Write-Output "src/Guardrails.Core/Execution/TaskExecutor.cs does not compile. This file already carries edits from 10-fold-the-digest-into-the-provenance, 12-record-the-turn-count and 12a-segment-the-attempt-durations: re-read the CURRENT text around BuildProvenance rather than editing from a remembered shape. Do NOT change BuildProvenance's signature to make something compile - the authored tests bind to it by reflection and a signature change fails them at runtime instead."
    exit 1
}

Write-Output "Core test project builds against the warmth-recording executor."
exit 0
