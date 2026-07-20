# catches: a wire that BUILDS the escalation component types but never CONSTRUCTS them at the
#          production composition root (SchedulerFactory.Create), or a Scheduler that never dispatches
#          through the classifier/sink — the #120 recurring false-green (all types built + unit-green +
#          terminal-suite-green, yet DEAD from the real CLI because nothing instantiates/reaches them in
#          production). This is the #120 WEAK STRUCTURAL form: a cheap, fast-failing complement to the
#          drive-the-real-factory integration test, which lives on the SINK task 17 (where ALL components
#          are wired + the exit code surfaces). It is NOT sufficient alone — a grep cannot prove the
#          construction is reached on the production path — but it fails fast/cheap when the wire is simply
#          ABSENT. Each check is scoped to the ONE file that owns it.
#          NOTE the deliberate 3 (not 5): GateClassifier is a STATIC class (GateClassifier.Classify(...)) —
#          not constructed, so the Scheduler-side check greps for it there; AnswerFileConsumer is
#          constructed at its resume use-site in task 16 (factory-injecting it here would leave an unused
#          field until 16 → CS0169 under the repo-wide TreatWarningsAsErrors, a false build break).
$factory = "src/Guardrails.Core/Execution/SchedulerFactory.cs"
$scheduler = "src/Guardrails.Core/Execution/Scheduler.cs"

if (-not (Test-Path $factory)) {
    Write-Output "$factory does not exist"
    exit 1
}
if (-not (Test-Path $scheduler)) {
    Write-Output "$scheduler does not exist"
    exit 1
}

$fc = Get-Content -Raw -Path $factory
foreach ($type in @('FileEscalationSink', 'CriticalityJudge', 'BlockerRetry')) {
    if ($fc -notmatch ("new\s+" + $type + "\b")) {
        Write-Output "SchedulerFactory.cs never constructs '$type' (no 'new $type(' in $factory) — the run-level escalation machinery is not wired at the production composition root (the #120 false-green: built but dead from the CLI). Construct + inject it under the autonomy block, mirroring how the factory constructs Overwatch/AiMergeWorker."
        exit 1
    }
}

$sc = Get-Content -Raw -Path $scheduler
if ($sc -notmatch 'GateClassifier') {
    Write-Output "Scheduler.cs never references GateClassifier — the classify-then-act dispatch at the needs-human/wave-checkpoint gate is not wired. The Scheduler must call GateClassifier.Classify(...) to route each gate."
    exit 1
}
if ($sc -notmatch 'FileEscalationSink') {
    Write-Output "Scheduler.cs never references FileEscalationSink — the escalate branch of classify-then-act is not wired (an at/above-threshold judgment-call gate must escalate via the injected FileEscalationSink)."
    exit 1
}
exit 0
