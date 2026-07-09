# catches: the scaffold wave's EXIT gate leaving a union that dropped a leaf's contribution or left
#          conflict markers — wave-01 has TWO independent leaves (greet.ps1, config.json) that fan in
#          at the wave boundary, so its exit gate carries a scope:"integration" UNION-SAFE re-run
#          (GR2028). Union-safe = CONDITIONAL (gate-then-verify): if a contribution is present, verify
#          it is real; never REQUIRE it (a partial merge may not hold it yet). (#125/#165)
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
