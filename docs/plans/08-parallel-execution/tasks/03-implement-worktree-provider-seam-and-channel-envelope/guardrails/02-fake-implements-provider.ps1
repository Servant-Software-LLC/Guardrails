# catches: an "implements IWorktreeProvider" claim satisfied by a comment, a using, or a local copy
#          of the interface - require the actual class declaration with IWorktreeProvider in its base
#          list, scoped to the one file this task owns (structural impl check, stacks/dotnet.md §3)
$impl = "src/Guardrails.Core/Execution/FakeWorktreeProvider.cs"
if (-not (Test-Path $impl)) {
    Write-Output "$impl does not exist - FakeWorktreeProvider was not created"
    exit 1
}
if ((Get-Content $impl -Raw) -notmatch 'class\s+\w+\s*:\s*(\w+,\s*)*IWorktreeProvider') {
    Write-Output "$impl does not declare a class implementing IWorktreeProvider"
    exit 1
}
exit 0
