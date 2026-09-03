# catches: a contract change that shipped WITHOUT its documentation - the invariant plan 34 broke when
#          it shipped events.jsonl and observer.jsonl with no SSOT entry at all.
# SCOPED TO SECTION 8 on purpose (#478). The content clauses are matched against section 8's body, not
#          the whole document. Measured at authoring time: 'TelemetryRow' appears 4 times document-wide
#          (it has its own section 15.2) and a bare 'seq' appears 29 times as a substring of
#          "sequence"/"consequence" - so document-wide those two clauses were GREEN ON ARRIVAL and
#          certified nothing, hiding behind their failing siblings under one exit code. Scoped to
#          section 8 every token below measures ZERO, and the clause asserts what it claims: that these
#          facts are stated where the run streams are documented.
# Comment-blind (#478): an HTML comment RENDERS AS NOTHING, so a token surviving only inside
#          <!-- ... --> is invisible text, not thin prose - stripped before matching. Fenced code blocks
#          are NOT stripped: a fence renders, so a field documented in a usage fence is house style.
# PRECEDENT (the documentation exemption from the two-sided sample pair, #468): no meaningful INVALID
#          sample of a design document exists, so the compensating control is that every token demanded
#          here already has a sibling precedent in this same document - each is either a heading in its
#          own '### N.M Title' form, or a filename/field name this document already introduces other
#          wire formats by (see section 15.2 for TelemetryRow's own entry).
$ErrorActionPreference = 'Continue'
$path = 'docs/plans/02-schemas-and-contracts.md'

if (-not (Test-Path -LiteralPath $path)) {
    Write-Output "PRECONDITION: $path does not exist - every clause below would crash."
    exit 1
}

$raw = Get-Content -LiteralPath $path -Raw
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
if ($doc -match '<!--') {
    Write-Output "$path has an unterminated '<!--'. Refusing to strip to EOF over one stray token, which would delete the rest of the document from this check's view."
    exit 1
}

$failures = New-Object System.Collections.Generic.List[string]

# --- headings: matched against the WHOLE document (they ARE the new subsections) ---
foreach ($h in @(
    @{ Token = '### 8.1'; Why = 'section 8.1 must document the run event stream (logs/<runId>/events.jsonl)' },
    @{ Token = '### 8.2'; Why = 'section 8.2 must document the observer projection (logs/<runId>/observer.jsonl)' })) {
    if ($doc -notmatch [regex]::Escape($h.Token)) {
        $failures.Add("MISSING HEADING '$($h.Token)' - $($h.Why)")
    }
}

# --- section 8's body: from its own heading to the start of section 9 ---
$s8 = [regex]::Match($doc, '(?s)^##\s+8\..*?(?=^##\s+9\.)', 'Multiline')
if (-not $s8.Success) {
    Write-Output "PRECONDITION: could not locate section 8's body (from '## 8.' to '## 9.') in $path. The document was restructured; this check cannot scope itself and would report every clause missing for the wrong reason."
    exit 1
}
$body = $s8.Value

foreach ($c in @(
    @{ Token = 'events.jsonl';     Why = "the semantic stream's filename - the artifact an external consumer parses" },
    @{ Token = 'observer.jsonl';   Why = 'the observer projection that drives guardrails attach' },
    @{ Token = 'run-finished';     Why = 'the run-termination kind this plan adds, and the only run-scoped one' },
    @{ Token = 'attempt-finished'; Why = 'the kind whose field set this plan widens' },
    @{ Token = 'ordering key';     Why = "seq, not at, is the ordering key - a consumer keying on 'at' is wrong under parallel workers, and the contract has to say so" },
    @{ Token = 'TelemetryRow';     Why = 'each attempt-finished field names its telemetry twin; unstated, the vocabulary forks (#585)' },
    @{ Token = 'faultKind';        Why = 'must be documented as a TYPE NAME and never a message - a security property once layer 3 POSTs these rows to an operator-supplied URL' })) {

    if ($body -notmatch [regex]::Escape($c.Token)) {
        $failures.Add("MISSING FROM SECTION 8 '$($c.Token)' - $($c.Why)")
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== $path is missing $($failures.Count) required element(s) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The wire format is the most contract-shaped artifact in this repo - the thing an external consumer parses. Document it in the same change that ships it."
    exit 1
}
Write-Output "Section 8 documents both run streams: headings, kinds, the ordering key, the TelemetryRow twinning and the faultKind rule."
exit 0
