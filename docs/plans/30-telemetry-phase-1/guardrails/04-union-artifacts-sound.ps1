# catches: a union that left git conflict markers in - or emptied - a file this plan produces. The
#          deterministic verdict on EVERY union's bytes, never git's no-conflict signal and never an
#          AI-merge worker's say-so. Declared scope:"integration" in the sidecar, so the harness re-runs
#          it on the merged bytes at EVERY union point AND on the final merged HEAD. This is the check
#          that CREDITS GR2028 for this plan: it is ungameable, whereas a contribution-present grep can
#          never FAIL when a merge DROPPED a contribution entirely.
#
# UNION-SAFE = CONDITIONAL throughout (#125/#165): "IF the file is present, verify it", never "REQUIRE it
#          present". At an intermediate union only a subset of the twenty-seven tasks has integrated, so
#          requiring presence would red-halt a correct partial merge. Seventeen of the paths below are files
#          this plan CREATES; they take the `continue` branch until their task integrates.
#
# LINE-ANCHORED marker regexes (#187): the ours and theirs tokens are matched ONLY through a (?m)^ anchor,
#          because a real conflict writes both at column 0 - so the anchor is false-positive-free. The
#          token literals appear NOWHERE else in this file, not even in the failure messages: an
#          unanchored occurrence anywhere in the script is a GR2037 ERROR. The messages therefore say
#          OURS and THEIRS in words. A separator-only ('=======') check is deliberately OMITTED:
#          unanchored it false-fires on a banner rule or a Markdown setext underline, and task 25 writes
#          TWO markdown files where that would red-halt a correct run.
#
# WHY THE #175 DUPLICATE-DEFINITION SUB-CHECK IS OMITTED, and it is a decision rather than an oversight:
#          that check exists for two COLLIDING SIBLINGS - branches cut from a common base that each
#          append the same new definition to different regions, which a 3-way merge keeps twice with no
#          textual marker. This plan has NINE multi-writer paths and NONE is a colliding pair, because
#          every one of them is serialized by a directed dependsOn path. That was verified MECHANICALLY
#          over the emitted folder - a reachability sweep across all twenty-six dependsOn arrays,
#          asserting that for every path written by two or more tasks, one of each pair reaches the
#          other - not read off the numbering by eye:
#            src/Guardrails.Cli/Commands/TelemetryCommand.cs             - tasks 22, 24
#            src/Guardrails.Core/Execution/ActionRunner.cs               - tasks 04, 10, 12
#            src/Guardrails.Core/Execution/AttemptJournaler.cs           - tasks 06, 12, 12a, 16
#            src/Guardrails.Core/Execution/GuardrailRunner.cs            - tasks 04, 12a
#            src/Guardrails.Core/Execution/TaskExecutor.cs               - tasks 10, 12, 12a, 14
#            src/Guardrails.Core/Journal/RunEnvironmentProbe.cs          - tasks 17, 18
#            src/Guardrails.Core/Journal/RunJournal.cs                   - tasks 06, 16, 18
#            src/Guardrails.Core/Telemetry/TaskFingerprintBucket.cs      - tasks 01, 02
#            src/Guardrails.Core/Telemetry/TelemetryAttributionCensus.cs - tasks 23, 24
#          Every later writer's segment base therefore already contains the earlier writer's merged
#          output, so none can add a definition blind. The residual - a CS0101 arriving some other way -
#          is caught by 01-solution-builds, whose failure line names these files first.
#
# Baseline note (#478): the "if present" half of a union-safe conditional is EXPECTED to be nonzero on
#          arrival. Of the 36 paths below, the 18 shipped ones exist and are non-empty and marker-free
#          today; the 18 this plan CREATES take the `continue` branch until their task integrates.
#          Nonzero-on-arrival is the NAMED reason this clause is exempt from the zero-baseline rule.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# Every path any task in this plan writes, derived MECHANICALLY from the twenty-six writeScopes by
# reading each task.json - not transcribed by hand. 36 distinct paths: 18 already exist on master and
# 18 are created by this plan (those take the `continue` branch until their task integrates).
$paths = @(
    'tests/Guardrails.Core.Tests/Telemetry/TaskFingerprintBucketTests.cs',
    'src/Guardrails.Core/Telemetry/TaskFingerprintBucket.cs',
    'src/Guardrails.Core/Journal/JournalModel.cs',
    'tests/Guardrails.Core.Tests/Journal/Phase1JournalShapeTests.cs',
    'tests/Guardrails.Core.Tests/Journal/TaskBucketJournalTests.cs',
    'src/Guardrails.Core/Journal/RunJournal.cs',
    'src/Guardrails.Core/Execution/AttemptJournaler.cs',
    'tests/Guardrails.Core.Tests/Prompts/ModelDigestCaptureTests.cs',
    'src/Guardrails.Core/Prompts/PromptInvocation.cs',
    'src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs',
    'tests/Guardrails.Core.Tests/Execution/ModelDigestProvenanceTests.cs',
    'src/Guardrails.Core/Execution/ActionRunner.cs',
    'src/Guardrails.Core/Execution/TaskExecutor.cs',
    'tests/Guardrails.Core.Tests/Execution/AttemptEnvelopeTests.cs',
    'src/Guardrails.Core/Execution/GuardrailRunner.cs',
    'tests/Guardrails.Core.Tests/Execution/RouteWarmthTests.cs',
    'tests/Guardrails.Core.Tests/Execution/WorktreeSettlePhase1Tests.cs',
    'src/Guardrails.Core/Execution/RunReport.cs',
    'tests/Guardrails.Core.Tests/Execution/TransportShapeTests.cs',
    'src/Guardrails.Core/Execution/ISchedulerJournal.cs',
    'src/Guardrails.Core/Execution/Scheduler.cs',
    'tests/Guardrails.Core.Tests/Journal/RunEnvironmentTests.cs',
    'src/Guardrails.Core/Journal/RunEnvironmentProbe.cs',
    'src/Guardrails.Cli/Commands/RunCommand.cs',
    'tests/Guardrails.Integration.Tests/Journal/RunEnvironmentJournalTests.cs',
    'tests/Guardrails.Core.Tests/Telemetry/CorpusRowShapeTests.cs',
    'tests/Guardrails.Core.Tests/Telemetry/Phase1TelemetryRowTests.cs',
    'src/Guardrails.Core/Telemetry/TelemetryRow.cs',
    'src/Guardrails.Core/Telemetry/TelemetryIngest.cs',
    'tests/Guardrails.Integration.Tests/Commands/TelemetryReportPhase1Tests.cs',
    'src/Guardrails.Cli/Commands/TelemetryCommand.cs',
    'tests/Guardrails.Core.Tests/Telemetry/AttributionCensusTests.cs',
    'tests/Guardrails.Integration.Tests/Commands/TelemetryCensusCommandTests.cs',
    'src/Guardrails.Core/Telemetry/TelemetryAttributionCensus.cs',
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
    Write-Output "Resolve the merge on the named file(s). Do NOT delete a contribution to make the markers go away - the nine multi-writer paths in this plan are serialized by dependsOn precisely so this should not happen; a marker here means that ordering was violated or an AI-merge misresolved."
    exit 1
}

Write-Output "Union sound: every file this plan produces that is present at this union is non-empty and conflict-marker-free."
exit 0
