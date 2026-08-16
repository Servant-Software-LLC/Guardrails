# catches: a brownfield plan building on a RED base - the EXISTING tests in Guardrails.Core.Tests are
#          already failing on the starting code. NOTE the honest scope limit: this covers the area
#          WAVE 1 modifies. Wave 2 rewires TaskExecutor on the production attempt path, which is what
#          Guardrails.Integration.Tests exercises - the wave-2 JIT breakdown must add its own area
#          baseline preflight for that project (see wave-02's brief).
#          Asserting them green BEFORE the DAG means a later work task's tests-pass failure is
#          attributable to THAT task rather than to pre-existing breakage, and a new test's red is
#          unambiguous (#181). Re-emits the failure DETAIL at the END so a red baseline's WHY reaches
#          the halt feedback, not just `[FAIL] <name>` (#179, dotnet.md 4.2).
# Scope: the EXISTING Core tests only, EXCLUDING the Category this plan is about to author.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # FIRST line - the run summary this guard reads is LOCALIZED

# The plan-wide trait stands ALONE here, and ONLY here (dotnet.md 4.3 / #455): "everything except the
# tests this plan is about to write". Every TASK-level filter must instead name that pair's own class.
$filter = 'Category!=TierResolution'
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

# EXIT CODE FIRST, guard second: a test host that never ran exits NON-zero with no summary at all,
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
    Write-Output "the existing tests in tests/Guardrails.Core.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181)"
    exit 1
}

# ZERO-MATCH GUARD: exit 0 alone does NOT mean the area is green - a --filter that matches nothing,
# or is malformed, also exits 0 and would certify "baseline green" over a run that executed NOTHING.
# Key on the EXECUTED count (Passed+Failed; "Total:" would also count [Skip]ped tests), never on the
# "No test matches ..." string (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}
exit 0
