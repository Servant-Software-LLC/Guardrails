# catches: the script is missing, or exists but is broken — wrong param handling, a syntax error,
#          or output that doesn't match the required 'Hello, <name>!' shape.
if (-not (Test-Path "out/greet.ps1")) {
    Write-Output "out/greet.ps1 does not exist in the workspace"
    exit 1
}
$output = & "out/greet.ps1" -Name "Smoke" 2>&1
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
    Write-Output "out/greet.ps1 exited non-zero for a sample name: $output"
    exit 1
}
if ("$output" -notmatch '^Hello, Smoke!$') {
    Write-Output "out/greet.ps1 printed '$output' instead of 'Hello, Smoke!'"
    exit 1
}
exit 0
