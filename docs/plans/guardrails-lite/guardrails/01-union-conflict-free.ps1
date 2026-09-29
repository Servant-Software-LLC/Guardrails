# catches: a union that did not cleanly integrate - an AI-merge or 3-way merge of two Lite tasks that left
#          git conflict markers in a kernel script, a Lite test class or a fixture. Every task's own
#          guardrails ran on its OWN segment, before the union existed, so none of them can see this.
#          UNION-SAFE / CONDITIONAL (#125/#165): it scans only what IS present and passes trivially when
#          nothing has landed yet (a wave-01 fan-in before any Lite file exists), so it is correct at every
#          intermediate union - which is why it may carry scope:"integration" (GR2028 credit: a
#          conflict-marker-freedom check). Markers are LINE-ANCHORED at column 0 (#187): a `====` banner,
#          a Markdown setext underline or an ASCII table rule must not false-red a correct run, so a bare
#          `=======` is deliberately NOT checked.
# Scope of the scan: scripts/lite/** and tests/Guardrails.Integration.Tests/Lite/** plus the Core contract
#          test - the surfaces this plan's tasks write.
# Measured baseline (#478): 0 markers in those trees at breakdown time (they do not exist yet) - a
#          forbidden-present ban, green on arrival by design.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$ws = if ($env:GR_SUBJECT) { $env:GR_SUBJECT }
      elseif ($args.Count -gt 0 -and $args[0]) { $args[0] }
      elseif ($env:GUARDRAILS_WORKSPACE) { $env:GUARDRAILS_WORKSPACE }
      else { (Get-Location).Path }

$roots = @('scripts/lite', 'tests/Guardrails.Integration.Tests/Lite', 'tests/Guardrails.Core.Tests/Loading/LiteProfileContractTests.cs')
$files = @()
foreach ($r in $roots) {
    $p = Join-Path $ws $r
    if (Test-Path -LiteralPath $p -PathType Container) {
        $files += @(Get-ChildItem -LiteralPath $p -Recurse -File)
    }
    elseif (Test-Path -LiteralPath $p -PathType Leaf) {
        $files += @(Get-Item -LiteralPath $p)
    }
    # absent: nothing produced at this union yet - fine (union-safe).
}

$hits = @()
foreach ($f in $files) {
    $content = [System.IO.File]::ReadAllText($f.FullName)
    if ($content -cmatch '(?m)^<<<<<<< ' -or $content -cmatch '(?m)^<<<<<<<$' -or
        $content -cmatch '(?m)^>>>>>>> ' -or $content -cmatch '(?m)^>>>>>>>$') {
        $hits += ([System.IO.Path]::GetRelativePath($ws, $f.FullName) -replace '\\', '/')
    }
}

if ($hits.Count -gt 0) {
    Write-Output "=== $($hits.Count) file(s) carry git conflict markers - the union did not cleanly integrate ==="
    $hits | ForEach-Object { Write-Output "  $_" }
    exit 1
}
exit 0
