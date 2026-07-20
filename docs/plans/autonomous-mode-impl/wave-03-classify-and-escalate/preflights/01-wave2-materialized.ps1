# catches: wave-03 (classify-then-act + the escalation & reply channel) starting before wave-02's dial
#          config surface is materialized on the branch. The #181 POSITIVE-baseline archetype at the
#          wave boundary (SSOT §14.3): terminal-gate-of-wave-2 == preflight-of-wave-3. Positive-monotone-
#          safe (assert-PRESENT, never "not yet present" — a branch only grows). Confirms wave-03 builds
#          on the REAL AutonomyConfig / the GR2040 ViolatesCompoundConfig predicate / the --dial
#          resolution wave-02 shipped, not on possibly-absent bytes.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# 1. The autonomy config model — the dial the classifier / assessment / escalation all read.
$autonomyCfg = Join-Path $ws 'src/Guardrails.Core/Model/AutonomyConfig.cs'
if (-not (Test-Path $autonomyCfg)) {
    Write-Output "src/Guardrails.Core/Model/AutonomyConfig.cs not materialized — wave-02's autonomy config surface is missing; wave-03 has no dial to classify/assess against"
    exit 1
}
if ((Get-Content -Raw -Path $autonomyCfg) -notmatch 'EscalationThreshold') {
    Write-Output "src/Guardrails.Core/Model/AutonomyConfig.cs has no EscalationThreshold — wave-02's dial enum is not materialized"
    exit 1
}

# 2. The reusable GR2040 predicate the clamp / non-answerability build on (grep the durable symbol,
#    never a line number — wave-02 shipped it as a public static method).
$validator = Join-Path $ws 'src/Guardrails.Core/Loading/PlanValidator.cs'
if (-not (Test-Path $validator) -or ((Get-Content -Raw -Path $validator) -notmatch 'ViolatesCompoundConfig')) {
    Write-Output "src/Guardrails.Core/Loading/PlanValidator.cs has no ViolatesCompoundConfig predicate — wave-02's GR2040 compound-config check is not materialized; wave-03's clamp/answerability has nothing to reuse"
    exit 1
}

# 3. The --autonomous/--dial CLI resolution the runtime escalation is driven under.
$runCmd = Join-Path $ws 'src/Guardrails.Cli/Commands/RunCommand.cs'
$runCmdText = if (Test-Path $runCmd) { Get-Content -Raw -Path $runCmd } else { '' }
if (($runCmdText -notmatch '--dial') -or ($runCmdText -notmatch '--autonomous')) {
    Write-Output "src/Guardrails.Cli/Commands/RunCommand.cs has no --autonomous/--dial resolution — wave-02's CLI surface is not materialized"
    exit 1
}

# 4. The GR2040 diagnostic code constant present.
$codes = Join-Path $ws 'src/Guardrails.Core/Loading/DiagnosticCodes.cs'
if (-not (Test-Path $codes) -or ((Get-Content -Raw -Path $codes) -notmatch 'GR2040')) {
    Write-Output "src/Guardrails.Core/Loading/DiagnosticCodes.cs has no GR2040 — wave-02's compound-config diagnostic is not materialized"
    exit 1
}
exit 0
