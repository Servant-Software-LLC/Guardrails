function Invoke-LiteRules {
    param([Parameter(Mandatory)]$Plan)
    $ids = @($Plan.Tasks | ForEach-Object { $_.Id })
    foreach ($t in $Plan.Tasks) {
        foreach ($d in @($t.Json.dependsOn)) {
            if ($d -and $ids -notcontains $d) {
                [pscustomobject]@{ code = 'GR2001'; severity = 'error'; message = "unknown dependency '$d'"; path = $t.Id }
            }
        }
    }
}
Export-ModuleMember -Function Invoke-LiteRules
