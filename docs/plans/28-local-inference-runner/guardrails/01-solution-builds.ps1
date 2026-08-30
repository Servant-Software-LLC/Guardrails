# catches: a merged HEAD that does not compile. LOCAL by design - no scope key. A full build is a TERMINAL
#          POSTCONDITION, and tagging it scope:"integration" would re-run it at EVERY union, including
#          partial merges where a downstream TDD task has not run yet: this plan's test files reference
#          types later tasks implement, so a whole build at an intermediate union FAILS and the harness
#          rolls back a correct wave (#125/#165).
$ErrorActionPreference = 'Continue'

$log = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compiler errors on the merged HEAD (re-emitted at the END) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match 'error [A-Z]+[0-9]+') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "The merged plan branch does not build. A duplicate-definition CS0101 here would point at a file two tasks both wrote - check OpenAiCompatPromptRunner.cs, PlanValidator.cs, DiagnosticCodes.cs, PromptRunnerConfig.cs and GuardrailRunner.cs first, in that order."
    exit 1
}
Write-Output "Merged HEAD builds."
exit 0
