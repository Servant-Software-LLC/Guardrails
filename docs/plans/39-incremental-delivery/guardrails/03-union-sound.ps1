# catches: a union that merged this plan's contributions into a broken state — a conflict marker
#          left in a source file it touches. GR2028's crediting core is a conflict-marker-freedom
#          scan.
#
#          CORRECTED AT REVIEW (2026-09-11): an earlier version of this header said the marker
#          scan was "ungameable" and contrasted it with a contribution-present grep that "passes
#          trivially when a merge DROPPED a contribution". That has it backwards — the MARKER
#          SCAN is the one that passes trivially over a silently dropped hunk, because a drop
#          leaves no marker. The scan is kept because it is cheap and union-safe, the
#          duplicate-definition count below covers the #175 half, and the dropped-hunk half
#          remains an accepted residual (#132) rather than something this file catches.
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

# #175: a 3-way merge of two siblings that each ADDED the same definition to different regions of
# one file produces NO conflict marker — git keeps both copies — so the scan above passes while
# the merged file holds a duplicate (the CS0101 that red-halted plan-0009's terminal gate).
# Conditional on the file being present, so it passes trivially at a union where a contributing
# task has not landed.
$definedOnce = @(
    @{ File = 'src/Guardrails.Core/Journal/WaveDeliveredRecord.cs'; Pattern = '(class|record)\s+WaveDeliveredRecord' },
    @{ File = 'src/Guardrails.Core/Model/WaveNode.cs';              Pattern = '(class|record)\s+WaveNode' },
    @{ File = 'src/Guardrails.Core/Execution/DecisionEntry.cs';     Pattern = '(class|record)\s+DecisionEntry' }
)
foreach ($d in $definedOnce) {
    if (-not (Test-Path $d.File)) { continue }   # union-safe: not landed yet
    $n = ([regex]::Matches((Get-Content -Raw -LiteralPath $d.File), $d.Pattern)).Count
    if ($n -gt 1) {
        $failures += "$($d.File) declares '$($d.Pattern)' $n times — the AI-merge kept BOTH copies of a definition two siblings added (CS0101). This shape produces no conflict marker."
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== Union NOT sound ($($failures.Count) file(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
