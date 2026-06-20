# catches: a GitWorktreeProvider that does not actually implement IWorktreeProvider (a comment, a
#          using, or a stub class that forgot the base list) - require the structural class
#          declaration with IWorktreeProvider in its base list, scoped to the owning file
$impl = "src/Guardrails.Core/Execution/GitWorktreeProvider.cs"
if (-not (Test-Path $impl)) {
    Write-Output "$impl does not exist - GitWorktreeProvider was not created"
    exit 1
}
if ((Get-Content $impl -Raw) -notmatch 'class\s+\w+\s*:\s*(\w+,\s*)*IWorktreeProvider') {
    Write-Output "$impl does not declare a class implementing IWorktreeProvider"
    exit 1
}
exit 0
