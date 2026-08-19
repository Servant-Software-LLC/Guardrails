# catches: VerifierAdvisory shipping as a tested unit that nothing calls - the #120 built-but-unwired
#          shape, and the one this wave is most exposed to because tasks 09/10 prove the class in
#          isolation and would stay green forever with no caller.
#
# TOKENS MEASURED against the integration tree at authoring: VerifierAdvisory = 0 occurrences in
# GuardrailRunner.cs, Advisory = 0. Both clauses therefore start RED and can only go green on work.
#
# SOUND ABSENCE ONLY (#468): a failure here is conclusive, a pass is not proof the value is right.
# The behavioural proof is the SIBLING 02-advisory-conformance-passes guardrail, which drives the real
# seam and reads run.json. An earlier version of this comment credited "the conformance clauses task
# 06 authored" - task 06 pinned five clauses at the time and NONE concerned the advisory, so this
# guardrail was accepting a weak structural check on the strength of a proof nobody had written.
$ErrorActionPreference = 'Continue'
$file = 'src/Guardrails.Core/Execution/GuardrailRunner.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist"
    exit 1
}
$raw  = Get-Content -Raw $file
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')

# A DOTTED CALL, not a bare token (#76): `nameof(VerifierAdvisory)` in a dead field satisfied the
# token form while computing nothing (measured, exit 0).
if ($code -cnotmatch 'VerifierAdvisory\s*\.') {
    $failures += 'GuardrailRunner never names VerifierAdvisory in real code - tasks 09/10 built and tested it, and with no caller it is a green unit that ships dead. The JIT boundary is this task: compute the finding where the judge is resolved.'
}
# The assignment must land INSIDE the AttemptJudge that GuardrailRunner returns - task 07 pins that
# type as the exposed one. Keyed on the two together within a REGION - deliberately NOT one statement:
# `var j = new AttemptJudge {...}; j = j with { Advisory = f };` is a correct hoisted form, and a
# statement-bounded window would false-RED it (that exact mistake cost task 05 two attempts). The
# proximity requirement still holds, because the free-floating
# form was satisfied by  _ = judge with { Advisory = ... }  - a with-expression whose result is
# DISCARDED (records are immutable), i.e. the #475 shape reproduced inside the task written to
# prevent it. Measured: that bypass exited 0 against the previous form.
if ($code -cnotmatch '(?s)AttemptJudge[\s\S]{0,1200}?Advisory\s*=') {
    $failures += 'no Advisory assignment inside an AttemptJudge construction - task 07 exposes the resolved judge as Guardrails.Core.Journal.AttemptJudge, and the advisory must be set ON THAT OBJECT so it rides the carry task 08 already built. A with-expression whose result is discarded, or an Advisory set on some other object, changes nothing that reaches run.json.'
}
if ($code -cnotmatch '\bAdvisory\s*=') {
    $failures += 'nothing ASSIGNS an Advisory member in real code - computing the finding and then not putting it on the judge datum leaves 6.5 half-implemented, and the schema field tasks 03/04 added stays null forever (the #475 shape this wave exists to avoid repeating)'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== verifier advisory at the JIT boundary: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The carry is ALREADY BUILT: task 08 folds the judge datum onto AttemptProvenance, which reaches both attempt-record paths. You are filling a field on an object that already makes the trip - do not build a second carry, and do not touch TaskExecutor or the journal model."
    exit 1
}
exit 0
