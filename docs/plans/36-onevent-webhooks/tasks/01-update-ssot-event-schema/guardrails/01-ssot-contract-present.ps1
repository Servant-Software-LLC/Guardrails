# catches: layer 3 shipping its CONTRACT CHANGES only in the design document - the `bracket` field on
#          the section 8.1 row and the whole of section 8.3 living in a plan folder nobody re-reads
#          while the SSOT still tells a webhook receiver that its dedupe key is (runId, seq). That is
#          invariant #4 broken exactly the way plan 34 broke it, and the cost is silent: a receiver
#          built to the un-updated SSOT discards an entire resumed run and nothing anywhere reports it.
#
# MEASURED BASELINES (#478) - every clause below returned ZERO in this file at authoring time:
#          table row  (?m)^\|\s*`bracket`\s*\|   = 0
#          (runId, bracket, seq)                 = 0
#          unix-ms                               = 0
#          heading    (?m)^###\s+8\.3            = 0
#          --on-event-detail                     = 0
#          by position           (SCOPED to 8.1) = 0 document-wide
#          GUARDRAILS_ON_EVENT_AUTH (SCOPED 5.1) = 0 document-wide
#          --on-event            (SCOPED 12.2)   = 1 DOCUMENT-WIDE (section 8.1 line 3841 already
#                                                  forward-references the flag), which is precisely
#                                                  why that clause is SCOPED to section 12.2, where it
#                                                  measures 0. Unscoped it would be GREEN ON ARRIVAL.
#          A NONZERO baseline means the clause was already satisfied and certifies nothing.
#
# WHY THE DISCRIMINATING FORMS. A bare `bracket` clause measures ELEVEN hits here and a bare
#          `on-event` clause measures ONE. Both would pass today, against an unedited document, and
#          would hide behind their failing siblings under one exit code. Every clause below is either
#          a form that measures 0 or is scoped to a section where it does.
#
# Comment-blind (#478): an HTML comment RENDERS AS NOTHING, so a token surviving only inside
#          <!-- ... --> is invisible text rather than thin prose - stripped before matching. Fenced
#          code blocks are NOT stripped: a fence renders, so a field shown in a usage fence is house
#          style. An UNTERMINATED '<!--' is a precondition exit, never a strip-to-EOF.
#
# PRECEDENT (the DOC-TARGET exemption from the two-sided sample pair, #468): this subject is a design
#          document, so no meaningful INVALID sample of it exists and none is committed. The
#          compensating control is that every token demanded here already has a sibling precedent in
#          this same document: the SSOT's own field-table style is `| `field` | description |` - one
#          backticked field name in the first cell - which is exactly the form Edit 1's `bracket` row
#          uses, and `### N.M Title` is how every other subsection here is headed.
$ErrorActionPreference = 'Continue'
$path = 'docs/plans/02-schemas-and-contracts.md'

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

$failures = New-Object System.Collections.Generic.List[string]

# --- Edits 1 and 3: forms that measure 0 across the WHOLE document, so no scope is needed. ---
$wide = @(
    @{  # measured baseline: 0 (a BARE `bracket` measures 11 - that form is banned here)
        Pattern = '(?m)^\|\s*`bracket`\s*\|'
        Name    = 'the `bracket` field row in section 8.1'
        Why     = 'Edit 1 is unwritten. Section 8.1''s per-row field table must gain a `bracket` row in that table''s own "| `field` | description |" form. Without the field, (runId, seq) stays the documented key and a resume - which reuses runId and restarts seq at 1 - makes a webhook receiver silently discard the whole resumed run.'
    },
    @{  # measured baseline: 0
        Pattern = '(runId, bracket, seq)'
        Literal = $true
        Name    = 'the delivery key (runId, bracket, seq)'
        Why     = 'the three-part key must be stated as a key. This is the one sentence that makes a receiver dedupe correctly across a resume, and it is the reason `bracket` is being added at all.'
    },
    @{  # measured baseline: 0
        Pattern = 'unix-ms'
        Literal = $true
        Name    = 'the bracket''s `<unix-ms>-<4 hex>` shape'
        Why     = 'a receiver is promised an OPAQUE token for equality whose millisecond prefix additionally ORDERS two brackets - which is the only way a consumer that never sees file order can apply the "take the LAST run-finished" rule. Naming the field without its shape leaves that unpromised.'
    },
    @{  # measured baseline: 0
        Pattern = '(?m)^###\s+8\.3'
        Name    = 'the section 8.3 heading'
        Why     = 'Edit 3 is unwritten. The whole webhook wire contract - request shape, headers, failure policy, shutdown promise, security posture - is a NEW subsection 8.3, at ### depth, between the end of 8.2 and "## 9. Prompt runners".'
    },
    @{  # measured baseline: 0
        Pattern = '--on-event-detail'
        Literal = $true
        Name    = 'the --on-event-detail opt-in'
        Why     = 'the ONE documented divergence between the wire body and the events.jsonl line is `detail`: withheld by default, included (capped) only when a human passes this flag. Undocumented, a receiver reads the withheld marker as "nothing to report".'
    }
)

