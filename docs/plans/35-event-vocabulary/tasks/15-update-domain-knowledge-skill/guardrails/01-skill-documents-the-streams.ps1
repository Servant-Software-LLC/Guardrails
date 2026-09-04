# catches: a contract change that shipped WITHOUT its documentation - the invariant plan 34 broke.
#          Each clause below is a required-present token in .claude/skills/guardrails-domain-knowledge/SKILL.md.
# Measured baseline (#478): every token below returns ZERO in this file at authoring time. NOTE: a bare
#          'seq' did NOT - it measured 1, matching 'sequence' in 'strictly-ordered sequence of waves' -
#          so that clause was replaced with 'ordering key', which measures 0 and asserts the RULE
#          rather than three letters. A nonzero
#          baseline would mean the clause was already satisfied and certifies nothing.
# Comment-blind (#478): an HTML comment RENDERS AS NOTHING, so a token that survives only inside
#          <!-- ... --> is invisible text, not thin prose - it is stripped before matching. Fenced code
#          blocks are NOT stripped: a fence renders, so a token documented in a usage fence is
#          legitimate house style.
# PRECEDENT (the documentation exemption from the two-sided sample pair, #468): no meaningful INVALID
#          sample of a design document exists, so the compensating control is that every token demanded
#          here already has a sibling precedent in this same document - each token is a filename, verb, or field name introduced exactly as this skill already introduces the journal and the telemetry corpus in the same quick-reference.
$ErrorActionPreference = 'Continue'
$path = '.claude/skills/guardrails-domain-knowledge/SKILL.md'

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

# SCOPED to the Quick Reference section. Measured: appending ONE line carrying all five tokens to the
# END of the file satisfied every clause and exited 0. The prompt requires these additions land in the
# contract quick-reference, and a whole-document scope cannot tell that from a footer.
$qr = [regex]::Match($doc, '(?ms)^##\s+Quick Reference.*?(?=^##\s)')
if (-not $qr.Success) {
    Write-Output "PRECONDITION: could not locate the '## Quick Reference' section - the skill was restructured, so this check cannot scope itself and would report a false absence."
    exit 1
}
$doc = $qr.Value

$clauses = @(
    @{ Token = 'events.jsonl'; Why = "the semantic agent-facing stream - absent from this skill entirely today" },
    @{ Token = 'observer.jsonl'; Why = "the render-fidelity projection that drives attach" },
    @{ Token = 'guardrails attach'; Why = "the verb a reader of this skill has no other way to learn about" },
    @{ Token = 'run-finished'; Why = "the run-termination kind, and the one an unattended supervisor branches on" },
    @{ Token = 'ordering key'; Why = "seq, not at, is the ordering key - a bare 'seq' clause was GREEN ON ARRIVAL here (it matches 'sequence' as a substring, measured 1 hit) and certified nothing" },
    @{ Token = 'DAG'; Why = "the ABSENCE RULE - the stream begins with the DAG, so an empty stream does NOT mean a healthy quiet run. An unattended supervisor branches on exactly this, and mistaking silence for health is the defect #585 was filed about" }
)

$failures = New-Object System.Collections.Generic.List[string]
foreach ($c in $clauses) {
    if ($doc -notmatch [regex]::Escape($c.Token)) {
        $failures.Add("MISSING '$($c.Token)' - $($c.Why)")
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== $path is missing $($failures.Count) required element(s) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The wire format is the most contract-shaped artifact in this repo - the thing an external consumer parses. Document it in the same change that ships it."
    exit 1
}
Write-Output "$path documents all $($clauses.Count) required element(s)."
exit 0
