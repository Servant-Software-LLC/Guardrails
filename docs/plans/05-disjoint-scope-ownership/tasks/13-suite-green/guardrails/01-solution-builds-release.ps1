# catches: a project that builds alone but breaks the whole solution in Release (an unregistered
#          project, a broken cross-project ref, a warning that is an error under Release). The
#          per-task builds upstream were single-project; this is the terminal whole-solution gate
#          (stacks/dotnet.md §4).
dotnet build Guardrails.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "Whole-solution Release build failed after the disjoint-scope feature - see the build output above."
    exit 1
}
exit 0
