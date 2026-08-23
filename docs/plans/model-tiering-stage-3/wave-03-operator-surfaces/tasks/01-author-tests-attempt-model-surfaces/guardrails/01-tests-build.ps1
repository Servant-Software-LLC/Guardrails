# catches: a test file that does not COMPILE being passed off as the TDD "red" (#155) - a non-compiling
#          test project exits `dotnet test` non-zero for a reason that has nothing to do with the
#          behaviour under test, so the census in 02 would read garbage as a clean red. The sharper risk
#          on THIS task is the two SHARED files it edits: Stage2PlanHarness.cs is consumed by the whole
#          Stage-2 conformance suite, and LiveRunObserver.cs is compiled by the CLI. A widened RunAsync
#          overload that breaks an existing call site, or a stray edit to the live observer, breaks a
#          project that tasks 02-04 all depend on - and none of THEM may fix it, because this file is
#          outside every one of their writeScopes.
#
#          Both projects, because this task's stubs straddle them: IRunObserver.cs is Core, and
#          LiveRunObserver.cs is Cli. Building the integration test project transitively builds both,
#          but a Core-only regression would surface here with a far clearer message than as a downstream
#          test failure.
# MEASURED BASELINE 2026-08-23 against the merged wave-2 HEAD: `dotnet build tests/Guardrails.Integration.Tests`
# exits 0. This clause is therefore a REGRESSION guard on this task's own edit - it can only go red
# because of what this task wrote.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# -v q IS correct on a BUILD (dotnet.md section 4); it is the TEST command that must never carry it (#179).
$out = dotnet build tests/Guardrails.Integration.Tests -v q --nologo 2>&1
$buildExit = $LASTEXITCODE                                 # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    $detail = $out |
        Select-String -Pattern 'error [A-Z]{2}\d+|: error|Build FAILED' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Build errors (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no error lines matched - inspect the full log above)" }
    Write-Output "the three new test classes must COMPILE and FAIL. They do not compile - most likely a stub is missing (IRunObserver.AttemptModelResolved, LiveRunObserver.AttemptModelSummary, or the optional observer parameter on Stage2PlanHarness.RunAsync), a decorator event was invoked on the CONCRETE type instead of through IRunObserver (a default-method member is not a class member until the class declares it), or an existing Stage2PlanHarness.RunAsync call site was disturbed."
    exit 1
}
exit 0
