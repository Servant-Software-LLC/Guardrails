function Invoke-LiteRules {
    param([Parameter(Mandatory)]$Plan)
    if ($Plan.Config.PSObject.Properties.Name -notcontains 'profile') {
        [pscustomobject]@{ code = 'GR2090'; severity = 'warning'; message = "plan is not marked 'profile': 'lite'"; path = 'guardrails.json' }
    }
}
Export-ModuleMember -Function Invoke-LiteRules
