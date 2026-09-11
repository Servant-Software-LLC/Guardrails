# catches: the contract moving without the document that describes it. Each token below
#          measured ZERO occurrences in the subject at authoring time (#478) — the expected
#          answer for a required-present clause, so every one of them has teeth.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = 'docs/plans/02-schemas-and-contracts.md'

if (-not (Test-Path -LiteralPath $subject)) {
    # PRECONDITION: every clause below would crash on a missing subject.
    Write-Output "PRECONDITION: $subject does not exist."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $subject

# An HTML comment RENDERS AS NOTHING, so a token that only appears inside one is invisible
# text and must not satisfy a required-present clause.
if ($raw -match '<!--(?![\s\S]*?-->)') {
    Write-Output "PRECONDITION: $subject contains an unterminated '<!--'. Refusing to strip to EOF, which would delete the rest of the document over one stray token."
    exit 1
}
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')

$failures = @()

if ($doc -notmatch [regex]::Escape('logs/<runId>/supplied/')) {
    $failures += "MISSING 'logs/<runId>/supplied/' in $subject — the §1 staging-tree contract is not recorded"
}

if ($doc -notmatch [regex]::Escape('supplied[]')) {
    $failures += "MISSING 'supplied[]' in $subject — the §7 run.json provenance section is not recorded"
}

if ($doc -notmatch [regex]::Escape('SuppliedResourcesCommitted')) {
    $failures += "MISSING 'SuppliedResourcesCommitted' in $subject — the §8 observer event is not recorded"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
