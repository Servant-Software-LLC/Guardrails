# catches: the scaffold wave's EXIT gate leaving a union that dropped a leaf's contribution or left
#          conflict markers — wave-01 has TWO independent leaves (greet.ps1, config.json) that fan in
#          at the wave boundary, so its exit gate carries a real integration re-run (GR2028).
#
# WHEN THIS RUNS: exactly ONCE, on the merged HEAD at the END of wave-01 (SSOT §14.3). It is LOCAL —
#          no `scope` key — because a wave-root scope:"integration" tag is INERT (GR2059, #459): the
#          per-union re-verify set is built from the task `<task>/guardrails/` folders plus the
#          PLAN-root `<plan>/guardrails/` folder only, never a wave root. A union invariant that must
#          re-run at every union belongs at the plan root.
# SHAPE:    kept CONDITIONAL (gate-then-verify — if a contribution is present, verify it is real)
#          rather than assert-present, because the paired assert-present check for these same two
#          artifacts is wave-02's ENTRY gate (`wave-02-greet/preflights/01-scaffold-materialized.ps1`):
#          one boundary, two authored folders.
foreach ($rel in @('out/greet.ps1', 'out/config.json')) {
    if (-not (Test-Path $rel)) { continue }   # not integrated at this union yet — fine
    $content = Get-Content -Raw -Path $rel
    if ([string]::IsNullOrWhiteSpace($content)) {
        Write-Output "$rel is empty on the merged bytes — the scaffold wave produced a hollow file"
        exit 1
    }
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        Write-Output "$rel contains git conflict markers — the scaffold union did not cleanly integrate"
        exit 1
    }
}
exit 0
