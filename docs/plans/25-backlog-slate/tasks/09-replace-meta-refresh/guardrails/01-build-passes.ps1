# catches: a change that does not COMPILE. This task edits code in THREE assemblies -
#          Guardrails.Core (HtmlDiagramRenderer), Guardrails.Core.Tests (the new DiagramRefreshTests
#          plus the retired assertion in HtmlDiagramRendererTests) and Guardrails.Integration.Tests
#          (the retired assertion in OnTheFlyDiagramTests) - and NO single project covers all three:
#          tests/Guardrails.Core.Tests does not reference Guardrails.Cli and is not referenced by
#          tests/Guardrails.Integration.Tests, so building either one alone would leave the other
#          task-edited test file UNCOMPILED and certify a broken tree green (the #176
#          transitive-compile-dependency trap). The solution build is the smallest scope that
#          actually covers this task's diff.
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto any dotnet test in this task - there it would delete the failure
# detail the #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build - the HtmlDiagramRenderer template change, DiagramRefreshTests.cs, or one of the two retired assertions is not type-correct (see the compiler errors above)"
    exit 1
}
exit 0
