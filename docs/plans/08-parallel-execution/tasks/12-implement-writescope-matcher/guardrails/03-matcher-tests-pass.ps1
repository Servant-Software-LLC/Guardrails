# catches: a matcher that does not satisfy the full 27-row truth table or the two fuzz properties -
#          e.g. the permissive prefix/suffix-discard bug (rows 1/4/7/18/19/21/23/25) or an
#          under-detecting Overlaps (the completeness property)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~WriteScopeMatcherTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "WriteScopeMatcherTests failing - the matcher does not satisfy the 27-row table or a fuzz property (likely the permissive segment bug or an incomplete Overlaps)"
    exit 1
}
exit 0
