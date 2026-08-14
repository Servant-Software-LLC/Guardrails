# catches: implementing only ONE of the two proofs. The resolved invariant-7-proof question settled on
#          BOTH - the golden catches drift the negative assertions never enumerated, the negative assertions
#          say plainly what must never appear. Either alone leaves the other's failure mode uncovered.
$ErrorActionPreference = 'Stop'
$dir = 'tests/Guardrails.Integration.Tests/ModelTiering'
if (-not (Test-Path $dir)) { Write-Output "$dir was not created"; exit 1 }
$golden = 'tests/Guardrails.Integration.Tests/Fixtures/no-routing-golden'
if (-not (Test-Path $golden)) {
    Write-Output "$golden is missing - the committed golden folder is half the settled proof"
    exit 1
}
if (-not (Get-ChildItem -Path $golden -Recurse -File)) {
    Write-Output "$golden is empty - a golden with no files asserts nothing"
    exit 1
}
$c = (Get-ChildItem -Path $dir -Filter *.cs -Recurse | Get-Content -Raw) -join "`n"
if ($c -notmatch '(?i)(byte|golden)') {
    Write-Output "no test in $dir asserts the byte-for-byte golden reproduction"
    exit 1
}
if ($c -notmatch '(?i)(DoesNotContain|NotContains|Assert\.False|should not)') {
    Write-Output "no test in $dir carries the NEGATIVE assertions (no action.tier / tiering block / report line) - the second half of the settled proof"
    exit 1
}
exit 0
