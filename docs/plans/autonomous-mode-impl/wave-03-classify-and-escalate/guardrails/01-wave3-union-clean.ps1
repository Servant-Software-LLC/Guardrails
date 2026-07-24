# catches: a union at wave-03's exit that left git conflict markers on any file wave-03 produced or
#          modified (a non-clean integration of the forensic / classifier / blocker / assessment / sink /
#          answer-consumption / wiring branches). Union-safe / CONDITIONAL (#125/#165): gate on each file
#          being present, THEN scan it — passes trivially at a partial merge where a contribution has not
#          landed yet. Line-anchored markers only (#187). This is wave-03's GR2028 integration re-run.
#          scope: integration. wave-03 is an INTERMEDIATE wave, so whole-build/whole-suite stay LOCAL
#          (see 02/03) and this is the union-safe invariant.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$files = @(
    'src/Guardrails.Core/Execution/DecisionEntry.cs',
    'src/Guardrails.Core/Journal/RunJournal.cs',
    'src/Guardrails.Core/Journal/AutonomyJsonl.cs',
    'src/Guardrails.Core/Execution/GateClassifier.cs',
    'src/Guardrails.Core/Execution/BlockerRetry.cs',
    'src/Guardrails.Core/Execution/CriticalityJudge.cs',
    'src/Guardrails.Core/Execution/IEscalationSink.cs',
    'src/Guardrails.Core/Execution/FileEscalationSink.cs',
    'src/Guardrails.Core/Execution/AnswerFile.cs',
    'src/Guardrails.Core/Execution/AnswerFileConsumer.cs',
    'src/Guardrails.Core/Prompts/PromptComposer.cs',
    'src/Guardrails.Core/Execution/ActionRunner.cs',
    'src/Guardrails.Core/Execution/SchedulerFactory.cs',
    'src/Guardrails.Core/Execution/Scheduler.cs',
    'src/Guardrails.Cli/Commands/RunCommand.cs',
    'src/Guardrails.Cli/ExitCodes.cs',
    'docs/plans/02-schemas-and-contracts.md'
)
foreach ($rel in $files) {
    $path = Join-Path $ws $rel
    if (-not (Test-Path $path)) { continue }   # not produced at this union yet — nothing to verify
    $content = Get-Content -Raw -Path $path
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        Write-Output "$rel contains git conflict markers — the wave-03 union did not cleanly integrate"
        exit 1
    }
}
exit 0
