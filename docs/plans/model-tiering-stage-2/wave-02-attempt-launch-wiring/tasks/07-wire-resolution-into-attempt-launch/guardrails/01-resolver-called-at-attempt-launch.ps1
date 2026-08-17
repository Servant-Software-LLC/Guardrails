# catches: the #120 composition-root failure in its purest form - the conformance clauses going
#          green while the production executor still never constructs a route through the resolver.
#          This is the CHEAP structural half of the proof; the sibling 02 guardrail is the strong
#          half (it drives the real path). Neither is sufficient alone: a grep cannot tell "calls the
#          resolver" from "resolves correctly", and a passing test set cannot tell you WHICH of the
#          two code paths produced the value when the old one is still there beside it.
#
#          It also guards the drift this task exists to remove: TWO independent model derivations
#          (provenance via ResolveModelForDisplay, argv via ApplyModelOverride(task.Action.Model))
#          that agree only by construction. After this task there must be ONE resolution feeding both.
$ErrorActionPreference = 'Continue'
$failures = @()

$executor = 'src/Guardrails.Core/Execution/TaskExecutor.cs'
$runner   = 'src/Guardrails.Core/Execution/ActionRunner.cs'

foreach ($f in @($executor, $runner)) {
    if (-not (Test-Path $f)) { $failures += "$f does not exist"; }
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

$exec = Get-Content -Raw $executor
$act  = Get-Content -Raw $runner

# POSITIVE: the production attempt path calls the wave-1 resolver.
if ($exec -notmatch 'TierResolver\.Resolve\s*\(') {
    $failures += 'TaskExecutor never calls TierResolver.Resolve( - the resolver is still dead code reachable only from wave 1''s unit tests, which is exactly the "built, green, and inert" failure this wave exists to close'
}

# NEGATIVE (#176/#221): the OLD two-level fallback must no longer be the executor's route source.
# TierResolver.Resolve implements the legacy branch itself (D30), so a surviving call here means the
# executor kept a second, weaker derivation beside the new one.
if ($exec -match 'ResolveModelForDisplay') {
    $failures += 'TaskExecutor still calls ResolveModelForDisplay - the two-level fallback it names is now the legacy BRANCH INSIDE TierResolver.Resolve (D30). Keeping the old call is the drift this task removes: provenance and argv would again be derived twice and agree only by construction'
}

# NEGATIVE: the invocation must be built from the RESOLVED route, not from the raw task override.
if ($act -match 'ApplyModelOverride\s*\([^)]*task\.Action\.Model') {
    $failures += 'ActionRunner still passes task.Action.Model straight to ApplyModelOverride - the model reaching the CLI must come from the RESOLVED route (a pin is honoured by TierResolver.Resolve''s own precedence, so a pinned task still gets its model this way). Otherwise provenance records a route the runner never received'
}

# NEGATIVE: tierSource must be READ from TierOrigin, never reconstructed by comparing the tier to the
# plan default - the shipped PlanValidator workaround, wrong exactly when a task tier equals the
# default (wave 1 pins that case, so this would red an already-merged test).
if ($exec -match '(?s)Tiering\??\.DefaultTier[^;]{0,120}(==|Equals)') {
    $failures += 'TaskExecutor appears to compare a tier against tiering.defaultTier - do NOT reconstruct the tier ORIGIN that way. It is PlanValidator''s shipped workaround and it is wrong exactly when a task''s own tier equals the default. Read ActionDefinition.TierOrigin, which wave 1 restored for this purpose'
}

# POSITIVE: the provenance write actually consumes the resolution's tier facts.
foreach ($member in @('TierSource', 'TierOrigin')) {
    if ($exec -notmatch "\b$member\b") {
        $failures += "TaskExecutor never references $member - the D31 mapping from the loader-restored origin to the journal's tierSource is not implemented"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== attempt-launch wiring: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "the resolver is not wired at the production composition root the way DoR 6.1 specifies. A conformance clause can pass for the wrong reason while the old derivation survives beside the new one - fix the findings above."
    exit 1
}
exit 0
