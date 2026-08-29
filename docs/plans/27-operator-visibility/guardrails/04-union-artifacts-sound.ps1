# catches: a union that left git conflict markers in - or emptied - a file this plan produces. The
#          deterministic verdict on EVERY union's bytes, never git's no-conflict signal and never an
#          AI-merge worker's say-so. This plan is ONE STRICTLY SERIAL CHAIN
#          (01 -> 02 -> 03 -> 04 -> 05 -> 06 -> 07), so it has no PARALLEL siblings racing a shared
#          file - but it still merges a segment back onto the plan branch at every task boundary, and
#          a badly-resolved hunk there leaves markers behind with a zero exit code. THREE of the
#          seventeen paths below are written by more than one task in the chain - LogSiteRenderer.cs
#          by 01/02/06, OnTheFlyDiagramObserver.cs by 02/04, LiveRunObserver.cs by 05/06 - which is
#          exactly where a dropped or duplicated hunk would land.
#
# This is the run's integration-guardrail set (scope:"integration", declared in the sidecar). The
# harness re-runs it on the merged bytes at EVERY union point AND on the final merged HEAD here, so
# it MUST assert only invariants that hold at every union - including an intermediate one where only
# a SUBSET of tasks has integrated. It is therefore CONDITIONAL throughout: "IF the file is present,
# verify it", never "REQUIRE it present". Requiring presence would red-halt a correct partial merge.
#
# NO #175 duplicate-definition sub-check, and that is a decision, not an omission. That check exists
# for two COLLIDING SIBLINGS - two branches cut from a common base that each append the same new
# definition to different regions, which a 3-way merge keeps twice with no conflict marker. This
# plan's chain is strictly serial (each task.json's dependsOn is a single edge), so every task's
# segment base already contains its predecessor's merged output: no two tasks can add the same
# definition without the later one seeing the earlier one. The residual - a CS0101 arriving some
# other way - is caught by 01-solution-builds on the merged HEAD, whose failure line names these
# exact files first.
#
# Baseline note (#478): the "if present" half of a union-safe conditional is EXPECTED to be nonzero
# on arrival - RE-MEASURED 2026-08-29 with a per-path Test-Path over the untouched tree, FOURTEEN of
# the seventeen paths below already exist. The three the plan CREATES are
# tests/Guardrails.Integration.Tests/LogSite/ServeDiagramTests.cs (task 01),
# tests/Guardrails.Core.Tests/Graph/DiagramRefreshTests.cs (task 03) and
# tests/Guardrails.Integration.Tests/ModelTiering/ModelInRowTests.cs (task 05). The conflict-marker
# clause is a forbidden-present clause and is exempt from the census (a ban green on arrival is a
# correct ban).
#
# Env/cwd: the attempt-decoupled re-verifier sets cwd to the union/integration worktree and does NOT
# set $GUARDRAILS_WORKSPACE. Resolve robustly: prefer $GUARDRAILS_WORKSPACE, else use cwd.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# Every file any task in this plan writes - the union of the SEVEN writeScopes, deduped, and nothing
# else. A path no task here writes would be a silent lie in the one file whose whole job is honesty:
# it can never fail (the conditional gates it away) and it would misdescribe the plan's surface to
# the next reader. RE-DERIVED MECHANICALLY 2026-08-29 by reading all seven task.json writeScope
# arrays and deduping: 2+3+5+4+2+3+2 = 21 declarations, 17 distinct paths, and this list is exactly
# that set - no path missing, no path extra. Each path is commented with the FIRST task that declares
# it; the three multi-writer paths are named in the header above.
$produced = @(
    # task 01-author-tests-serve-diagram
    'tests/Guardrails.Integration.Tests/LogSite/ServeDiagramTests.cs',
    'src/Guardrails.Cli/Ui/LogSiteRenderer.cs',
    # task 02-serve-diagram-from-log-site
    'src/Guardrails.Cli/Ui/LogServer.cs',
    'src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs',
    # task 03-replace-meta-refresh
    'src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs',
    'tests/Guardrails.Core.Tests/Graph/DiagramRefreshTests.cs',
    'tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs',
    'tests/Guardrails.Integration.Tests/OnTheFlyDiagramTests.cs',
    'tests/Guardrails.Integration.Tests/RunCommandFinalSiteSettleTests.cs',
    # task 04-raise-attempt-route-resolved  (also writes OnTheFlyDiagramObserver.cs, listed under 02)
    'src/Guardrails.Core/Execution/IRunObserver.cs',
    'src/Guardrails.Core/Execution/TaskExecutor.cs',
    'src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs',
    # task 05-author-tests-model-in-row
    'tests/Guardrails.Integration.Tests/ModelTiering/ModelInRowTests.cs',
    'src/Guardrails.Cli/Ui/LiveRunObserver.cs',
    # task 06-render-model-in-row-and-index  (also writes LogSiteRenderer.cs and LiveRunObserver.cs)
    'src/Guardrails.Cli/ConsoleRunObserver.cs',
    # task 07-record-visibility-surfaces-in-ssot
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
    # '=======' - unanchored it false-fires on a '====' banner, a Markdown setext underline or an
    # ASCII table rule (#187).
    #
    # THE ANCHOR IS LOAD-BEARING HERE, MEASURED, NOT INHERITED AS FOLKLORE. Two of the seventeen paths
    # above are large Markdown documents that DISCUSS conflict markers in prose. Counted 2026-08-29
    # against the real docs/plans/02-schemas-and-contracts.md:
    #     '<<<<<<<'  4 occurrences anywhere,  0 at column 0
    #     '>>>>>>>'  3 occurrences anywhere,  0 at column 0
    #     '======='  4 occurrences anywhere,  0 at column 0
    # So an UNANCHORED scan - or a bare '=======' clause - would fire on that file at EVERY union and
    # red-halt every run of this plan, on prose, forever. The anchored form was executed against the
    # real repo while authoring this guardrail and exited 0; RE-EXECUTED 2026-08-29 after the plan
    # grew its seventh task, it exits 0 with 14 of the 17 paths present. Do not
    # "tighten" this by dropping the (?m)^ anchors or adding an equals-sign clause.
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$rel contains git conflict markers - the union did not cleanly integrate"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
