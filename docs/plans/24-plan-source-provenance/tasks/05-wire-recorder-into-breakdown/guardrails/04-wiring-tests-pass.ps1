# catches: a component built and unit-tested behind an injected seam but never reached by the
#          production path - PlanSourceRecord green in isolation while InitialBreakdownInvoker.PrepareInvocation
#          never writes state/plan-source.json, so `guardrails breakdown` records nothing and the whole
#          provenance chain is inert from the CLI (#120). This is the composition-root guardrail in its
#          strongest form: PlanSourceWiringTests drives the REAL PrepareInvocation with no manual
#          injection, asserts the artifact the wired path (and only the wired path) produces, feeds the
#          REAL gate the count read back out of that artifact, and pins the --fresh survival property.
#          Guardrail 01 is what stops that test from injecting the seam itself.
#          scope: LOCAL (no sidecar) - it asserts "the recorder IS wired", which cannot be true before
#          this task's own action has run, so it fails the #125 union-safe test and must not be tagged
#          scope:"integration" (#250).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=PlanSourceProvenance&FullyQualifiedName~PlanSourceWiringTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "PlanSourceWiringTests failing - PrepareInvocation does not write state/plan-source.json, the gate does not reject an under-recording folder through the real record, or the artifact does not survive --fresh (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the class this task owns (PlanSourceWiringTests, trait Category=PlanSourceProvenance)."
    exit 1
}
exit 0
