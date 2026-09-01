# catches: a journal change that is not additive at the SIGNATURE level. All three RunJournal recorders
#          already take an optional 'string? definitionHash = null'; this stage adds a SECOND optional
#          parameter beside it. If it is added without a default, or added in front of an existing
#          optional parameter, every call site breaks - and several of them are in Guardrails.Cli, a
#          different assembly, which is why it is built here rather than left to the terminal gate.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). These are builds.
$failures = @()

foreach ($project in @('src/Guardrails.Core', 'src/Guardrails.Cli', 'tests/Guardrails.Core.Tests')) {
    dotnet build $project --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $failures += "$project does not build."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output "The Cli project is built here because RunJournal's recorders are called from it. If a recorder's new parameter is not OPTIONAL with a default, every existing call site breaks - and the ones outside Core are the ones this stage would otherwise discover late."
    exit 1
}
exit 0
