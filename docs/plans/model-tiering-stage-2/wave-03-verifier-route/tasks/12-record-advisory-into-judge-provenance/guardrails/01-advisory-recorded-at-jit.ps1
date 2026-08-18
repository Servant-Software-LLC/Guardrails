# catches: VerifierAdvisory shipping as a tested unit that nothing calls - the #120 built-but-unwired
#          shape, and the one this wave is most exposed to because tasks 09/10 prove the class in
#          isolation and would stay green forever with no caller.
#
# TOKENS MEASURED against the integration tree at authoring: VerifierAdvisory = 0 occurrences in
# GuardrailRunner.cs, Advisory = 0. Both clauses therefore start RED and can only go green on work.
#
# SOUND ABSENCE ONLY (#468): a failure is conclusive, a pass is not proof the value is right. The
# conformance clauses task 06 authored are what prove the advisory actually reaches run.json.
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

if ($code -cnotmatch 'VerifierAdvisory') {
    $failures += 'GuardrailRunner never names VerifierAdvisory in real code - tasks 09/10 built and tested it, and with no caller it is a green unit that ships dead. The JIT boundary is this task: compute the finding where the judge is resolved.'
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
