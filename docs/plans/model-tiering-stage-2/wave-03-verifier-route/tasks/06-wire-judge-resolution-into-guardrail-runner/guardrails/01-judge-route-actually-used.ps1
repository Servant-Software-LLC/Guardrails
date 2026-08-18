# catches: the half-wire - a change that COMPUTES the judge route and then still executes the prompt
#          on frontmatter-or-default. That compiles, passes unit tests, and is dead in production:
#          the classic #120 built-but-unwired shape one layer in.
#
# SOUND ABSENCE ONLY (#468). Each probe's FAILURE is conclusive: if the file never names the symbol,
# the wiring cannot have happened. Presence proves nothing on its own - CORRECTNESS is the sibling
# 02-conformance-judge-tests-pass guardrail's job, driving the REAL seam through the harness.
$ErrorActionPreference = 'Continue'
$file = 'src/Guardrails.Core/Execution/GuardrailRunner.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist"
    exit 1
}
$raw = Get-Content -Raw $file
# Comment-stripped: a comment naming the call is not a call (#97/#98).
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')

# The dotted CALL, not a bare name (#76) - a local of the same name, or a mention, must not satisfy it.
if ($code -cnotmatch 'TierResolver\s*\.\s*ResolveJudge\s*\(') {
    $failures += 'GuardrailRunner never CALLS TierResolver.ResolveJudge in real code - the judge is still resolved from frontmatter-or-default with no tier awareness, which is the state this task exists to change'
}

# The resolved datum must be exposed, or task 07 has nothing to carry to the journal (#474).
if ($code -cnotmatch 'JudgeResolution') {
    $failures += 'GuardrailRunner never mentions JudgeResolution in real code - even a correct resolution is useless if it stays a local: task 07 carries it to the journal and cannot invent what this task does not expose (wave 2 lost a task to exactly this severed-path shape)'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== judge wiring: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Note what this check CANNOT tell you: that the resolved block is the one the invocation actually EXECUTES on, and that EffectiveSettings(isGuardrail: true) is called on the RESOLVED judge block rather than the actor's or the frontmatter's. Get that wrong and rule 7 silently mis-profiles every bumped judge with another block's permissions and turn budget. The conformance clauses are what prove it."
    exit 1
}
exit 0
