# catches: a wiring that builds the component types but never CONSTRUCTS them at the production
#          composition root — the #120 false-green (all built, all dead from the CLI). Cheap STRUCTURAL
#          complement to the drive-the-real-factory test (guardrail 02): asserts SchedulerFactory.cs
#          actually names FileEscalationSink AND the classifier/judge in its Create path. NOT sufficient
#          alone (a grep can't prove the injection is reached) — the tests-pass guardrail is the real
#          proof; this fails fast/cheap when the construction is simply absent. Scoped to the one file.
$factory = "src/Guardrails.Core/Execution/SchedulerFactory.cs"
if (-not (Test-Path $factory)) {
    Write-Output "$factory does not exist"
    exit 1
}
$c = Get-Content -Raw -Path $factory
if ($c -notmatch 'FileEscalationSink') {
    Write-Output "$factory never constructs FileEscalationSink — the escalation sink is not wired at the composition root (the #120 false-green: built but dead from the CLI)"
    exit 1
}
if ($c -notmatch 'GateClassifier' -and $c -notmatch 'CriticalityJudge') {
    Write-Output "$factory never constructs the GateClassifier / CriticalityJudge — classify-then-act is not wired at the composition root"
    exit 1
}
exit 0
