# catches: a collapsed data-model task whose "round-trip" tests are hollow - passing without actually
#          exercising the new sections/outcome. Structural [Fact] presence + distinctive-token lower bound
#          (a comment still matches; residual is human review), scoped to the files this task owns. Also
#          confirms the two new journal sections actually exist in the model source.
$f = Get-Content "tests/Guardrails.Core.Tests/JournalOutcomesRoundTripTests.cs" -Raw
if ($f -notmatch '(?m)^\s*\[(Fact|Theory)\]') {
    Write-Output "JournalOutcomesRoundTripTests.cs declares no [Fact]/[Theory] test - the tokens appear only as text"
    exit 1
}
if ($f -notmatch 'planPreflights|PlanPreflights') {
    Write-Output "JournalOutcomesRoundTripTests.cs does not exercise the planPreflights section"
    exit 1
}
if ($f -notmatch 'planGuardrails|PlanGuardrails') {
    Write-Output "JournalOutcomesRoundTripTests.cs does not exercise the planGuardrails section"
    exit 1
}
if ($f -notmatch 'task-preflight-failed|TaskPreflight') {
    Write-Output "JournalOutcomesRoundTripTests.cs does not exercise the task-preflight-failed outcome"
    exit 1
}
# the model actually gained the sections (not just a test that reads a hand-rolled JSON string)
$model = Get-Content (Get-ChildItem "src/Guardrails.Core/Journal" -Filter *.cs | ForEach-Object FullName) -Raw
if (($model -join "`n") -notmatch 'PlanPreflights' -or ($model -join "`n") -notmatch 'PlanGuardrails') {
    Write-Output "the Journal model does not declare PlanPreflights / PlanGuardrails sections - the additive sections were not added to JournalDocument"
    exit 1
}
exit 0
