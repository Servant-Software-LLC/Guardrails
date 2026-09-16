# catches: a union that merged this plan's contributions into a broken state - a conflict marker
#          left in a source file it touches, or a definition an AI-merge kept TWICE.
#          This is the GR2028-crediting content of the terminal gate: a line-anchored
#          conflict-marker-freedom scan. Be honest about its limit - the marker scan passes
#          trivially over a hunk a merge silently DROPPED, because a drop leaves no marker. The
#          dropped-hunk half is the accepted #132 residual, not something this file catches; the
#          duplicate-definition count below covers the #175 half, which produces no marker either.
#          UNION-SAFE / CONDITIONAL (#125): every check is gated on the artifact being present, so
#          it passes trivially at a union where a contributing task has not landed yet.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$failures = @()
$roots = @('src/Guardrails.Core', 'src/Guardrails.Cli', 'tests/Guardrails.Core.Tests',
           'tests/Guardrails.Integration.Tests')

foreach ($root in $roots) {
    if (-not (Test-Path $root)) { continue }
    foreach ($f in Get-ChildItem -Path $root -Filter '*.cs' -File -Recurse) {
        $content = Get-Content -Raw -LiteralPath $f.FullName
        # Line-anchored ours/theirs only (#187): a bare ======= false-fires on a banner rule or a
        # Markdown setext underline, red-halting a correct run.
        if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
            $failures += "$($f.FullName) contains git conflict markers - the union did not cleanly integrate"
        }
    }
}

# #175: a 3-way merge of two contributions that each ADDED the same definition to different regions
# of one file produces NO conflict marker - git keeps both copies - so the scan above passes while
# the merged file holds a duplicate (the CS0101 that red-halted plan-0009's terminal gate). Each
# entry is conditional on its file being present, so it is union-safe.
$definedOnce = @(
    @{ File = 'src/Guardrails.Core/Execution/MissingResourceSignal.cs'; Pattern = '(class|record)\s+MissingResourceSignal\b' },
    @{ File = 'src/Guardrails.Core/Execution/MissingResourceFacts.cs';  Pattern = '(class|record)\s+MissingResourceFacts\b' },
    @{ File = 'src/Guardrails.Core/Execution/GateThreshold.cs';         Pattern = '(class|record)\s+GateThreshold\b' },
    @{ File = 'src/Guardrails.Core/Execution/OverwatchDecision.cs';     Pattern = '(class|record)\s+OverwatchSupplyAutoResolve\b' },
    @{ File = 'src/Guardrails.Core/Execution/OverwatchDecision.cs';     Pattern = '(class|record|struct)\s+SupplyCertification\b' },
    @{ File = 'src/Guardrails.Core/Execution/SuppliedDrain.cs';         Pattern = '(class|record)\s+SuppliedDrain\b' },
    @{ File = 'src/Guardrails.Core/Execution/RunOutcomePolicy.cs';      Pattern = '(class|record)\s+RunOutcomePolicy\b' },
    @{ File = 'src/Guardrails.Core/Execution/OverwatchTrigger.cs';      Pattern = 'enum\s+OverwatchTrigger\b' }
)
foreach ($d in $definedOnce) {
    if (-not (Test-Path $d.File)) { continue }   # union-safe: not landed at this union yet
    $n = ([regex]::Matches((Get-Content -Raw -LiteralPath $d.File), $d.Pattern)).Count
    if ($n -gt 1) {
        $failures += "$($d.File) declares '$($d.Pattern)' $n times - the AI-merge kept BOTH copies of a definition (CS0101). This shape produces no conflict marker."
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== Union NOT sound ($($failures.Count) finding(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
