# catches: dispatch tests that check only the happy path - the plan's load-bearing requirement is that an
#          unimplemented kind FAILS rather than silently falling back to Claude, and a test suite that never
#          asserts the failure would go green over exactly the bug the requirement exists to prevent.
$ErrorActionPreference = 'Stop'
$file = 'tests/Guardrails.Core.Tests/ModelTiering/RegistryKindDispatchTests.cs'
if (-not (Test-Path $file)) { Write-Output "$file was not created"; exit 1 }
$c = Get-Content -Raw -Path $file
if ($c -notmatch '\[(Fact|Theory)\]') { Write-Output "$file declares no [Fact]/[Theory]"; exit 1 }
if ($c -notmatch '(Assert\.Throws|Assert\.ThrowsAny|ShouldThrow)') {
    Write-Output "$file never asserts that an unimplemented kind FAILS registry construction - the no-silent-fallback requirement is unguarded"
    exit 1
}
if ($c -notmatch 'IsType<\s*ClaudePromptRunner') {
    Write-Output "$file never asserts the CONCRETE runner type for kind 'claude' - a type assertion is what catches an inverted pairing"
    exit 1
}
exit 0
