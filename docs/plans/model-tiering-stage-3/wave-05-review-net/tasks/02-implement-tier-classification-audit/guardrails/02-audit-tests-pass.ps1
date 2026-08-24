# catches: an implementation that satisfies its own reading of the rule rather than the eleven tests
#          01-author-tests-tier-classification-audit authored - and, specifically, the two failure modes
#          this whole stage is about.
#
#          (1) An audit that reads ActionDefinition.Tier instead of TierOrigin. It looks right, compiles,
#          and finds NOTHING on any plan carrying a tiering.defaultTier - because the loader resolves that
#          default into every untagged task. PlanWideDefaultTier_DoesNotDischargeTheFinding_BecauseItIsResolvedAtLoad
#          is the only thing standing between this wave and that permanent silence.
#
#          (2) A graceful skip that swallows everything. The two Group B silence tests are EXCLUDED from
#          the red census on the task upstream - a silence assertion cannot be red before the feature
#          exists - so THIS is the only guardrail in the wave where their green means anything. They are in
#          this filter's set, and they are the reason it runs the whole class rather than a subset.
#
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179, dotnet.md 4.2).
#
# SCOPE (#455): ONE class, in one project - `TierClassificationAuditTests`, which is a substring of no
# other test class anywhere under tests/ (verified 2026-08-24: `TierClassification` occurs nowhere in the
# tree on this wave's entry tree). It selects exactly this task pair's own tests, and no sibling's.
#
# LOCAL - no `scope` key. This asserts "the audit works", which cannot be true before this task's own
# action has run, so it fails the #125 union-safe test and must never be tagged integration (#250).
#
# FORWARD polarity: the exit-code check runs FIRST, so a test host that never started is reported as a run
# failure rather than mis-diagnosed as a bad filter (#455).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the summary line the zero-match guard reads is LOCALIZED (#455)

$filter = 'FullyQualifiedName~TierClassificationAuditTests'

# NO -v q on a TEST command (#179) - it suppresses the entire Error Message / Expected / Actual block and
# leaves the re-emit below with nothing but test NAMES to re-emit.
$out = dotnet test tests/Guardrails.Core.Tests --nologo --filter $filter 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== TierClassificationAuditTests failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output ""
    Write-Output "The audit does not satisfy the authored tests. Fix tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs - the tests and the fixtures are outside this task's writeScope, and an edit to either fails the task immediately. If a PlanWideDefaultTier... failure is in the list, read ActionDefinition.TierOrigin's docstring before changing anything else."
    exit 1
}

# ZERO-MATCH GUARD (#455): a --filter that matches nothing exits 0 and certifies nothing. Keyed on the
# EXECUTED count (Passed + Failed), never Total, which counts [Skip]ped tests - so a fully-skipped class
# cannot pass this. Never on the "no tests matched" STRING, which is verbosity-dependent (#248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output ""
    Write-Output "the filter $filter executed ZERO tests - TierClassificationAuditTests is missing, empty, or named differently, so this guardrail certified nothing. The class is authored by 01-author-tests-tier-classification-audit; if it is absent from your tree that is a delivery problem, not a filter to widen."
    exit 1
}
exit 0
