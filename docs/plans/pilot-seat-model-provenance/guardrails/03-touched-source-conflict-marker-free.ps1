# catches: a union that left git conflict markers on any source file this plan produced/modified.
#          scope:integration so it re-verifies at EVERY union; union-safe/CONDITIONAL - each file is
#          checked only IF present, so it passes trivially at a union where a contributing task has not
#          landed. Line-anchored markers (#187): a real conflict writes <<<<<<< / >>>>>>> at column 0.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$targets = @(
  'src/Guardrails.Core/Prompts/ClaudeStreamParser.cs',
  'src/Guardrails.Core/Prompts/PromptInvocation.cs',
  'src/Guardrails.Core/Prompts/ClaudePromptRunner.cs',
  'src/Guardrails.Core/Journal/JournalModel.cs',
  'src/Guardrails.Core/Execution/AttemptJournaler.cs',
  'src/Guardrails.Core/Execution/PromptExecutionSupport.cs',
  'src/Guardrails.Core/Execution/IRunObserver.cs',
  'src/Guardrails.Core/Execution/TaskExecutor.cs',
  'src/Guardrails.Core/Execution/RunReport.cs',
  'src/Guardrails.Cli/Ui/LiveRunObserver.cs',
  'src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs',
  'src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs',
  'src/Guardrails.Cli/ConsoleRunObserver.cs',
  'src/Guardrails.Cli/Commands/RunCommand.cs'
)
foreach ($rel in $targets) {
    $path = Join-Path $ws $rel
    if (-not (Test-Path $path)) { continue }
    $content = Get-Content -Raw -Path $path
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        Write-Output ($rel + ' contains git conflict markers - the union did not cleanly integrate')
        exit 1
    }
}
exit 0
