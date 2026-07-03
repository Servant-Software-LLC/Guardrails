# catches: tautological renderer goldens - tests that PASS against the CURRENT (old-model) renderer assert
#          nothing about the container model / anchors / plan-level-hash. Build is green (guardrail 01), so a
#          non-zero exit here means the goldens RAN and FAILED = TDD red (the current renderer emits done_
#          nodes + fan-out edges and does not fold plan-level checks). INVERSE check - does NOT re-emit (#179).
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~ContainerDiagram" --no-build --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the ContainerDiagram goldens PASS against the current old-model renderer - they are tautological; they must assert the container subgraphs, invisible anchors, absence of done_/fan-out, and the plan-level-check staleness"
    exit 1
}
exit 0
