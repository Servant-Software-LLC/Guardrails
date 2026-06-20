# catches: a vacuous GuardrailScopeTests that compiles-fails (passing tests-fail-on-current-code
#          trivially on, say, only the GuardrailDefinition.Scope reference) while never encoding the
#          load-bearing §4.3/B-3 scenarios - the integration-vs-local scope FILTER on the union
#          re-verify, and the B-3 split where a COLLIDING SIBLING's local guardrail re-runs REGARDLESS
#          of touched-files (a touched-files local-skip wrongly applied to a colliding sibling must make
#          the test FAIL). Assert both the scope/integration term AND the sibling term are present.
#          Scoped to the one file this task owns (grep-scope rule - no project-tree greps).
$file = "tests/Guardrails.Core.Tests/GuardrailScopeTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the guardrail-scope test file was not authored"
    exit 1
}
$text = Get-Content $file -Raw
$needles = @{
    '(?i)integration'   = 'no "integration" term - the scope: "integration" classification / integration-guardrail-set filter scenario is missing'
    '(?i)sibling'       = 'no "sibling" term - the B-3 colliding-sibling local-guardrail-re-runs-regardless scenario is missing'
}
foreach ($n in $needles.Keys) {
    if ($text -notmatch $n) {
        Write-Output "GuardrailScopeTests: $($needles[$n])"
        exit 1
    }
}
exit 0
