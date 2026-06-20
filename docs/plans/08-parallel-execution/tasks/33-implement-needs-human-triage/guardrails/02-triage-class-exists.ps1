# catches: NeedsHumanTriageTests made to pass by some OTHER means - the §9 triage type the plan names
#          (Execution/NeedsHumanTriage) was never actually declared in the file this task owns. This is
#          STRUCTURAL: it asserts a real C# class DECLARATION of `NeedsHumanTriage` via the dotnet
#          declaration regex (optional modifiers, then class/record/struct, then the exact name as a
#          whole word), not a bare keyword/comment/usage. Scoped to the one file this task owns
#          (grep-scope rule - no project-tree greps).
$file = "src/Guardrails.Core/Execution/NeedsHumanTriage.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the §9 NeedsHumanTriage step was not implemented in its owned file"
    exit 1
}
$text = Get-Content $file -Raw
# A real declaration: (modifiers)* (class|record|struct|record class) NeedsHumanTriage <word-boundary>.
# Excludes a comment mention, a variable/usage, or the type appearing only in another file.
if ($text -notmatch '(?m)^\s*(?:public\s+|internal\s+|sealed\s+|abstract\s+|partial\s+|static\s+)*(?:class|record\s+class|record|struct)\s+NeedsHumanTriage\b') {
    Write-Output "NeedsHumanTriage.cs does not DECLARE a type 'NeedsHumanTriage' (expected e.g. 'public sealed class NeedsHumanTriage'). A passing test suite with no real triage type declared is the gameable path this structural check closes."
    exit 1
}
exit 0
