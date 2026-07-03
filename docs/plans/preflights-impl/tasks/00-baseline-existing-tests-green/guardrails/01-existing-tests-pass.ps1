# catches: a brownfield plan building on a RED base - the existing tests in Guardrails.Core.Tests /
#          Guardrails.Integration.Tests are already failing on the starting code. Asserting them green
#          at the DAG root means a later work task's tests-pass failure is attributable to THAT task,
#          not pre-existing breakage, and every inserted author-tests task's "red" is unambiguous (#181).
#          Re-emits the failure DETAIL at the END so a red baseline's WHY reaches the harness retry-feedback
#          tail (last ~60 lines), not just `[FAIL] <name>` (#179, dotnet.md §4.2 / §21).
# Filtered to the PRE-EXISTING tests (Category!=Preflights). New tests this plan authors are tagged
# [Trait("Category","Preflights")], so this baseline never goes red on the about-to-be-authored tests
# (defensive: at the pristine DAG root those files do not exist yet anyway).
$out = dotnet test Guardrails.sln --filter "Category!=Preflights" --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:|error CS|error NU' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the existing tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181)"
    exit 1
}
exit 0
