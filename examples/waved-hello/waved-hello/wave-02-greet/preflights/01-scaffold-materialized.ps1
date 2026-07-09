# catches: wave-02 starting before wave-01's outputs are materialized on the branch — the entry gate
#          IS the "prior wave's outputs materialized" check (SSOT §14.3): terminal-gate-of-wave-1 ==
#          preflight-of-wave-2, one boundary, two authored folders. This is the #181 POSITIVE-baseline
#          archetype applied at the wave boundary — assert the artifacts this wave builds on are
#          present + real before its DAG spends a turn against possibly-absent bytes.
foreach ($rel in @('out/greet.ps1', 'out/config.json')) {
    if (-not (Test-Path $rel)) {
        Write-Output "$rel not materialized on the branch — wave-01's scaffold output is missing; wave-02 cannot build on it"
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace((Get-Content -Raw -Path $rel))) {
        Write-Output "$rel is present but empty — wave-01's scaffold did not materialize real content"
        exit 1
    }
}
exit 0
