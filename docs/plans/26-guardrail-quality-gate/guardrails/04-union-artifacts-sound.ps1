# catches: a union that left git conflict markers in - or emptied - a file this plan produces. The
#          deterministic verdict on EVERY union's bytes, never git's no-conflict signal and never an
#          AI-merge worker's say-so.
#
#          Be honest about the topology, because it is not the usual one: this plan is a SINGLE SERIAL
#          CHAIN (01 -> 02 -> 03 -> 04 -> 05). Nothing runs concurrently, so there is no
#          sibling-collision surface and no duplicate-definition hazard (#175) to check for. Exactly
#          one of the eight paths below is written by two tasks -
#          src/Guardrails.Core/Samples/SampleVerifier.cs, the stub from task 01 superseded by the
#          implementation from task 02 - and because 02's segment is based on 01's already-merged
#          output that is a clean supersede, not a merge of two independent hunks.
#
#          What is still real, and is what this file is for: a serial chain still has FIVE unions, one
#          per task segment merging back to the plan branch, and this guardrail is re-run on the merged
#          bytes at every one of them. Two of the eight paths are LARGE MARKDOWN DOCUMENTS that task 05
#          modifies by anchored edit rather than rewrite - docs/plans/02-schemas-and-contracts.md
#          (~533 KB) and .claude/skills/guardrails-domain-knowledge/SKILL.md (~115 KB, written through
#          needsHarnessWrite `edits` because it is over the 64 KB full-content ceiling). An anchored
#          edit applied to a half-megabyte document is exactly where a bad resolution silently truncates
#          or empties a file while every process involved reports success, and no build and no test
#          suite in this plan reads either document. This check is the only thing that does.
#
#          It is also the file that CREDITS GR2028. The plan's other three terminal checks are LOCAL
#          terminal postconditions by necessity (a whole-solution build and two whole-suite runs cannot
#          be tagged scope:"integration" without red-halting correct partial merges, #125/#165), so this
#          conditional conflict-marker-freedom scan is the plan's one real per-union invariant.
#
# This is the run's integration-guardrail set (scope:"integration", declared in the sidecar). The
# harness re-runs it on the merged bytes at EVERY union point AND on the final merged HEAD here, so it
# MUST assert only invariants that hold at every union - including an intermediate one where only a
# SUBSET of tasks has integrated. It is therefore CONDITIONAL throughout: "IF the file is present,
# verify it", never "REQUIRE it present". Requiring presence would red-halt every union before the
# last, which on a serial chain is all of them.
#
# Baseline note (#478): the "if present" half of a union-safe conditional is EXPECTED to be nonzero on
# arrival - MEASURED 2026-08-29 with a per-path Test-Path over the untouched tree, FOUR of the eight
# paths below already exist (src/Guardrails.Cli/CommandFactory.cs, src/Guardrails.Cli/PlanPreflightPhase.cs,
# docs/plans/02-schemas-and-contracts.md and the guardrails-domain-knowledge SKILL.md); the other four
# are created by this plan (SampleVerifierTests.cs, SampleVerifier.cs, SamplesCommand.cs,
# SampleVerifierWiringTests.cs). The conflict-marker clause is a forbidden-present clause and is exempt
# from the census (a ban green on arrival is a correct ban).
#
# THE LINE ANCHOR IS NOT DECORATION HERE - IT IS MEASURED (#187). On the untouched tree,
# docs/plans/02-schemas-and-contracts.md contains `<<<<<<<` on 4 lines, `>>>>>>>` on 3 lines and
# `=======` on 4 lines - in prose ABOUT conflict markers, none of them at column 0 (measured: `^<<<<<<<`
# and `^>>>>>>>` both return 0 in every one of the four existing paths). So an UNANCHORED marker scan
# would fire on the SSOT immediately and red-halt EVERY union of this plan, and a bare `=======` clause
# would do the same. Both are omitted deliberately; a real conflict writes `<<<<<<<` and `>>>>>>>` at
# column 0, which is what is matched.
#
# Env/cwd: the attempt-decoupled re-verifier sets cwd to the union/integration worktree and does NOT
# set $GUARDRAILS_WORKSPACE. Resolve robustly: prefer $GUARDRAILS_WORKSPACE, else use cwd.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# EXACTLY the union of this plan's five writeScopes, deduped - nothing else. A path here that no task
# writes is a silent lie in the one file whose entire job is honesty, so each entry names its task:
$produced = @(
    'tests/Guardrails.Core.Tests/Samples/SampleVerifierTests.cs',              # task 01
    'src/Guardrails.Core/Samples/SampleVerifier.cs',                           # tasks 01 (stub) + 02 (impl)
    'src/Guardrails.Cli/Commands/SamplesCommand.cs',                           # task 03
    'src/Guardrails.Cli/CommandFactory.cs',                                    # task 03
    'src/Guardrails.Cli/PlanPreflightPhase.cs',                                # task 04
    'tests/Guardrails.Integration.Tests/Samples/SampleVerifierWiringTests.cs', # task 04
    'docs/plans/02-schemas-and-contracts.md',                                  # task 05
    '.claude/skills/guardrails-domain-knowledge/SKILL.md'                      # task 05
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

    # Line-anchored ours/theirs markers only - see the measured note in the header: two of these eight
    # paths are Markdown documents that DISCUSS conflict markers, and an unanchored scan false-fires on
    # them at every union.
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$rel contains git conflict markers at column 0 - the union did not cleanly integrate"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
