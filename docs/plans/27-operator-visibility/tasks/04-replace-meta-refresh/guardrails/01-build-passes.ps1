# catches: a change that does not COMPILE. This task edits code in THREE assemblies -
#          Guardrails.Core (HtmlDiagramRenderer), Guardrails.Core.Tests (the retired assertion in
#          HtmlDiagramRendererTests) and Guardrails.Integration.Tests (the retired assertions in
#          OnTheFlyDiagramTests and RunCommandFinalSiteSettleTests) - and NO single project covers
#          all three:
#          tests/Guardrails.Core.Tests does not reference Guardrails.Cli and is not referenced by
#          tests/Guardrails.Integration.Tests, so building either one alone would leave the other
#          task-edited test file UNCOMPILED and certify a broken tree green (the #176
#          transitive-compile-dependency trap). The solution build is the smallest scope that
#          actually covers this task's diff.
#
#          It ALSO compiles tests/Guardrails.Core.Tests/Graph/DiagramRefreshTests.cs, which task 03
#          authored and this task may NOT edit. That is deliberate and useful: if the renderer change
#          breaks that file's compile, this check catches it here, and the only in-scope remedy is to
#          fix HtmlDiagramRenderer.cs. If the test file is genuinely un-compilable against a correct
#          renderer, that is a needsHuman, not a licence to edit it (see the action prompt).
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto any dotnet test in this task - there it would delete the failure
# detail the #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build - the HtmlDiagramRenderer template change, or one of the three retired assertions, is not type-correct (see the compiler errors above). If the errors are in Graph/DiagramRefreshTests.cs, that file is task 03's and is OUT of your write scope: fix the renderer, or escalate with needsHuman - do not edit it."
    exit 1
}
exit 0
