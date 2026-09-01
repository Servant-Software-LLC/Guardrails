# catches: a union that left git conflict markers in - or emptied - a file this plan produces. The
#          deterministic verdict on EVERY union's bytes, never git's no-conflict signal and never an
#          AI-merge worker's say-so. This is the run's integration-guardrail set (scope:"integration",
#          declared in the sidecar), so the harness re-runs it on the merged bytes at EVERY union point AND
#          on the final merged HEAD.
#
# UNION-SAFE = CONDITIONAL throughout (#125/#165): "IF the file is present, verify it", never "REQUIRE it
#          present". At an intermediate union only a SUBSET of tasks has integrated, so requiring presence
#          would red-halt a correct partial merge. The conflict-marker-freedom scan is what CREDITS GR2028:
#          it is ungameable, whereas a contribution-present grep is not - the conditional form can never
#          FAIL when a merge DROPPED a contribution entirely, so it certifies nothing about union soundness
#          on its own.
#
# LINE-ANCHORED marker regexes (#187): the ours and theirs tokens are matched ONLY through a (?m)^ anchor,
#          because a real conflict writes both at column 0 - so the anchor is false-positive-free. The token
#          literals appear NOWHERE else in this file, not even in the failure messages: an unanchored
#          occurrence anywhere in the script is a GR2037 ERROR, and rightly so. The messages therefore say
#          OURS and THEIRS in words. A separator-only check is deliberately OMITTED: unanchored it
#          false-fires on a banner rule or a Markdown setext underline, and this plan's stage 16 writes TWO
#          markdown files where that would red-halt a correct run.
#
# WHY THE #175 DUPLICATE-DEFINITION SUB-CHECK IS OMITTED, and it is a decision rather than an oversight:
#          that check exists for two COLLIDING SIBLINGS - branches cut from a common base that each append
#          the same new definition to different regions, which a 3-way merge keeps twice with no marker.
#          This plan has three multi-writer paths and NONE is a colliding pair, because every one of them is
#          serialized by a directed dependsOn path:
#            src/Guardrails.Core/Execution/Scheduler.cs      - tasks 05, 09, 13; 09 depends on 05 through
#                                                              06, and 13 depends on 09 explicitly, for
#                                                              exactly this reason
#            src/Guardrails.Core/Loading/PlanLoader.cs       - tasks 03 and 09; 09 depends on 03 through
#                                                              06 -> 05
#            tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs
#                                                            - tasks 02 and 14; 14 depends on 02 through
#                                                              13 -> 12 -> 10 -> 05 -> 07 -> 04
#          Every later writer's segment base therefore already contains the earlier writer's merged output,
#          so none can add a definition blind. The residual - a CS0101 arriving some other way - is caught
#          by 01-solution-builds, whose failure line names these files first.
#
# Baseline note (#478): the "if present" half of a union-safe conditional is EXPECTED to be nonzero on
#          arrival. Of the paths below, the shipped ones exist and are non-empty and marker-free today; the
#          six files this plan CREATES take the `continue` branch until their task integrates.
#          Nonzero-on-arrival is the NAMED reason this clause is exempt from the zero-baseline rule.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# Every path any task in this plan writes, derived mechanically from the sixteen writeScopes.
$paths = @(
    'tests/Guardrails.Core.Tests/Journal/ExecutedDefinitionHashTests.cs',
    'tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs',
    'src/Guardrails.Core/Model/TaskNode.cs',
    'src/Guardrails.Core/Loading/PlanLoader.cs',
    'src/Guardrails.Core/Execution/AttemptJournaler.cs',
    'src/Guardrails.Core/Execution/TaskExecutor.cs',
    'src/Guardrails.Core/Execution/Scheduler.cs',
    'src/Guardrails.Core/Execution/LivePlanEditWatch.cs',
    'tests/Guardrails.Core.Tests/ExecutedDefinitionHashAnchorTests.cs',
    'tests/Guardrails.Integration.Tests/MidRunDefinitionEditTests.cs',
    'tests/Guardrails.Core.Tests/Journal/WaveExecutedDefinitionHashTests.cs',
    'src/Guardrails.Core/Model/WaveNode.cs',
    'src/Guardrails.Core/Journal/WaveDefinitionHash.cs',
    'tests/Guardrails.Core.Tests/Execution/ExecutedDefinitionDivergenceTests.cs',
    'tests/Guardrails.Integration.Tests/DivergenceDeliveryGateTests.cs',
    'src/Guardrails.Core/Journal/JournalModel.cs',
    'src/Guardrails.Core/Journal/RunJournal.cs',
    'src/Guardrails.Core/Execution/DecisionEntry.cs',
    'src/Guardrails.Core/Execution/RunReport.cs',
    'src/Guardrails.Cli/Commands/RunCommand.cs',
    'docs/plans/02-schemas-and-contracts.md',
    '.claude/skills/guardrails-domain-knowledge/SKILL.md'
)

# ACCUMULATE (#478): one distinguishable message per problem, dumped once at the end, so ONE attempt
# learns every gap rather than one gap per attempt.
$failures = @()

foreach ($rel in $paths) {
    $full = Join-Path $ws $rel
    # UNION-SAFE gate: absent means the producing task has not integrated at THIS union. Correct, not a
    # failure - pass trivially and let a later union (or the terminal one) see it.
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }

    $content = Get-Content -Raw -LiteralPath $full
    if ([string]::IsNullOrWhiteSpace($content)) {
        $failures += "$rel is PRESENT but EMPTY - the union kept the path and dropped every byte. A merge that empties a file this plan produces is a silently lost contribution."
        continue
    }
    if ($content -match '(?m)^<<<<<<<') {
        $failures += "$rel contains an OURS conflict marker at column 0 - the union did not cleanly integrate and the merged bytes are not shippable."
        continue
    }
    if ($content -match '(?m)^>>>>>>>') {
        $failures += "$rel contains a THEIRS conflict marker at column 0 - the union did not cleanly integrate and the merged bytes are not shippable."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== union soundness: $($failures.Count) problem(s) on the merged bytes ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Resolve the merge on the named file(s). Do NOT delete a contribution to make the markers go away - the three multi-writer paths in this plan (Scheduler.cs, PlanLoader.cs, PlanEditedDuringRunTests.cs) are serialized by dependsOn precisely so this should not happen; a marker here means that ordering was violated or an AI-merge misresolved."
    exit 1
}

Write-Output "Union sound: every file this plan produces that is present at this union is non-empty and conflict-marker-free."
exit 0
