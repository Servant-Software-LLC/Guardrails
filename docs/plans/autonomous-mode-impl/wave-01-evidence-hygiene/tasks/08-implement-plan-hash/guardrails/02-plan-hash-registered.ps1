# catches: the plan-hash command dropped from the production dispatch — CommandFactory.BuildRootCommand
#          no longer constructs it, so `guardrails plan-hash` is unreachable from the CLI even though the
#          command class exists. Cheap structural wiring insurance alongside the drive-the-real-factory
#          test (guardrail 01). Scoped to the one dispatch file.
$factory = "src/Guardrails.Cli/CommandFactory.cs"
if (-not (Test-Path $factory)) {
    Write-Output "$factory does not exist"
    exit 1
}
if ((Get-Content -Raw -Path $factory) -notmatch 'PlanHashCommand\s*\.\s*Create') {
    Write-Output "$factory does not register PlanHashCommand.Create — the plan-hash command is not wired into production dispatch"
    exit 1
}
exit 0
