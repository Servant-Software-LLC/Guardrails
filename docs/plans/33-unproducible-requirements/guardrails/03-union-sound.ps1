# catches: a union that did not cleanly integrate - the GR2028-crediting invariant. Three tasks (1, 2, 4)
#          write PlanValidator.cs and two (4, 8) write DiagnosticCodes.cs, so this plan has genuinely
#          overlapping writeScopes and the AI-merge has real work to do on both files. Two failure modes
#          are checked: literal conflict markers left in a merged file, and a DUPLICATE DEFINITION - the
#          #175 trap, where two branches each append the SAME new declaration to different regions and
#          the merge keeps BOTH with no textual conflict at all.
#
# UNION-SAFE / CONDITIONAL (#125): every check is gated on the artifact being PRESENT, so it passes
#          trivially at a union where a contributing task has not run yet. It never REQUIRES a
#          contribution; it verifies whatever is there. That is what lets it carry scope integration
#          without red-halting a correct partial merge.
#
# Marker regexes are LINE-ANCHORED (#187): a real conflict writes <<<<<<< and >>>>>>> at column 0. The
#          bare ======= form is deliberately NOT checked - it false-fires on a banner, a Markdown setext
#          underline, or an ASCII table rule, and would red-halt a correct run.
$ErrorActionPreference = 'Continue'

$failures = New-Object System.Collections.Generic.List[string]

$watched = @(
    'src/Guardrails.Core/Loading/PlanValidator.cs',
    'src/Guardrails.Core/Loading/DiagnosticCodes.cs',
    'src/Guardrails.Core/Loading/GuardrailClauseText.cs',
    'src/Guardrails.Core/Loading/ProducerCoverage.cs',
    'src/Guardrails.Core/Execution/Scheduler.cs',
    'docs/plans/02-schemas-and-contracts.md',
    'docs/plans/19-producer-coverage.md'
)

$seen = 0
foreach ($f in $watched) {
    if (-not (Test-Path -LiteralPath $f)) { continue }   # union-safe: not produced at this union yet
    $seen++
    $content = Get-Content -LiteralPath $f -Raw

    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures.Add('CONFLICT MARKERS IN ' + $f + ' - the union did not cleanly integrate. The merged file still carries an unresolved hunk.')
    }
    if ([string]::IsNullOrWhiteSpace($content)) {
        $failures.Add('EMPTY FILE AFTER MERGE: ' + $f + ' is present but blank. A produced file that survived the union with no content is a dropped contribution wearing a filename.')
    }
}

# #175 duplicate-definition check on the two files two tasks each define into. Conditional on presence.
$dupes = @{
    'src/Guardrails.Core/Loading/GuardrailClauseText.cs' = @('class\s+GuardrailClauseText')
    'src/Guardrails.Core/Loading/ProducerCoverage.cs'    = @('class\s+ProducerCoverage')
    'src/Guardrails.Core/Loading/DiagnosticCodes.cs'     = @('UnproducibleGateRequirement\s*=')
}
foreach ($f in $dupes.Keys) {
    if (-not (Test-Path -LiteralPath $f)) { continue }
    $content = Get-Content -LiteralPath $f -Raw
    foreach ($pat in $dupes[$f]) {
        $n = ([regex]::Matches($content, $pat)).Count
        if ($n -gt 1) {
            $failures.Add('DUPLICATE DEFINITION IN ' + $f + ': the pattern ' + $pat + ' occurs ' + $n + ' times. Two segments each added the same declaration to different regions and the AI-merge kept BOTH with no conflict marker - the #175 trap, which only the build catches otherwise.')
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The union did not integrate soundly (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output ("Union sound: " + $seen + " of " + $watched.Count + " watched files present at this union, none carrying conflict markers, none empty, no duplicate definitions.")
exit 0
