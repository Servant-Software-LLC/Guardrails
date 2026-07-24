# catches: a wiring test that PASSES against the currently-UNWIRED SchedulerFactory — which means it
#          either injects the seam itself (the #120 anti-pattern: `new Scheduler(..., new
#          FileEscalationSink())`) or asserts nothing about the real production path. With the build
#          green (guardrail 01), a non-zero exit here means the test drives the REAL factory and FAILS
#          because the escalation machinery is not wired yet = the correct #120 red. Task 15 makes it
#          green by WIRING (not by weakening the test). (INVERSE check: non-zero is success, no #179
#          re-emit.)
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~SchedulerEscalationWiringTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the SchedulerEscalationWiringTests PASS against the UNWIRED factory — the test is either injecting the seam itself (forbidden #120) or asserting nothing; it must drive the REAL SchedulerFactory.Create and fail until task 15 wires the escalation machinery"
    exit 1
}
exit 0
