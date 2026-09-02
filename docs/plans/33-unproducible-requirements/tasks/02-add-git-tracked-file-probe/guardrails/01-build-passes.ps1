# catches: a probe addition that does not compile - most likely a constructor overload that breaks one
#          of the 73 existing call sites by changing an arity instead of defaulting a new parameter.
#          A green build is the cheapest gate here and it runs before the call-site census.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$log = & dotnet build Guardrails.sln -v q --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Build failures (detail re-emitted) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match 'error [A-Z]{2}\d+|: error ') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "The solution does not build after adding the git-tracked-file probe. The likeliest cause is one of the 73 existing `new PlanValidator(` call sites losing its overload: the new parameter must arrive with a DEFAULT so every existing arity still binds."
    exit 1
}
exit 0
