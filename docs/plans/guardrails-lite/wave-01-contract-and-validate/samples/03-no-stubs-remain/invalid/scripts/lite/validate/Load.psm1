function Invoke-LiteLoad {
    param([Parameter(Mandatory)][string]$PlanDir)
    $diagnostics = @()
    if (-not (Test-Path -LiteralPath (Join-Path $PlanDir 'guardrails.json'))) {
        $diagnostics += [pscustomobject]@{ code = 'GR1001'; severity = 'error'; message = 'guardrails.json not found'; path = 'guardrails.json' }
        return @{ Plan = $null; Diagnostics = $diagnostics }
    }
    @{ Plan = [pscustomobject]@{ Root = $PlanDir; Tasks = @() }; Diagnostics = $diagnostics }
}
Export-ModuleMember -Function Invoke-LiteLoad
