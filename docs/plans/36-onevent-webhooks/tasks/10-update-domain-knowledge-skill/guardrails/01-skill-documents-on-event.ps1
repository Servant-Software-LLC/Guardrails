# catches: layer 3 shipping past the guardrails-domain-knowledge skill's OWN self-updating clause -
#          the same way plan 34 shipped events.jsonl past both this skill and the SSOT. The skill is
#          what every agent in this repo reads before touching the harness; a webhook contract it has
#          never heard of is a contract the next agent designs against from memory.
#
# MEASURED BASELINES (#478) - every clause below returned ZERO in this file at authoring time:
#          --on-event            = 0    (and `bracket` = 0, and `delivery key` = 0)
#          --on-event-detail     = 0
#          (runId, bracket, seq) = 0
#          affects the run       = 0
#          8.3                   = 0    (the section already cites 8.1 and 8.2, never 8.3)
#          A NONZERO baseline would mean the clause was already satisfied and certifies nothing.
#          NOTE the near-miss: a bare `withheld` measures THREE here, so the detail rule is asserted
#          through `--on-event-detail` (0) rather than through the word `withheld`.
#          NOTE the second near-miss: `--on-event-detail` CONTAINS `--on-event`, so a plain
#          `--on-event` clause would be satisfied by the detail clause alone and would certify
#          nothing of its own. It is written as `--on-event(?!-)` so it demands the bare flag.
#
# Comment-blind (#478): an HTML comment RENDERS AS NOTHING, so a token surviving only inside
#          <!-- ... --> is invisible text rather than thin prose - stripped before matching. Fenced
#          code blocks are NOT stripped: a fence renders, so a flag shown in a usage fence is house
#          style. An UNTERMINATED '<!--' is a precondition exit, never a strip-to-EOF.
#
# SCOPED to the '## Quick Reference' section on purpose. Measured: appending ONE line carrying all
#          five tokens to the END of the file satisfied a document-wide version of every clause and
#          exited 0. The deliverable is an addition to the CONTRACT QUICK-REFERENCE - the section an
#          agent actually reads - and a whole-document scope cannot tell that from a footer.
#
# PRECEDENT (the DOC-TARGET exemption from the two-sided sample pair, #468): this subject is a
#          markdown skill, so no meaningful INVALID sample of it exists and none is committed. The
#          compensating control is that every token demanded here already has a sibling precedent in
#          this same section: the "run's own streams" paragraph already introduces `events.jsonl` and
#          `observer.jsonl` as backticked names, already states an ordering rule in prose, and
#          already cites 'docs/plans/02-schemas-and-contracts.md sections 8.1 and 8.2' as the SSOT
#          instead of copying their field tables. Clause 5 asks for exactly that citation form.
$ErrorActionPreference = 'Continue'
$path = '.claude/skills/guardrails-domain-knowledge/SKILL.md'

if (-not (Test-Path -LiteralPath $path)) {
    Write-Output "PRECONDITION: $path does not exist - every clause below would crash rather than report."
    exit 1
}

$raw = Get-Content -LiteralPath $path -Raw
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
if ($doc -match '<!--') {
    Write-Output "PRECONDITION: $path has an unterminated '<!--'. Refusing to strip to EOF over one stray token, which would delete the rest of the document from this check's view and report every clause below as a false absence."
    exit 1
}

$qr = [regex]::Match($doc, '(?ms)^##\s+Quick Reference.*?(?=^##\s)')
if (-not $qr.Success) {
    Write-Output "PRECONDITION: could not locate the '## Quick Reference' section in $path - the skill was restructured, so this check cannot scope itself and would report every clause below as a false absence."
    exit 1
}
$section = $qr.Value

$clauses = @(
    @{  # measured baseline: 0. Written as `--on-event(?!-)` so `--on-event-detail` cannot satisfy it.
        Pattern = '--on-event(?!-)'
        IsRegex = $true
        Name    = 'the --on-event flag itself'
        Why     = 'an agent reading this skill has no other way to learn the harness can POST its event rows to an operator-supplied endpoint. Name the flag and say what it does: the same events.jsonl projection, delivered rather than served.'
    },
    @{  # measured baseline: 0 (a bare `bracket` also measures 0 in this file)
        Pattern = '(runId, bracket, seq)'
        Name    = 'the delivery key (runId, bracket, seq)'
        Why     = 'state the triple in that form. `seq` restarts at 1 on a resume, so (runId, seq) collides across brackets and a receiver deduplicating on it silently discards an entire resumed run - which is the whole reason the `bracket` field was added to the row.'
    },
    @{  # measured baseline: 0
        Pattern = 'affects the run'
        Name    = 'the never-affects-the-run rule'
        Why     = "the maintainer ruling is that a failed delivery must NEVER affect the run - not the exit code, not the verdict, not the journal. An agent that does not know this will 'fix' a dropped delivery by failing the run, which inverts the ruling."
    },
    @{  # measured baseline: 0 (a bare `withheld` measures 3 here and would be green on arrival)
        Pattern = '--on-event-detail'
        Name    = 'the --on-event-detail opt-in'
        Why     = '`detail` is the one free-text field on the row and it is WITHHELD by default, carrying a fixed marker, unless a human passes this flag. The field is always present, so a supervisor reading the marker as "nothing to report" is reading it wrong.'
    },
    @{  # measured baseline: 0 (this section cites 8.1 and 8.2 today; 8.3 appears nowhere)
        Pattern = '8.3'
        Name    = 'the SSOT citation to section 8.3'
        Why     = 'the wire contract - headers, retry policy, shutdown promise, security posture - belongs in docs/plans/02-schemas-and-contracts.md section 8.3, cited here the same way this section already cites 8.1 and 8.2. A second copy of a contract in a quick-reference is a second thing to drift.'
    }
)

$failures = New-Object System.Collections.Generic.List[string]
foreach ($c in $clauses) {
    $pattern = if ($c.IsRegex) { $c.Pattern } else { [regex]::Escape($c.Pattern) }
    if ($section -notmatch $pattern) {
        $failures.Add("MISSING FROM THE QUICK REFERENCE - $($c.Name). $($c.Why)")
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== $path is missing $($failures.Count) required element(s) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "This file is under .claude/ - do NOT Write or Edit it directly. Emit a top-level needsHarnessWrite request using the 'edits' form (the file is ~140 KB; full-content mode is refused over 64 KB) and let the harness perform the write."
    Write-Output "The skill's own frontmatter carries a SELF-UPDATING clause: a contract change updates it in the SAME change-set. This skill is what the next agent reads before touching the harness."
    exit 1
}

Write-Output "$path documents all $($clauses.Count) required element(s) in its Quick Reference: the --on-event flag, the (runId, bracket, seq) delivery key, the never-affects-the-run rule, the --on-event-detail opt-in, and the SSOT citation."
exit 0
