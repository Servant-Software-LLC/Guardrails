# catches: code that does not compile. Cheapest-first, so a CS error surfaces here with the compiler's
#          own message rather than as an opaque non-zero from a test run two guardrails later.
#          The specific shape it catches for THIS task: AppendSalvageSection / AppendHeader go
#          private -> internal and gain a defaulted SalvageFraming parameter, and AttemptJournaler
#          .NeedsHuman gains a SalvageRef? parameter. Both are same-assembly consumers, so a
#          CS0122/CS1739/CS7036 from a half-applied signature change lands here first.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (strips restore/banner chatter, keeps the compiler errors); banned only
# on `dotnet test`, where it deletes the failure detail (#462).
$failures = @()

dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "src/Guardrails.Core does not build. If the error is CS7036/CS1739 on a NeedsHuman or AppendSalvageSection call, the signature change was applied at the declaration but not at a call site - or the new parameter was made required when the plan specifies it DEFAULTED."
}

# The Integration test project is what this task's verdict rests on; build it too so a compile break
# there is not mistaken for a behavioural failure by guardrail 02.
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "tests/Guardrails.Integration.Tests does not build against your change. The authored tests are OUTSIDE your writeScope - fix the production signature, not the tests."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
