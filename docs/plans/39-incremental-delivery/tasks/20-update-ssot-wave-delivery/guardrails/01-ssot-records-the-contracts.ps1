# catches: the contract moving without the document that describes it. Each token below measured
#          ZERO occurrences in the subject at authoring time (#478) — the expected answer for a
#          MEASURED: 'delivers' was already present 7x in docs/plans/02-schemas-and-contracts.md — green on
#          arrival and therefore toothless, hidden behind its siblings' failure. Replaced
#          with '"delivers"', measured 0.
#          MEASURED: 'covers' was already present 28x in docs/plans/02-schemas-and-contracts.md — green on
#          arrival and therefore toothless, hidden behind its siblings' failure. Replaced
#          with 'covers: [', measured 0.
#          required-present clause, so every one has teeth.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = 'docs/plans/02-schemas-and-contracts.md'

if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output "PRECONDITION: $subject does not exist."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $subject

# An HTML comment RENDERS AS NOTHING, so a token only inside one is invisible text and must not
# satisfy a required-present clause. Fences are NOT stripped — a fence renders, so a token in a
# usage fence is legitimate house style.
if ($raw -match '<!--(?![\s\S]*?-->)') {
    Write-Output "PRECONDITION: $subject has an unterminated '<!--'. Refusing to strip to EOF."
    exit 1
}
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')

$failures = @()

if ($doc -notmatch [regex]::Escape('"delivers"')) {
    $failures += "MISSING '`"delivers`"' in $subject — the per-wave delivers flag is not recorded"
}

if ($doc -notmatch [regex]::Escape('WaveDelivered')) {
    $failures += "MISSING 'WaveDelivered' in $subject — the observer event is not recorded"
}

if ($doc -notmatch [regex]::Escape('covers: [')) {
    $failures += "MISSING 'covers: [' in $subject — the covers[] field is not recorded, so a reader cannot tell what a merge carried"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
