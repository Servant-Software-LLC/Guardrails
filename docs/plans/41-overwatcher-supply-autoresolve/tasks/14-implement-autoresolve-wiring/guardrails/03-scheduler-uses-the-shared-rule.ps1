# catches: HALF of design section 2.1 shipping. That section says the effective needs-human threshold rule
#          is spelled TWICE today - in CriticalityJudge.EffectiveThreshold and in
#          Scheduler.EffectiveThresholdToken - and that BOTH are replaced by the shared GateThreshold rule.
#          Task 05 does the judge half and explicitly defers this one to the wiring task. Before this
#          guardrail existed, NO task asked for it and NO clause checked it: measured, every mention of
#          EffectiveThresholdToken in the plan folder was a deferral, and task 14 mentioned GateThreshold
#          zero times. So the duplication the design frames as fact 11's twin would have survived, with
#          the design's own sentence shipping false.
#
#          Why no test can carry this (the #468 report obligation). The two spellings agree on every input
#          the suite can supply - that is what makes them a DUPLICATION rather than a bug - so no
#          behavioural assertion distinguishes "one shared rule" from "two rules that happen to match".
#          The property is a structural fact about the wiring graph, which is the one case source-shape is
#          the right rung. The C7 control in the wiring proof exercises the DIAL and stays green either
#          way; it is not a substitute for this.
#
# MEASURED BASELINES on src/Guardrails.Core/Execution/Scheduler.cs, over the SAME $code this script reads
# (string literals neutralized, then comments stripped), case-sensitive to match the -cmatch operators:
#   GateThreshold\s*\.\s*Effective\s*\(          0   required-present, honest
#   \bGateThreshold\b                            0   required-present, honest
#   perGate\s*\?\?\s*cfg\.EscalationThreshold    1   DECLARED NONZERO - a REMOVAL deliverable, red on
#                                                    arrival by design. This is the strong clause:
#                                                    appending cannot satisfy it.
#   OVER-MATCH CHECK (#470) on that ban: EscalationThreshold occurs EXACTLY TWICE in this file, at the
#   gate switch and at the return - BOTH inside the method being replaced. So the ban cannot false-red
#   code that must survive. Verified by listing every occurrence, not by assuming.
#   ANCESTOR CHECK: GateThreshold is created by task 05, which task 14 dependsOn, so the type exists on
#   the tree this task runs against. It does not exist on the plan baseline, which is why the two
#   required clauses measure 0 there rather than being unmeasurable.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/Scheduler.cs" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - there is no Scheduler to inspect for the shared threshold rule."
    exit 1
}

# Neutralize string literals BEFORE stripping comments (#561): deriving $code by stripping comments
# straight from $raw lets a literal spelling '/*' forge a delimiter and blank real code. Only / and *
# INSIDE literals are replaced, so a literal's CONTENT still satisfies a required-present clause.
$raw  = Get-Content $f -Raw
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })   # raw strings
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })  # verbatim
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })  # ordinary
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')

# ACCUMULATE (#478): one distinguishable message per clause, dumped once, so ONE attempt learns every gap.
$failures = @()

# REQUIRED - the shared rule is actually CALLED here. Anchored on the call construct, not the name:
# a dotted mention without the trailing paren is satisfied by nameof(GateThreshold.Effective).
if ($code -cnotmatch 'GateThreshold\s*\.\s*Effective\s*\(') {
    $failures += "$f never CALLS GateThreshold.Effective(...). Design section 2.1 says the effective needs-human threshold rule is spelled twice today and that BOTH spellings are replaced by the shared rule; task 05 replaced the CriticalityJudge half and left this one to you. A mention is not a call - invoke it."
}

# FORBIDDEN - the private duplicate is GONE. A removal clause, so appending cannot satisfy it.
if ($code -cmatch 'perGate\s*\?\?\s*cfg\.EscalationThreshold') {
    $failures += "$f still resolves the threshold with its own private 'perGate ?? cfg.EscalationThreshold' fallback. That is the SECOND spelling design section 2.1 deletes. Leaving it beside a call to the shared rule is worse than either alone: two live spellings that agree today and drift silently, which is exactly the shape of fact 11 that this plan exists to close."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
