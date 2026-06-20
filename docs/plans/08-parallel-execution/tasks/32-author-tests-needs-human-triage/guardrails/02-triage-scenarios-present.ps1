# catches: a vacuous NeedsHumanTriageTests that compiles-fails (passing tests-fail-on-current-code
#          trivially on, say, only a NeedsHumanTriage reference) while never encoding the load-bearing
#          §9 scenarios. Each §9 behavior is pinned to a NAMED test method (the 2nd review proved a bare
#          keyword grep is satisfied by a comment / a single happy-path test). The five load-bearing
#          properties of §9: (a) triage runs ONLY on attempt-exhaustion (not on agent-emitted
#          {needsHuman}, not mid-retry); (b) it writes feedback.md with a tool-vs-local diagnosis;
#          (c) the needs-human message references the feedback.md path; (d) triage is ADVISORY (a
#          thrown/error triage does NOT change the verdict and does NOT block - the exit code is never a
#          verdict); (e) auto-file is OFF by default (drafts only). Scoped to the one file this task
#          owns (grep-scope rule - no project-tree greps).
$file = "tests/Guardrails.Integration.Tests/NeedsHumanTriageTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the needs-human-triage test file was not authored"
    exit 1
}
$text = Get-Content $file -Raw
if ($text -notmatch 'NeedsHumanTriage') {
    Write-Output "NeedsHumanTriageTests does not reference NeedsHumanTriage - the triage step under test is missing"
    exit 1
}
# Each §9 behavior is pinned to an EXACT named test method. A bare keyword (e.g. 'advisory',
# 'feedback.md') is satisfiable by a comment or a single happy-path test - the named methods force the
# distinct scenarios to actually exist as separate, structurally-present tests.
$methods = @{
    'Triage_RunsOnAttemptExhaustion'                            = "the attempt-exhaustion trigger (triage runs ONCE when the task reaches needs-human via exhausted retries)"
    'Triage_SkippedOnAgentEmittedNeedsHuman'                    = "the agent-emitted-needsHuman skip (a clean {needsHuman} short-circuit is already a human ask - triage must NOT run)"
    'Triage_NotRunMidRetry'                                     = "the mid-retry negative (triage must NOT run between attempts while the task can still retry)"
    'Triage_WritesFeedbackMdWithToolVsLocalDiagnosis'          = "the feedback.md + tool-vs-local diagnosis (writes logs/<runId>/<task-id>/feedback.md classifying tool-problem vs local-problem)"
    'Triage_NeedsHumanMessageReferencesFeedbackPath'           = "the needs-human-message-points-to-feedback.md pin"
    'Triage_IsAdvisory_ThrownTriageDoesNotChangeVerdictOrBlock' = "the ADVISORY pin (a thrown/error triage does NOT change the needs-human verdict and does NOT block; PromptResult.IsError/exit code is never a verdict)"
    'Triage_AutoFileOffByDefault_DraftsOnly'                    = "the auto-file-OFF-by-default pin (triageAutoFile default off; drafts the GH issue into feedback.md, files nothing)"
}
$missing = @()
foreach ($m in $methods.Keys) {
    if ($text -notmatch [regex]::Escape($m)) {
        $missing += "$m - $($methods[$m])"
    }
}
if ($missing.Count -gt 0) {
    Write-Output ("NeedsHumanTriageTests is missing required named test method(s) - each §9 behavior must be a separately-named test, not a keyword:`n  - " + ($missing -join "`n  - "))
    exit 1
}
exit 0
