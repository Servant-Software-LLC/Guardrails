# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. A non-compiling
#          "test" exits dotnet test non-zero IDENTICALLY to a failing one, so without this the red
#          signal in 02-tests-fail-on-stubs is gameable by garbage (#155).
#
# WHY tests/Guardrails.Core.Tests AND NOT THE SOLUTION: this task's diff is exactly ONE file, in
# exactly ONE project. tests/Guardrails.Core.Tests references Guardrails.Core, so building it
# compiles DiagramRefreshTests.cs AND the production assembly it drives (HtmlDiagramRenderer lives in
# Guardrails.Core). Building src/Guardrails.Core alone would leave THIS TASK'S ONLY DELIVERABLE
# uncompiled and certify it green (the #176 transitive-compile-dependency trap, inverted). The
# solution build is the right scope for task 04, which edits three assemblies; here it would just be
# slower with no extra coverage.
#
# THERE IS NO STUB, and none is needed: HtmlDiagramRenderer.Render is already public with the exact
# 5-arg signature the tests call, so the file compiles against today's code and is red on OUTPUT.
# That is why this check and the census can be cleanly separated - a compile failure here means the
# TEST is wrong, never that the production API is missing.
#
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto any dotnet test in this plan - there it would delete the failure
# detail the #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - DiagramRefreshTests.cs is not type-correct (see the compiler errors above). Check the Render overload you called: the 5-arg form is Render(string mermaidSource, string sourceHash, IReadOnlyDictionary<string,string> taskFolderTargets, IReadOnlyDictionary<string,string> statusByNodeId, bool duringRun)."
    exit 1
}
exit 0
