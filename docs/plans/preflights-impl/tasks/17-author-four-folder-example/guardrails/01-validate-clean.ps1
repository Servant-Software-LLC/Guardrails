# catches: the re-authored example folder does not validate against the NEW four-folder loader. Uses the
#          FRESHLY-BUILT CLI (`dotnet run --project src/Guardrails.Cli`), NOT the installed `guardrails` on
#          PATH (preview.34 does not understand the four folders). Re-emits the validator output tail so the
#          retry sees WHY.
$out = dotnet run --project src/Guardrails.Cli -c Debug -- validate docs/plans/09-preflight-first-class/example 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "the four-folder example does not `guardrails validate` clean against the freshly-built CLI (see output above)"
    exit 1
}
exit 0
