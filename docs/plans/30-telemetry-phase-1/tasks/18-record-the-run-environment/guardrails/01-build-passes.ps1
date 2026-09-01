# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail so
#          a compile error is diagnosed as a compile error rather than as four failing behaviours.
#
# THE SOLUTION, not one project, and that is the point of this file rather than a copy of its siblings.
#          Every other build guardrail in this plan builds tests/Guardrails.Core.Tests, which pulls in
#          Guardrails.Core and nothing else. THIS task is the only one in the plan that writes into
#          Guardrails.Cli as well (RunCommand.cs), and a Cli break is invisible to a Core-only build - so
#          the sibling form would certify a task that leaves the tool unbuildable. The specific break:
#          RunEnvironmentProbe must be PUBLIC for RunCommand, in a different assembly, to call it at all,
#          and a `internal` declaration compiles perfectly inside Core.
#
# Debug, not Release: the test guardrail that follows runs `dotnet test` at Debug, so a Release build
#          here would compile a DIFFERENT set of binaries from the ones actually exercised - twice the
#          work, certifying the wrong artifact. The plan's own terminal gate
#          (guardrails/01-solution-builds.ps1) is the Release solution build, and it belongs there.
#
# -v q is correct on a `dotnet build` and only there (#462): the #179 "never -v q" rule governs test
#          commands, whose Error Message/Expected/Actual block the flag deletes. Build errors are the
#          build's own stdout and survive it.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$solution = 'Guardrails.sln'
if (-not (Test-Path $solution)) {
    Write-Output "PRECONDITION: $solution not found - this task spans Guardrails.Core and Guardrails.Cli, so this guardrail builds the solution and cannot run without it."
    exit 1
}

$log = & dotnet build $solution --nologo -v q 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compilation errors (re-emitted so they land in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match 'error [A-Z]{2}\d+') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "The solution does not build. If the error is an accessibility one in RunCommand.cs: RunEnvironmentProbe and RunEnvironment must be PUBLIC - RunCommand lives in Guardrails.Cli, a different assembly from Guardrails.Core. If the error is that GuardrailsVersion cannot be found from Guardrails.Core: that is the constraint the pinned signature exists for - Guardrails.Cli depends on Guardrails.Core and not the reverse, so the versions are PASSED IN by the CLI rather than read by the probe. Do not change Probe's signature to make something compile."
    exit 1
}

Write-Output "Solution builds - the probe, the journal recorder and the CLI stamp compile together."
exit 0
