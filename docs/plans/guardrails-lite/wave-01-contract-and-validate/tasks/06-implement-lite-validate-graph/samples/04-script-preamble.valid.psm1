#Requires -Version 7
# Lite graph/scope rules - a complete, representative opening of a correct rule module.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-LiteRules {
    param($Plan)
    $diagnostics = [System.Collections.Generic.List[object]]::new()
    $ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($t in $Plan.Tasks) { [void]$ids.Add($t.Id) }
    foreach ($t in $Plan.Tasks) {
        $deps = $t.Json.PSObject.Properties['dependsOn']
        foreach ($d in @(if ($deps) { $deps.Value } else { @() })) {
            if (-not $ids.Contains($d)) {
                $diagnostics.Add([pscustomobject]@{ code = 'GR2001'; severity = 'error'; message = "Task '$($t.Id)' dependsOn '$d', which is not a known task id."; path = $t.Dir })
            }
        }
    }
    return $diagnostics.ToArray()
}

Export-ModuleMember -Function Invoke-LiteRules