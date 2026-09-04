# catches: an AI-merge that produced a syntactically-intact but semantically-broken union of two
#          siblings' contributions - a conflict the merge did not mark, or a file left empty. Re-runs
#          at EVERY union point (scope: integration), where the per-task guardrails cannot see.
#          UNION-SAFE / CONDITIONAL by construction (#125): every check is gated on the artifact being
#          PRESENT, so it passes trivially at a union where the contributing task has not run yet.
#          Conflict-marker freedom is what credits GR2028 here - a contribution-present grep cannot,
#          because its conditional form can never fail when a merge DROPPED a contribution entirely.
$ErrorActionPreference = 'Continue'
$failures = New-Object System.Collections.Generic.List[string]

$subjects = @(
    'src/Guardrails.Core/Execution/IRunObserver.cs',
    'src/Guardrails.Core/Execution/RunEventStream.cs',
    'src/Guardrails.Core/Execution/ObserverProjection.cs',
    'src/Guardrails.Core/Execution/AttemptJournaler.cs',
    'src/Guardrails.Core/Execution/TaskExecutor.cs',
    'src/Guardrails.Core/Execution/Scheduler.cs',
    'src/Guardrails.Cli/Commands/RunCommand.cs',
    'src/Guardrails.Cli/Commands/AttachCommand.cs',
    'src/Guardrails.Cli/Ui/LogServer.cs',
    'src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs',
    'src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs',
    'src/Guardrails.Cli/Ui/LiveRunObserver.cs',
    'src/Guardrails.Cli/ConsoleRunObserver.cs'
)

foreach ($rel in $subjects) {
    if (-not (Test-Path -LiteralPath $rel)) { continue }   # union-safe: absent is fine at this union
    $content = Get-Content -LiteralPath $rel -Raw
    if ([string]::IsNullOrWhiteSpace($content)) {
        $failures.Add("$rel is EMPTY in the union - a merge truncated a file every task in this plan builds on.")
        continue
    }
    # Line-anchored ours/theirs only (#187): a bare '=======' false-fires on a banner or a Markdown
    # setext underline and would red-halt a correct run.
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures.Add("$rel contains git conflict markers - the union did not cleanly integrate.")
    }
}

# Duplicate-definition check (#175): task 01 and the later writer tasks both edit RunEventStream.cs
# and ObserverProjection.cs. A 3-way merge that appended the SAME member in two regions keeps BOTH
# copies with no conflict marker - a CS0101 only the build catches. Gated on presence, as above.
foreach ($pair in @(
    @{ File = 'src/Guardrails.Core/Execution/RunEventStream.cs';    Member = 'private sealed record EventRow' },
    @{ File = 'src/Guardrails.Core/Execution/RunEventStream.cs';    Member = 'public void RunFinished' },
    @{ File = 'src/Guardrails.Core/Execution/ObserverProjection.cs'; Member = 'public void RunFinished' })) {

    if (-not (Test-Path -LiteralPath $pair.File)) { continue }
    $c = Get-Content -LiteralPath $pair.File -Raw
    $n = [regex]::Matches($c, [regex]::Escape($pair.Member)).Count
    if ($n -gt 1) {
        $failures.Add("$($pair.File) declares '$($pair.Member)' $n times - an AI-merge kept two copies of one member (CS0101, #175).")
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== Union integrity failures ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "Union integrity: every present subject is non-empty, conflict-marker-free, and declares each member once."
exit 0
