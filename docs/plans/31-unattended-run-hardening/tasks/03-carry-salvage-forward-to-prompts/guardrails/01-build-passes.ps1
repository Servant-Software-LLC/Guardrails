# catches: code that does not compile. Cheapest-first, so a CS error surfaces with the compiler's own
#          message rather than as an opaque non-zero two guardrails later.
#          The specific shape for THIS task: PriorAttemptRef gains two init-only members, and
#          PromptComposer (Guardrails.Core) calls RetryPolicy.AppendSalvageSection, which stage 2 made
#          `internal static` in the SAME assembly. A CS0122 here means stage 2's accessibility change
#          did not land or was reverted; a CS9035/CS7036 on a PriorAttemptRef initializer means a new
#          member was made `required` when the plan specifies both as OPTIONAL, which would break
#          tests/Guardrails.Core.Tests/PromptComposerTests.cs - a file outside this task's writeScope.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD; banned only on `dotnet test` (#462).
$failures = @()

dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "src/Guardrails.Core does not build. CS0122 on AppendSalvageSection means stage 2's private -> internal change is missing (do NOT fix it here - RetryPolicy.cs is outside your writeScope; escalate with needsHuman). CS9035/CS7036 on a PriorAttemptRef initializer means a new member was made required rather than optional."
}

dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "tests/Guardrails.Core.Tests does not build against your change. The most likely cause is a REQUIRED new PriorAttemptRef member breaking PromptComposerTests.cs's object initializer - that file is outside your writeScope, so make the members optional rather than editing it."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
