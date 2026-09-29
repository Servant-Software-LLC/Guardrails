# catches: a lock.ps1 that re-implements hashing inline instead of importing Hash.psm1 - it can pass
#          today's tests and then silently drift from the parity-tested module (and from Core) the first
#          time HashText changes, so the lock would verify against a DIFFERENT hash than the review marker
#          and the journal. Also a stub marker ("stub":true / exit 99) left behind green tests.
#          The required clause asserts an Import-Module STATEMENT naming Hash.psm1 - a module-import
#          declaration, deliberately not a member reference; the teeth are the forbidden clause below.
# WHY A SOURCE-SHAPE CHECK (#468): "lock.ps1 relies on Hash.psm1" is an agreement property whose runtime
#          proxy (LiteLockTests comparing against Core) passes for an inlined copy that is equivalent TODAY;
#          only the file's shape says whether the two can drift. The ban on SHA256/HashData/ComputeHash is
#          the teeth: a lock that hashes on its own has to reach one of those APIs.
# Comments are stripped first, so a comment that MENTIONS SHA-256 or the old stub is legal.
# baseline counts on the tree this task sees (task 11's stub, measured on the prompt's stub shape):
#   Import-Module ... Hash.psm1 (required-present)   0   - the stub imports nothing
#   forbidden clauses are not censused (a ban green on arrival is a correct ban)
# Subject: argv[0] or GR_SUBJECT, else scripts/lite/lock.ps1.
# Committed pair: ../samples/03-uses-hash-module-no-stub.valid.ps1 -> 0 ; .invalid.ps1 -> 1 (inline SHA256)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($args.Count -gt 0 -and $args[0]) { $args[0] } elseif ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'scripts/lite/lock.ps1' }

# PRECONDITION - the only early exit.
if (-not (Test-Path -LiteralPath $f)) {
    Write-Output "$f is missing - lock.ps1 must exist"
    exit 1
}
$raw  = Get-Content -LiteralPath $f -Raw
$code = [regex]::Replace($raw, '(?s)<#.*?#>', '')
$code = [regex]::Replace($code, '(?m)#.*$', '')

$failures = @()
if ($code -notmatch '(?im)^\s*Import-Module\b[^\r\n]*Hash\.psm1') {
    $failures += "$f does not Import-Module lib/Hash.psm1 - reuse the parity-tested hash functions instead of hashing inline"
}
if ($code -match '(?i)\bSHA256\b|\bHashData\b|\bComputeHash\b|Get-FileHash') {
    $failures += "$f computes a hash itself (SHA256 / HashData / ComputeHash / Get-FileHash) - call Get-LitePlanDefinitionHash / Get-LiteTaskDefinitionHash from Hash.psm1 instead"
}
if ($code -match '"stub"\s*:\s*true') { $failures += "$f still emits the stub marker '`"stub`":true'" }
if ($code -match '(?m)\bexit\s+99\b')   { $failures += "$f still exits 99 (the stub exit code)" }

if ($failures.Count -gt 0) {
    Write-Output "=== lock.ps1 shape problems ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "$f imports Hash.psm1, hashes nothing itself, and carries no stub marker"
exit 0
