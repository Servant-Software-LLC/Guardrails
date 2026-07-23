# catches: the auto-tier gate built but NOT reached from production, or the block-presence signal never
#          wired — the #120/#158 recurring false-green. Two scoped structural checks (a cheap fast-fail
#          complement to the drive-the-behavior test in guardrail 02): (1) Overwatch.cs's Decide gate reads
#          the autonomyBlockPresent field; (2) SchedulerFactory.cs's new Overwatch(...) construction passes
#          an Autonomy-derived value (so the gate engages on the BLOCK PRESENCE, not autonomyPolicy:auto
#          alone — the anti-Option-(c) wiring). Each check is scoped to the ONE file that owns it.
$overwatch = "src/Guardrails.Core/Execution/Overwatch.cs"
$factory   = "src/Guardrails.Core/Execution/SchedulerFactory.cs"

if (-not (Test-Path $overwatch)) { Write-Output "$overwatch does not exist"; exit 1 }
if (-not (Test-Path $factory))   { Write-Output "$factory does not exist";   exit 1 }

$ow = Get-Content -Raw -Path $overwatch
if ($ow -notmatch 'autonomyBlockPresent') {
    Write-Output "Overwatch.cs never references autonomyBlockPresent — the auto-tier gate does not read the block-presence signal, so 'auto' still degrades to prompt unconditionally (the gate is not wired)."
    exit 1
}

# The SchedulerFactory must pass an Autonomy-derived value INTO the Overwatch construction (proximity,
# multiline-dotall, both orders) — proving the gate engages on the autonomy-block presence, NOT auto alone.
$fc = Get-Content -Raw -Path $factory
if ($fc -notmatch 'new\s+Overwatch\([\s\S]{0,600}Autonomy' -and $fc -notmatch 'Autonomy[\s\S]{0,600}new\s+Overwatch\(') {
    Write-Output "SchedulerFactory.cs does not pass a plan.Config.Autonomy-derived value into new Overwatch(...) — the auto-tier would key on autonomyPolicy:auto alone (the rejected Option (c)); it must gate on the PRESENCE of the autonomy block (§9 Phase 4)."
    exit 1
}
exit 0
