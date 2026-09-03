# catches: a union that did not cleanly integrate - a merge that left git conflict markers in a file two
#          tasks both wrote, or that dropped a sibling's hunk. This is the UNION-SOUNDNESS proof, re-run
#          at every integration point; the build and the suite beside it are terminal postconditions and
#          run only once, on the merged HEAD.
# UNION-SAFE / CONDITIONAL (#125): every check is gated on the artifact being PRESENT, so it passes
# trivially at a union where the contributing task has not run yet, and tightens once that hunk lands.
# Measured baselines (#478): the two conditional clauses below are the "if X is present" half of a
# union-safe conditional, which is the named exemption - they are expected to be inert until their
# contributing task lands, and neither is a required-present clause on the starting tree.
$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]

# 1. Conflict-marker freedom over every file this plan's tasks write. Line-anchored (#187): a real
#    conflict writes both markers at column 0, while an unanchored '=======' false-fires on a banner
#    or a Markdown setext underline and would red-halt a correct run.
$targets = @(
    'src/Guardrails.Core/Execution/IRunObserver.cs',
    'src/Guardrails.Core/Execution/TaskExecutor.cs',
    'src/Guardrails.Core/Execution/RunEventStream.cs',
    'src/Guardrails.Core/Execution/ObserverProjection.cs',
    'src/Guardrails.Cli/Commands/RunCommand.cs',
    'src/Guardrails.Cli/Commands/AttachCommand.cs',
    'src/Guardrails.Cli/Ui/LogServer.cs',
    'src/Guardrails.Cli/Ui/LiveRunObserver.cs',
    'src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs',
    'src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs',
    'src/Guardrails.Cli/ConsoleRunObserver.cs'
)
foreach ($t in $targets) {
    if (-not (Test-Path -LiteralPath $t)) { continue }   # not produced at this union yet - fine
    $c = Get-Content -LiteralPath $t -Raw
    if ($c -match '(?m)^<<<<<<<' -or $c -match '(?m)^>>>>>>>') {
        $failures.Add("$t contains git conflict markers - the union did not cleanly integrate")
    }
    if ([string]::IsNullOrWhiteSpace($c)) {
        $failures.Add("$t is present but EMPTY - the union dropped its contents")
    }
}

# NOTE (review finding, #125): a contribution-present check for the wired projections used to live
# here. It was gated on task 13's contribution (the extracted BuildObserverChain seam) while ASSERTING
# task 15's (the constructed projections) - so at every union between those two merges it fired on a
# perfectly valid partial state and red-halted a correct run. Reproduced at review time: exit 1 with
# "has the extracted BuildObserverChain seam but never constructs RunEventStream".
# A scope:"integration" file re-runs at EVERY union (SSOT 4.3), so it may assert ONLY what is true of
# any valid intermediate union. The assertion it was reaching for is a TERMINAL postcondition about
# task 15's own output and lives there, LOCAL: tasks/15-*/guardrails/02-wiring-tests-pass.ps1, which
# drives the real composed chain. GR2028 is credited by the conflict-marker scan above, not by that block.

if ($failures.Count -gt 0) {
    Write-Output "=== Union not intact ($($failures.Count) problem(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