foreach ($c in $wide) {
    $pattern = if ($c.Literal) { [regex]::Escape($c.Pattern) } else { $c.Pattern }
    if ($doc -notmatch $pattern) {
        $failures.Add("MISSING $($c.Name) - $($c.Why)")
    }
}

# --- Edits 2, 4 and 5 land in sections the clauses above are structurally BLIND to. Each is scoped ---
# --- to its own section: unscoped, Edit 5's token is already present elsewhere in the document.   ---
$scoped = @(
    @{  # measured baseline: 0 document-wide; scoped anyway so the sentence cannot land in 8.3 instead
        Start   = '(?m)^###\s+8\.1(?!\d)'
        End     = '(?m)^###\s+8\.2(?!\d)'
        Section = 'section 8.1'
        Pattern = 'by position'
        Literal = $true
        Name    = "Edit 2's sentence in section 8.1's multi-process paragraph"
        Why     = 'the "A runId spans processes" paragraph must say that each process''s rows carry a distinct `bracket`, so "which run-finished is mine?" is answerable BY KEY rather than only BY POSITION - the form a webhook receiver needs, since it never sees file order at all.'
    },
    @{  # measured baseline: 0 document-wide (GUARDRAILS_ON_EVENT appears nowhere today)
        Start   = '(?m)^###\s+5\.1(?!\d)'
        End     = '(?m)^###\s+5\.2(?!\d)'
        Section = 'section 5.1'
        Pattern = 'GUARDRAILS_ON_EVENT_AUTH'
        Literal = $true
        Name    = "Edit 4's harness-process knobs update in section 5.1"
        Why     = "the closing 'harness-process knobs' paragraph must list GUARDRAILS_ON_EVENT and GUARDRAILS_ON_EVENT_AUTH (and the two telemetry vars the sentence is already stale about). For the AUTH value this is a SECURITY property, not bookkeeping: section 5.1's hermeticity rule (#442) strips every unlisted GUARDRAILS_* variable from every child, and that is what keeps a webhook credential out of every action, guardrail script and merge worker."
    },
    @{  # measured baseline: 1 DOCUMENT-WIDE (section 8.1's forward reference), 0 inside section 12.2
        Start   = '(?m)^###\s+12\.2(?!\d)'
        End     = '(?m)^###\s+12\.3(?!\d)'
        Section = 'section 12.2'
        Pattern = '--on-event'
        Literal = $true
        Name    = "Edit 5's cross-reference in section 12.2"
        Why     = "section 12.2 documents GET /events serving these rows; it must also say they can be PUSHED to an operator-supplied endpoint (section 8.3) - delivery of the same projection, not a second stream. NOTE: this clause is scoped to 12.2 on purpose - '--on-event' already appears once in section 8.1, so a document-wide clause here would have been green before any edit was made."
    }
)

foreach ($c in $scoped) {
    $m = [regex]::Match($doc, "(?ms)$($c.Start).*?(?=$($c.End))")
    if (-not $m.Success) {
        $failures.Add("CANNOT LOCATE $($c.Section) (searched '$($c.Start)' up to '$($c.End)') - the document was restructured, so this clause cannot scope itself and would otherwise report a false absence. Confirm the section still exists, then re-run.")
        continue
    }
    $pattern = if ($c.Literal) { [regex]::Escape($c.Pattern) } else { $c.Pattern }
    if ($m.Value -notmatch $pattern) {
        $failures.Add("MISSING FROM $($c.Section) - $($c.Name) is unwritten. $($c.Why)")
    }
}

# --- Substance floor on section 8.3. A LOWER BOUND, never a quality judgement. ---
# Measured: a five-line insert - one heading plus one line carrying every token demanded above -
# satisfied every clause and exited 0. A token census cannot tell a wire contract from a word list,
# so bound how much has to be written. Edit 3's own text is ~80 lines, so 25 is comfortably beneath a
# faithful application and far above a token-stuffed stub.
$s83 = [regex]::Match($doc, '(?ms)^###\s+8\.3(?!\d).*?(?=^###\s|^##\s|\z)')
if ($s83.Success) {
    $lines = @($s83.Value -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -lt 25) {
        $failures.Add("SECTION 8.3 has $($lines.Count) non-blank line(s). It is the whole webhook wire contract - request and body, the five headers, the retry/circuit policy, the shutdown promise for the terminal row, the drop-recording rule, the configuration table and the security posture. Apply Edit 3 in full rather than a stub.")
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== $path is missing $($failures.Count) required element(s) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "All five edits are written out verbatim in 'docs/plans/36-onevent-webhooks.md' section 7. Anchor each one by grepping for its durable marker ('On every row, without exception.', 'A runId spans processes', '## 9. Prompt runners', 'Harness-process knobs', 'GET /events' inside 12.2) - never by the line numbers that section cites."
    Write-Output "A contract change lands in the SAME change-set that motivates it (invariant #4). The SSOT is what a webhook receiver is built against; the design document is not."
    exit 1
}

Write-Output ($path + ' carries all five layer-3 schema edits: the bracket row and its <unix-ms>-<4 hex> shape in 8.1, the (runId, bracket, seq) key, section 8.3 with the --on-event-detail rule, and the 5.1 / 12.2 cross-references.')
exit 0
