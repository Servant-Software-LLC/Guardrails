# catches: a union that left git conflict markers in - or emptied - a file this plan produces. The
#          deterministic verdict on EVERY union's bytes, never git's no-conflict signal and never an
#          AI-merge worker's say-so. THREE chains of this plan run in parallel (01->02->03->04,
#          05->06, and 07->08->09->10->11) and fan in at task 12, and four of the twenty paths below
#          are written by more than one task; an AI-merge that resolves a hunk badly can leave markers
#          behind with a zero exit code.
#
# This is the run's integration-guardrail set (scope:"integration", declared in the sidecar). The
# harness re-runs it on the merged bytes at EVERY union point AND on the final merged HEAD here, so it
# MUST assert only invariants that hold at every union - including an intermediate one where only a
# SUBSET of tasks has integrated. It is therefore CONDITIONAL throughout: "IF the file is present,
# verify it", never "REQUIRE it present". Requiring presence would red-halt a correct partial merge.
#
# Baseline note (#478): the "if present" half of a union-safe conditional is EXPECTED to be nonzero on
# arrival - MEASURED 2026-08-29 with a per-path Test-Path over the untouched tree, TEN of the twenty
# paths below already exist: Program.cs, PlanPreflightPhase.cs, Scheduler.cs, LogSiteRenderer.cs,
# OnTheFlyDiagramObserver.cs, HtmlDiagramRenderer.cs, LiveRunObserver.cs, ConsoleRunObserver.cs,
# docs/plans/02-schemas-and-contracts.md and the guardrails-domain-knowledge SKILL.md. The other ten
# are created by this plan. The conflict-marker clause is a forbidden-present clause and is exempt from
# the census (a ban green on arrival is a correct ban).
#
# Env/cwd: the attempt-decoupled re-verifier sets cwd to the union/integration worktree and does NOT
# set $GUARDRAILS_WORKSPACE. Resolve robustly: prefer $GUARDRAILS_WORKSPACE, else use cwd.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# Every file any task in this plan writes (the union of the twelve writeScopes, deduped).
$produced = @(
    'tests/Guardrails.Core.Tests/Samples/SampleVerifierTests.cs',
    'tests/Guardrails.Core.Tests/Samples/SampleVerifierWiringTests.cs',
    'src/Guardrails.Core/Samples/SampleVerifier.cs',
    'src/Guardrails.Cli/Commands/SamplesCommand.cs',
    'src/Guardrails.Cli/CharterCommands.cs',
    'src/Guardrails.Cli/Program.cs',
    'src/Guardrails.Cli/PlanPreflightPhase.cs',
    'tests/Guardrails.Core.Tests/Providers/BarrierWaitTests.cs',
    'src/Guardrails.Core/Providers/BarrierWait.cs',
    'src/Guardrails.Core/Execution/Scheduler.cs',
    'tests/Guardrails.Core.Tests/LogSite/ServeDiagramTests.cs',
    'src/Guardrails.Cli/Ui/LogSiteRenderer.cs',
    'src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs',
    'src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs',
    'tests/Guardrails.Core.Tests/Graph/DiagramRefreshTests.cs',
    'tests/Guardrails.Core.Tests/ModelTiering/ModelInRowTests.cs',
    'src/Guardrails.Cli/Ui/LiveRunObserver.cs',
    'src/Guardrails.Cli/ConsoleRunObserver.cs',
    'docs/plans/02-schemas-and-contracts.md',
    '.claude/skills/guardrails-domain-knowledge/SKILL.md'
)

$failures = @()
foreach ($rel in $produced) {
    $full = Join-Path $ws $rel
    if (-not (Test-Path $full -PathType Leaf)) {
        # Not produced at THIS union yet - fine. The conditional is what makes this union-safe.
        continue
    }

    $content = Get-Content -Raw -Path $full
    if ([string]::IsNullOrWhiteSpace($content)) {
        $failures += "$rel is EMPTY on the merged bytes - the union dropped its contents"
        continue
    }

    # Line-anchored ours/theirs markers only (a real conflict writes both at column 0); no bare
    # '=======' - unanchored it false-fires on a '====' banner, a Markdown setext underline or an ASCII
    # table rule, and two of the twenty paths above are large Markdown documents whose own prose is
    # full of rules and banners (#187).
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$rel contains git conflict markers - the union did not cleanly integrate"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
