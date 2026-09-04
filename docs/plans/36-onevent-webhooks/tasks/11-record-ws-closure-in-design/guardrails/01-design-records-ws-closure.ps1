# catches: layer 3 shipping while the design of record still leaves the `ws:` question DANGLING - so
#          #585 cannot honestly be closed with the implementation, and the next reader who finds the
#          endpoint unbuilt goes looking for the deferral issue that was never filed. "Superseded" and
#          "deferred" are different records with different obligations; only one of them is true here.
#
# MEASURED BASELINES (#478), all against docs/plans/585-layer3-webhooks-contract.md as it stands:
#          heading  (?m)^##\s+12\.        = 0   (the document ends at section 11)
#          charter                        = 0   (case-insensitive: neither 'charter' nor 'Charter')
#          charter, in the FIRST 40 LINES = 0   <-- re-measured 2026-09-04 for the deliverable-2 clause
#                                                below; the document is 1208 lines, so this window ends
#                                                around 1160 lines above section 12 and cannot be
#                                                satisfied from it
#          all five                       = 0
#          ws:                            = 16  <-- DOCUMENT-WIDE, and that is the trap
#          superseded                     = 5   <-- DOCUMENT-WIDE (section 2.1's own heading)
#          #585                           = many
#          #585 can be closed             = 1   <-- ALREADY PRESENT, in section 10's handoff row 9,
#                                                where it describes this very deliverable. A clause on
#                                                that phrase would be GREEN ON ARRIVAL and certify
#                                                nothing at all.
#
# THIS IS WHY EVERY CLAUSE IS SCOPED TO SECTION 12'S BODY. This document ALREADY argues the `ws:`
#          closure at length in section 2.1 - a bare `ws:` or `superseded` clause passes today against
#          an unedited file. Inside a section that does not yet exist, each token measures ZERO by
#          construction, and the clause then asserts what it claims: that the closure is RECORDED as
#          the document's own closing statement, not merely argued in a decision section 200 lines up.
#          When section 12 is absent, the body is treated as EMPTY so every clause reports its own
#          reason rather than one clause masking the rest under a single exit code.
#
# Comment-blind (#478): an HTML comment RENDERS AS NOTHING, so a token surviving only inside
#          <!-- ... --> is invisible text rather than thin prose - stripped before matching. (Measured:
#          this document contains ZERO HTML comments today; the strip is kept because a charter
#          round-trip can introduce them.) Fenced code blocks are NOT stripped: a fence renders. An
#          UNTERMINATED '<!--' is a precondition exit, never a strip-to-EOF.
#
# PRECEDENT (the DOC-TARGET exemption from the two-sided sample pair, #468): this subject is a design
#          document, so no meaningful INVALID sample of it exists and none is committed. The
#          compensating control is that every token demanded here already has a sibling precedent in
#          this same document: '## N. Title' is how all eleven existing sections are headed, and
#          section 2.1 already states a closure using the words 'superseded' and 'ws:' in exactly the
#          form clause 2 and clause 3 ask for.
#
# DELIVERABLE 2 IS ASSERTED TOO, and by a SCOPED REQUIRED-PRESENT clause rather than a required-absent
#          one. The document's fourth paragraph still reads '**Status:** proposed ... draft PR for
#          inline review'; correcting it is half of this task. An earlier version of this file declared
#          that a KNOWN LIMIT on the grounds that a required-ABSENT clause on 'draft PR' would misfire
#          on a legitimate record that QUOTES the old status while superseding it. That reasoning about
#          required-absent is correct and is why no such clause exists - but it does not follow that
#          the deliverable is unassertable. The clause below instead requires the word CHARTER within
#          the document's FIRST 40 LINES: measured 0 today (see the baselines above - 'charter' appears
#          ZERO times document-wide, in either case), so it is armed by construction, and a corrected
#          status line naming Charter as the review vehicle is the only realistic way to satisfy it.
#          It cannot be satisfied from section 12, which begins around line 1200. A quotation of the
#          old status is untouched by a require-present clause, so the misfire the limit worried about
#          is structurally impossible here.
$ErrorActionPreference = 'Continue'
$path = 'docs/plans/585-layer3-webhooks-contract.md'

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

# The closing section. Its ABSENCE is the thing under test, so it is an accumulated failure, never an
# early exit - and the body falls back to empty so each clause below still reports its own reason.
$body = ''
$s12 = [regex]::Match($doc, '(?ms)^##\s+12\.(?!\d).*?(?=^##\s|\z)')
if ($s12.Success) {
    $body = $s12.Value
}
else {
    $failures.Add("MISSING the closing section '## 12.' - the review closure is unrecorded. The document ends at section 11, still reading as an open proposal. Append a last section that records the closure.")
}

