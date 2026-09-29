function Get-LitePlanDefinitionHash {
    param([Parameter(Mandatory)][string]$PlanDir)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((Get-Content -Raw -LiteralPath (Join-Path $PlanDir 'guardrails.json')))
    'sha256:' + (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '')
}
Export-ModuleMember -Function Get-LitePlanDefinitionHash
