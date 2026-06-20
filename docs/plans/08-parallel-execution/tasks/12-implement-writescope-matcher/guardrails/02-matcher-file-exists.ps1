# catches: tests made to pass by some other means - the matcher file WriteScope.cs the §2.1 spec
#          names was never actually written (scoped to the one file this task owns)
$file = "src/Guardrails.Core/Execution/WriteScope.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the write-scope matcher was not implemented"
    exit 1
}
exit 0