$clauses = @(
    @{  # measured baseline: 16 DOCUMENT-WIDE, 0 inside section 12 - which is why this clause is scoped
        Pattern = 'ws:'
        Name    = 'the `ws:` endpoint named as the thing being closed'
        Why     = 'the closure has to say WHICH question it closes. Section 2.1 argues it; section 12 records it. NOTE this clause is scoped to section 12 on purpose - `ws:` appears 16 times document-wide, so an unscoped clause would have been green before any edit was made.'
    },
    @{  # measured baseline: 5 DOCUMENT-WIDE (section 2.1's heading), 0 inside section 12
        Pattern = 'superseded'
        Name    = 'the word SUPERSEDED'
        Why     = '"superseded" and "deferred" are different records with different obligations. A reader who finds "deferred" goes looking for a follow-up issue that was deliberately never filed. Say superseded, in that word.'
    },
    @{  # measured baseline: many DOCUMENT-WIDE, 0 inside section 12
        Pattern = '#585'
        Name    = 'the #585 issue number'
        Why     = 'the point of the record is that #585 can be CLOSED with layer 3''s implementation rather than left open behind a dangling question. Name the issue the closure applies to.'
    },
    @{  # measured baseline: 0 DOCUMENT-WIDE (neither `charter` nor `Charter` appears anywhere)
        Pattern = 'charter'
        Name    = 'Charter named as the review vehicle'
        Why     = 'a design of record is reviewed in Charter, not in a draft PR - and section 10 row 9 still calls it a draft-PR review. Record where the review actually happened.'
    },
    @{  # measured baseline: 0 DOCUMENT-WIDE
        Pattern = 'all five'
        Name    = 'the statement that ALL FIVE open questions were settled'
        Why     = 'the design carried five :::question blocks and every one is answered (the answers are inline in docs/plans/36-onevent-webhooks.md). "Some were answered" is the state this record exists to rule out.'
    }
)

foreach ($c in $clauses) {
    if ($body -notmatch [regex]::Escape($c.Pattern)) {
        $failures.Add("MISSING FROM SECTION 12 - $($c.Name). $($c.Why)")
    }
}

# DELIVERABLE 2 - the stale status line. Scoped to the document's HEAD, for the reason in the header:
# a require-present clause cannot misfire on a quotation, and 'charter' measures 0 document-wide today
# so the scope is belt-and-braces rather than the thing doing the work. Lines are counted over the
# comment-stripped text; the document carries zero HTML comments today, so the two are identical, and
# a charter round-trip that introduced some would only widen the window - never far enough to reach
# section 12, which begins around line 1200.
$headLines = @($doc -split "`r?`n" | Select-Object -First 40)
if (($headLines -join "`n") -notmatch 'charter') {
    $failures.Add("MISSING FROM THE DOCUMENT'S FIRST 40 LINES - Charter named as the review vehicle. The fourth paragraph still reads '**Status:** proposed. To be delivered as a **draft PR for inline review**' - which is no longer true; the review has happened, in Charter. Correct that line in place: say the design is reviewed and settled, and name Charter rather than a draft PR (a design of record is reviewed in Charter; a PR is a code-review vehicle). This clause is REQUIRE-PRESENT, so it is satisfied by naming Charter - not by deleting anything, and a sentence that QUOTES the old status while superseding it is fine.")
}

# Substance floor. A LOWER BOUND, never a quality judgement. Measured: a single line carrying all five
# tokens under a '## 12.' heading satisfied every clause above and exited 0. Five non-blank lines is
# still a small section - this task is deliberately small - and it is more than a word list.
if ($s12.Success) {
    $lines = @($body -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -lt 5) {
        $failures.Add("SECTION 12 has $($lines.Count) non-blank line(s). Record the two things in prose: the ws: supersession with its pointer to section 2.1, and what the Charter review settled. A token list is not a record.")
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== $path is missing $($failures.Count) required element(s) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "Section 2.1 already ARGUES the ws: closure; section 10 row 9 already ASKS for it. Neither is the record. Until the document states it as its own closing position, #585 cannot honestly be closed with the implementation."
    exit 1
}

Write-Output "$path records the review closure in section 12: the ws: endpoint superseded (not deferred), #585 closable with the implementation, and the Charter review settling all five open questions - and its opening status paragraph names Charter rather than the draft PR it used to promise."
exit 0
