# catches: the disjoint-scope work regressing anything across the whole test suite (M1-M7
#          unit tests, the integration tests, the golden round-trip, and the #48 single-writer
#          regression pin). This is the ONE terminal place a no-filter `dotnet test` is allowed
#          (catalogue: whole-suite green is terminal-only).
dotnet test Guardrails.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "Full test suite has failures after the disjoint-scope feature - see the failing tests above."
    exit 1
}
exit 0
