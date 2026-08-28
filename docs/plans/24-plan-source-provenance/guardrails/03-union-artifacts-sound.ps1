# catches: a union that left git conflict markers in - or emptied - a file this plan produces. The
#          deterministic verdict on EVERY union's bytes, never git's no-conflict signal and never an
#          AI-merge worker's say-so. Two branches of this plan run in parallel (01->02 and 03->04) and
#          fan in at task 05, and the fan-in merges files that four different tasks have touched; an
#          AI-merge that resolves a hunk badly can leave markers behind with a zero exit code.
#
# This is the run's integration-guardrail set (scope:"integration", declared in the sidecar). The
# harness re-runs it on the merged bytes at EVERY union point AND on the final merged HEAD here, so it
# MUST assert only invariants that hold at every union - including an intermediate one where only a
# SUBSET of tasks has integrated. It is therefore CONDITIONAL throughout: "IF the file is present,
# verify it", never "REQUIRE it present". Requiring presence would red-halt a correct partial merge.
#
# Baseline note (#478): the "if present" half of a union-safe conditional is EXPECTED to be nonzero on
# arrival - four of the nine paths below already exist on the untouched tree - and the conflict-marker
# clause is a forbidden-present clause, which is exempt from the census (a ban green on arrival is a
# correct ban).
#
# Env/cwd: the attempt-decoupled re-verifier sets cwd to the union/integration worktree and does NOT
# set $GUARDRAILS_WORKSPACE. Resolve robustly: prefer $GUARDRAILS_WORKSPACE, else use cwd.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# Every file any task in this plan writes (the union of the six writeScopes).
$produced = @(
    'src/Guardrails.Core/Breakdown/PlanSourceRecord.cs',
    'src/Guardrails.Core/Breakdown/DeclaredCountGate.cs',
    'src/Guardrails.Core/Execution/InitialBreakdownInvoker.cs',
    'src/Guardrails.Cli/Commands/BreakdownCommand.cs',
    'tests/Guardrails.Core.Tests/PlanSource/PlanSourceRecordTests.cs',
    'tests/Guardrails.Core.Tests/PlanSource/DeclaredCountGateTests.cs',
    'tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs',
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
    # table rule, and two of the nine paths above are large Markdown documents (#187).
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$rel contains git conflict markers - the union did not cleanly integrate"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
