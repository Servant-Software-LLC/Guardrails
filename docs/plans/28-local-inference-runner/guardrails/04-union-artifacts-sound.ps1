# catches: a union that left git conflict markers in - or emptied - a file this plan produces. The
#          deterministic verdict on EVERY union's bytes, never git's no-conflict signal and never an
#          AI-merge worker's say-so. This is the run's integration-guardrail set (scope:"integration",
#          declared in the sidecar), so the harness re-runs it on the merged bytes at EVERY union point
#          AND on the final merged HEAD.
#
# UNION-SAFE = CONDITIONAL throughout: "IF the file is present, verify it", never "REQUIRE it present".
#          At an intermediate union only a SUBSET of tasks has integrated, so requiring presence would
#          red-halt a correct partial merge. This is what credits GR2028 (the conflict-marker-freedom
#          check is ungameable; a contribution-present grep is not, because the conditional can never
#          FAIL when a merge DROPPED a contribution entirely).
#
# WHY THE DUPLICATE-DEFINITION SUB-CHECK IS OMITTED (#175), and it is a decision, not an oversight:
#          that check exists for two COLLIDING SIBLINGS - branches cut from a common base that each
#          append the same new definition to different regions, which a 3-way merge keeps twice with no
#          marker. Every multi-writer path in this plan is written by tasks on a STRICTLY SEQUENTIAL
#          chain (each depends transitively on the previous writer), so every later writer's segment base
#          already contains the earlier one's merged output and no two can add the same definition
#          blind. The residual - a CS0101 arriving some other way - is caught by 01-solution-builds,
#          whose failure line names these exact files first.
#
# Baseline note (#478): the "if present" half of a union-safe conditional is EXPECTED to be nonzero on
#          arrival - MEASURED below, per path, against the untouched tree.
#
# MULTI-WRITER PATHS (where a dropped or duplicated hunk would land), derived mechanically:
#   src/Guardrails.Core/Execution/ActionRunner.cs
#     written by: 01-author-tests-role-seam, 02-assign-roles-at-seven-sites, 13-implement-kind-aware-harness
#   src/Guardrails.Core/Execution/AiMergeResolver.cs
#     written by: 01-author-tests-role-seam, 02-assign-roles-at-seven-sites
#   src/Guardrails.Core/Execution/CriticalityJudge.cs
#     written by: 01-author-tests-role-seam, 02-assign-roles-at-seven-sites
#   src/Guardrails.Core/Execution/GuardrailRunner.cs
#     written by: 01-author-tests-role-seam, 02-assign-roles-at-seven-sites, 13-implement-kind-aware-harness, 23-implement-judge-spend
#   src/Guardrails.Core/Execution/NeedsHumanTriage.cs
#     written by: 01-author-tests-role-seam, 02-assign-roles-at-seven-sites, 04-implement-shared-json-extractor
#   src/Guardrails.Core/Execution/Overwatch.cs
#     written by: 01-author-tests-role-seam, 02-assign-roles-at-seven-sites
#   src/Guardrails.Core/Execution/WaveBreakdownInvoker.cs
#     written by: 01-author-tests-role-seam, 02-assign-roles-at-seven-sites
#   src/Guardrails.Core/Loading/DiagnosticCodes.cs
#     written by: 15-implement-block-diagnostics, 17-implement-reachability-gate
#   src/Guardrails.Core/Loading/PlanValidator.cs
#     written by: 15-implement-block-diagnostics, 17-implement-reachability-gate
#   src/Guardrails.Core/Model/PromptRunnerConfig.cs
#     written by: 08-author-tests-openai-runner, 11-implement-runner-verdict-roles, 19-implement-endpoint-preflight
#   src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs
#     written by: 08-author-tests-openai-runner, 09-implement-runner-transport, 10-implement-runner-tool-loop, 11-implement-runner-verdict-roles
#   src/Guardrails.Core/Prompts/PromptJsonExtractor.cs
#     written by: 03-author-tests-json-extractor, 04-implement-shared-json-extractor
#   src/Guardrails.Core/Prompts/PromptToolContainment.cs
#     written by: 06-author-tests-tool-containment, 07-implement-tool-containment
#
# Env/cwd: the attempt-decoupled re-verifier sets cwd to the union/integration worktree and does NOT set
# $GUARDRAILS_WORKSPACE. Resolve robustly: prefer $GUARDRAILS_WORKSPACE, else use cwd.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# Every file any task in this plan writes - the union of all 26 writeScopes, deduped, and nothing
# else. DERIVED MECHANICALLY from the task.json files (61 declarations, 41 distinct paths), never
# by eye: a path no task here writes can never fail (the conditional gates it away) and would misdescribe
# the plan's surface to the next reader.
$produced = @(
    '.claude/skills/guardrails-domain-knowledge/SKILL.md',
    '.claude/skills/plan-breakdown/references/schemas.md',
    'docs/plans/02-schemas-and-contracts.md',
    'docs/plans/17-model-tiering.md',
    'src/Guardrails.Cli/Commands/ProvidersCommand.cs',
    'src/Guardrails.Cli/PlanPreflightPhase.cs',
    'src/Guardrails.Core/Execution/ActionRunner.cs',
    'src/Guardrails.Core/Execution/AiMergeResolver.cs',
    'src/Guardrails.Core/Execution/CriticalityJudge.cs',
    'src/Guardrails.Core/Execution/GuardrailRunner.cs',
    'src/Guardrails.Core/Execution/NeedsHumanTriage.cs',
    'src/Guardrails.Core/Execution/Overwatch.cs',
    'src/Guardrails.Core/Execution/OverwatchProposal.cs',
    'src/Guardrails.Core/Execution/WaveBreakdownInvoker.cs',
    'src/Guardrails.Core/Journal/JournalTierSpend.cs',
    'src/Guardrails.Core/Loading/DiagnosticCodes.cs',
    'src/Guardrails.Core/Loading/PlanLoader.cs',
    'src/Guardrails.Core/Loading/PlanValidator.cs',
    'src/Guardrails.Core/Loading/RawManifests.cs',
    'src/Guardrails.Core/Model/PromptRunnerConfig.cs',
    'src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs',
    'src/Guardrails.Core/Prompts/PromptComposer.cs',
    'src/Guardrails.Core/Prompts/PromptFailureKind.cs',
    'src/Guardrails.Core/Prompts/PromptInvocation.cs',
    'src/Guardrails.Core/Prompts/PromptJsonExtractor.cs',
    'src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs',
    'src/Guardrails.Core/Prompts/PromptToolContainment.cs',
    'tests/Guardrails.Core.Tests/Journal/JudgeSpendRecordingTests.cs',
    'tests/Guardrails.Core.Tests/Loading/ActionReachabilityGateTests.cs',
    'tests/Guardrails.Core.Tests/Loading/OpenAiCompatDiagnosticsTests.cs',
    'tests/Guardrails.Core.Tests/Prompts/PromptJsonExtractorTests.cs',
    'tests/Guardrails.Core.Tests/Prompts/PromptRoleSeamTests.cs',
    'tests/Guardrails.Core.Tests/Prompts/PromptToolContainmentTests.cs',
    'tests/Guardrails.Integration.Tests/OpenAiCompat/FakeOpenAiServer.cs',
    'tests/Guardrails.Integration.Tests/OpenAiCompat/FakeOpenAiServerTests.cs',
    'tests/Guardrails.Integration.Tests/OpenAiCompat/KindAwareHarnessTests.cs',
    'tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatPreflightTests.cs',
    'tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatToolLoopTests.cs',
    'tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatTransportTests.cs',
    'tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatVerdictTests.cs',
    'tests/Guardrails.Integration.Tests/OpenAiCompat/ProvidersCheckTests.cs'
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

    # Line-anchored ours/theirs markers ONLY (a real conflict writes both at column 0), and NO bare
    # '=======' clause: unanchored it false-fires on a '====' banner, a Markdown setext underline or an
    # ASCII table rule (#187). THE ANCHOR IS LOAD-BEARING HERE, not folklore - four of the paths above are
    # large Markdown documents (the SSOT, the tiering DoR, two skill files) that DISCUSS conflict markers
    # in prose, so an unanchored scan would fire on them at EVERY union and red-halt every run of this
    # plan, forever, on prose.
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$rel contains git conflict markers - the union did not cleanly integrate"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
