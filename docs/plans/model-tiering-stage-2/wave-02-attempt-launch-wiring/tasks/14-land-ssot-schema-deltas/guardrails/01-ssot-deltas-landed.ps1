# catches: the SSOT edit being skipped, or made so thinly that Invariant 4 is satisfied in name only.
#          This task exists because the plan's TERMINAL gate asserts this file mentions tierSource,
#          and it did not: no wave-2 task owned the file until this one. Without it the whole plan
#          fails at the terminal gate, after every task has run, where there is no retry (#474).
#
# TOKEN CHOICE IS DELIBERATE. Every token below was MEASURED at ZERO occurrences in this 431 KB file
# before the wave ran, so none can be pre-satisfied by existing prose:
#     tierSource 0 | inputTokens 0 | outputTokens 0 | TierOrigin 0
# Tokens that were ALREADY present are NOT used, precisely because they prove nothing here:
#     no-route 1 | 9.6 13 | judge 23
# That is the same trap the terminal gate's own comment records for its `no-route` probe.
$ErrorActionPreference = 'Continue'
$file = 'docs/plans/02-schemas-and-contracts.md'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the SSOT is the one file this task owns"
    exit 1
}
$content = Get-Content -Raw $file

# --- the net-new schema tokens ------------------------------------------------------------------
foreach ($probe in @(
    @{ Token = 'tierSource';   What = 'the per-attempt provenance field naming WHICH source supplied the rung (DoR 12.4). This is the exact token the PLAN TERMINAL GATE probes for - if it is missing here, the whole plan fails at the terminal gate where there is no retry' },
    @{ Token = 'TierOrigin';   What = 'the ActionDefinition enum (None/Task/PlanDefault) recording which source supplied action.tier - the input tierSource is derived from, and the reason the loader no longer collapses action.tier into tiering.defaultTier' },
    @{ Token = 'inputTokens';  What = 'the attempt record''s optional usage block (DoR 12.4)' },
    @{ Token = 'outputTokens'; What = 'the attempt record''s optional usage block (DoR 12.4)' })) {
    if ($content -cnotmatch [regex]::Escape($probe.Token)) {
        $failures += "the SSOT never mentions '$($probe.Token)' - $($probe.What)"
    }
}

# --- each tierSource value must be documented, or the enum is named without being defined --------
foreach ($v in @('plan-default', 'override')) {
    if ($content -cnotmatch [regex]::Escape($v)) {
        $failures += "the SSOT never mentions the tierSource value '$v' - DoR D31 gives each v1 value exactly ONE producer, and a field documented without its values tells a reader nothing about what to expect in a journal"
    }
}

# --- the 6.3 answer must be DOCUMENTED, not left as a regex nobody can cite -----------------------
# Keyed on the concrete signal families task 04 reported in its state fragment. A summary sentence
# ("connection failures are transient") passes no clause here, which is the point: the answer is only
# useful if a reader can tell WHICH shapes are covered.
if ($content -cnotmatch '(?i)getaddrinfo|ENOTFOUND|EAI_AGAIN|resolve host') {
    $failures += 'nothing documents the DNS family of connection-level signals - task 04 published its DoR 6.3 answer to state (alreadyCovered / added / newEnumMember) precisely so this task could document it; naming the concrete families is what makes the answer citable rather than a paraphrase'
}
if ($content -cnotmatch '(?i)PromptFailureKind') {
    $failures += 'the SSOT never mentions PromptFailureKind - the 6.3 answer is that a connection-level failure is the SHIPPED Transient value riding the existing #115 pause, with NO new enum member; that is a statement about PromptFailureKind and cannot be made without naming it'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== SSOT schema deltas: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Invariant 4: a schema change lands in the SSOT in the SAME change as the code that motivates it. Every other task in this wave has already shipped its code; this task is what stops those changes from being claims that live outside the schema."
    exit 1
}
exit 0
