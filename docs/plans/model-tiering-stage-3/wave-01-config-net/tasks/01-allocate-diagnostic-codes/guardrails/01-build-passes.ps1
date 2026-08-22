# catches: a constant declaration that does not compile - a duplicate member name, a stray comma, a
#          malformed XML doc comment. Cheapest check first: with the build green, the structural check
#          in 02 is reading a file the compiler already accepted, so a failure there is about the
#          ALLOCATION being wrong rather than the syntax.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# -v q IS correct on a BUILD (dotnet.md §4); it is the TEST command that must never carry it (#179).
$out = dotnet build src/Guardrails.Core/Guardrails.Core.csproj -v q --nologo 2>&1
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
    Write-Output "Guardrails.Core does not compile after the code allocation - see the errors above"
    exit 1
}
exit 0
