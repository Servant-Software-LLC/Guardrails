# catches: a union that left git conflict markers or emptied a shared multi-writer file - notably
#          docs/plans/02-schemas-and-contracts.md (edited by 04/06/08/10/14), the Execution/ files
#          (08/10/12), and the Cli/ files (08/10/14) - the genuine parallel multi-writers (#132).
#          scope:"integration" -> re-runs at EVERY union (SSOT §4.3), so it MUST be UNION-SAFE (#125):
#          gate on each file being present, THEN verify only INVARIANTS true of any valid intermediate
#          union (non-empty + conflict-marker-free). It does NOT assert contribution-PRESENCE - that is a
#          terminal postcondition (a token like plan:guardrails from task 10/wave-5 is legitimately absent
#          at an earlier union where task 14/wave-4 has already landed), so it lives in the LOCAL
#          04-ssot-complete.ps1 on this gate instead (#125). The CS0101 same-definition duplicate (#175)
#          cannot arise here: 08 and 10 add DIFFERENT phases to Scheduler.cs and are serialized (10
#          dependsOn 08); the LOCAL solution build (02) is the backstop for any residual duplicate.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

function Test-UnionFile([string]$rel) {
    $p = Join-Path $ws $rel
    if (-not (Test-Path $p)) { return $null }   # not integrated at this union yet - nothing to verify
    $content = Get-Content -Raw -Path $p
    if ([string]::IsNullOrWhiteSpace($content)) { return "$rel is empty on the merged bytes - a union dropped its whole contribution" }
    # Line-anchored markers only: a real git conflict always writes BOTH `<<<<<<<` and `>>>>>>>` at
    # column 0. An UNanchored `-match '======='` false-positives on a legitimate `====` banner / setext
    # header / ASCII table (e.g. ConsoleRunObserver.cs prints a 64-char `=` rule) and red-halts a correct
    # union. No legit source/markdown line starts with 7 `<` or 7 `>`, so this is false-positive-free.
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        return "$rel contains git conflict markers - the union did not cleanly integrate"
    }
    return ""
}

$shared = @(
    'docs/plans/02-schemas-and-contracts.md',
    'src/Guardrails.Core/Execution/Scheduler.cs',
    'src/Guardrails.Core/Execution/SchedulerFactory.cs',
    'src/Guardrails.Core/Execution/TaskExecutor.cs',
    'src/Guardrails.Core/Loading/DiagnosticCodes.cs'
)
foreach ($rel in $shared) {
    $msg = Test-UnionFile $rel
    if ($msg) { Write-Output $msg; exit 1 }
}

# The CLI files are written concurrently by 08/10 (run/revalidate) and 14 (graph) - scan every present
# .cs there for conflict markers too (union-safe: only files present at this union are checked).
# Line-anchored markers only (see Test-UnionFile above - an unanchored `=======` false-positives on the
# `====` banner ConsoleRunObserver.cs legitimately prints).
$cliDir = Join-Path $ws 'src/Guardrails.Cli'
if (Test-Path $cliDir) {
    foreach ($f in Get-ChildItem -Path $cliDir -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue) {
        $c = Get-Content -Raw -Path $f.FullName
        if ($c -match '(?m)^<<<<<<<' -or $c -match '(?m)^>>>>>>>') {
            Write-Output ("src/Guardrails.Cli/" + $f.Name + " contains git conflict markers - the union did not cleanly integrate")
            exit 1
        }
    }
}
exit 0
