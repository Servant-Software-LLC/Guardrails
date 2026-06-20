# catches: the worktree lifecycle implemented wrong - GitWorktreeLifecycleTests still failing (plan
#          branch not off HEAD, linear chain not reusing one tree, fork-the-rest on the wrong base -
#          W-2, or the user branch was touched)
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~GitWorktreeLifecycleTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "GitWorktreeLifecycleTests failing - the worktree lifecycle / reuse topology is not implemented to spec"
    exit 1
}
exit 0
