# catches: a non-fast-forward union (task 06's RunCommand.cs wiring branch and task
#          08's search-box branch both fan out from task 02 and merge back
#          independently) leaving unresolved git conflict markers in a shared file.
#          Union-safe/conditional (#125/#165/#187): gates on the file being present,
#          then checks it - passes trivially at an intermediate union before a
#          contributing task has landed, never requires a file to already exist.
$ws = $env:GUARDRAILS_WORKSPACE
$filesToCheck = @(
    "src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs",
    "src/Guardrails.Cli/Commands/RunCommand.cs",
    "src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs"
)

$failures = @()
foreach ($relPath in $filesToCheck) {
    $fullPath = Join-Path $ws $relPath
    if (-not (Test-Path $fullPath)) {
        continue
    }
    $content = Get-Content -Raw -Path $fullPath
    if ($content -match "(?m)^<<<<<<<" -or $content -match "(?m)^>>>>>>>") {
        $failures += "$relPath contains git conflict markers - the union did not cleanly integrate"
    }
}

if ($failures.Count -gt 0) {
    foreach ($f in $failures) {
        Write-Output $f
    }
    exit 1
}

exit 0
