# catches: the config is missing, unparseable, or has a blank name — the second stage would then
#          greet nobody.
if (-not (Test-Path "out/config.json")) {
    Write-Output "out/config.json does not exist in the workspace"
    exit 1
}
try {
    $cfg = Get-Content -Raw -Path "out/config.json" | ConvertFrom-Json
} catch {
    Write-Output "out/config.json is not valid JSON: $_"
    exit 1
}
if ([string]::IsNullOrWhiteSpace($cfg.name)) {
    Write-Output "out/config.json has no non-empty 'name' — nobody to greet"
    exit 1
}
exit 0
