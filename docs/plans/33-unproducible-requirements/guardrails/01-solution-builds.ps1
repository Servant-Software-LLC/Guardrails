# catches: a merged plan-branch HEAD that does not build. Twelve tasks merge into this branch and three
#          of them edit PlanValidator.cs; a union that compiles in each segment but not together is
#          exactly what a terminal build gate is for.
#
# LOCAL, not scope integration (#165): a whole-solution build is a TERMINAL POSTCONDITION and would
#          red-halt a correct PARTIAL merge, where a downstream task has not run yet. It belongs here,
#          once, on the fully merged HEAD.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$log = & dotnet build Guardrails.sln -v q --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -ne 0) {
    Write-Output ''
    Write-Output '=== Build failures on the merged HEAD (detail re-emitted) ==='
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match 'error [A-Z]{2}\d+|: error ') { Write-Output $line }
    }
    Write-Output ''
    Write-Output 'The merged plan-branch HEAD does not build, so delivery is withheld. Tasks 1, 2 and 4 all edit PlanValidator.cs and are strictly sequential by dependsOn - a duplicate definition here would mean the AI-merge kept both copies of something two segments each added.'
    exit 1
}

Write-Output 'The merged plan-branch HEAD builds.'
exit 0
