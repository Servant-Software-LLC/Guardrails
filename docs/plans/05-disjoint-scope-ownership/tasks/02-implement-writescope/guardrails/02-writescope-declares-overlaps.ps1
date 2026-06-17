# catches: an "Overlaps implemented" claim satisfied by a comment, a using, or a helper
#          on another type — require the actual public static Overlaps signature in the
#          one file this task owns (structural match, not a bare token grep; stacks/dotnet.md §3).
$impl = "src/Guardrails.Core/Execution/WriteScope.cs"
if (-not (Test-Path $impl)) {
    Write-Output "$impl does not exist - WriteScope was not created where the design requires (next to WorkspaceContainment)."
    exit 1
}
if ((Get-Content $impl -Raw) -notmatch '(?m)public\s+static\s+bool\s+Overlaps\s*\(') {
    Write-Output "$impl does not declare 'public static bool Overlaps(...)' - the pure overlap function is missing or has the wrong shape."
    exit 1
}
exit 0
