# catches: a union that merged the plan's contributions into a broken state — a conflict
#          marker left in a source file this plan touches. GR2028's crediting core: a
#          conflict-marker-freedom scan, which is ungameable, unlike a contribution-present
#          grep (which passes trivially when a merge DROPPED a contribution).
#
#          UNION-SAFE / CONDITIONAL (#125): every check is gated on the artifact being
#          present, so it passes trivially at a union where a contributing task has not
#          landed yet.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$failures = @()

$roots = @('src/Guardrails.Core', 'src/Guardrails.Cli', 'tests/Guardrails.Core.Tests',
           'tests/Guardrails.Integration.Tests')

foreach ($root in $roots) {
    if (-not (Test-Path $root)) { continue }   # union-safe: nothing here yet
    foreach ($f in Get-ChildItem -Path $root -Filter '*.cs' -File -Recurse) {
        $content = Get-Content -Raw -LiteralPath $f.FullName
        # Line-anchored ours/theirs only (#187) — a bare ======= false-fires on a banner
        # or a Markdown setext underline.
        if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
            $failures += "$($f.FullName) contains git conflict markers — the union did not cleanly integrate"
        }
    }
}

# #175: a 3-way merge of two siblings that each ADDED the same definition to different regions
# of one file produces NO conflict marker — git keeps both copies — so the scan above passes
# while the merged file holds a duplicate (the CS0101 that red-halted plan-0009's terminal
# gate). Conditional on the file being present, so it passes trivially at a union where a
# contributing task has not landed.
$definedOnce = @(
    @{ File = 'src/Guardrails.Core/Execution/SuppliedStagingTree.cs'; Pattern = 'class\s+SuppliedStagingTree' },
    @{ File = 'src/Guardrails.Core/Execution/SuppliedDrain.cs';       Pattern = 'class\s+SuppliedDrain' },
    @{ File = 'src/Guardrails.Core/Execution/SupplyCallerScope.cs';   Pattern = 'class\s+SupplyCallerScope' },
    @{ File = 'src/Guardrails.Core/Journal/SuppliedRecord.cs';        Pattern = '(class|record)\s+SuppliedRecord' }
)
foreach ($d in $definedOnce) {
    if (-not (Test-Path $d.File)) { continue }   # union-safe: not landed yet
    $n = ([regex]::Matches((Get-Content -Raw -LiteralPath $d.File), $d.Pattern)).Count
    if ($n -gt 1) {
        $failures += "$($d.File) declares '$($d.Pattern)' $n times — the AI-merge kept BOTH copies of a definition two siblings added (CS0101). No conflict marker is produced by this shape."
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== Union NOT sound ($($failures.Count) file(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
