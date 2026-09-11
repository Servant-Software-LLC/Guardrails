# catches: a union that merged this plan's contributions into a broken state — a conflict marker
#          left in a source file it touches. GR2028's crediting core is a conflict-marker-freedom
#          scan, which is ungameable; a contribution-present grep passes trivially when a merge
#          DROPPED a contribution.
#          UNION-SAFE / CONDITIONAL (#125): gated on the artifact being present, so it passes
#          trivially at a union where a contributing task has not landed.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$failures = @()
$roots = @('src/Guardrails.Core', 'src/Guardrails.Cli', 'tests/Guardrails.Core.Tests',
           'tests/Guardrails.Integration.Tests')

foreach ($root in $roots) {
    if (-not (Test-Path $root)) { continue }
    foreach ($f in Get-ChildItem -Path $root -Filter '*.cs' -File -Recurse) {
        $content = Get-Content -Raw -LiteralPath $f.FullName
        # Line-anchored ours/theirs only (#187): a bare ======= false-fires on a banner rule.
        if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
            $failures += "$($f.FullName) contains git conflict markers — the union did not cleanly integrate"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== Union NOT sound ($($failures.Count) file(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
