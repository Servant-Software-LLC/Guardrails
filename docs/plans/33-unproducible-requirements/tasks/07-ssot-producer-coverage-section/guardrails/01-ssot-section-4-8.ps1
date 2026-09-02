# catches: an SSOT section 4.8 that exists but omits the two paragraphs it was written by hand to carry.
#          Doc 19 did not anticipate either, and both encode findings that cost this design real rework:
#          (a) PlanIsClosed and wavePrefixIsIncomplete are NOT interchangeable, and (b) an excused
#          finding is still REPORTED. A section that carries the predicate and drops those two reads
#          complete and teaches the next reader the mistake this plan just made.
#
# EVERY CONTENT CLAUSE SCANS THE 4.8 SECTION BODY, NOT THE WHOLE DOCUMENT - and that is a correction
#          this guardrail made to itself when it was first run (#478). Measured on master @67859c7
#          against the WHOLE file: GR2060 appears 2 times (a section 14.1 cross-reference and the
#          section 14.10 reservation list) and PlanIsClosed 3 times. Whole-document clauses for those
#          two were therefore GREEN ON ARRIVAL, hiding behind their failing siblings inside one exit
#          code - satisfied before the task ran, proving nothing about the section being written.
#
# Required-present baseline (#478), re-measured against the SECTION BODY, which is where these clauses
#          now scan: the 4.8 heading occurs 0 times, so the body does not exist and every content clause
#          measures 0. Expected 0, and now genuinely so.
#
# DOCUMENTATION TARGET: exempt from the two-sided sample pair (#468) - no meaningful invalid sample of a
#          contract document exists. The PRECEDENT check is the substitute: the heading shape mirrors the
#          shipped `### 4.7 ` sibling, `GR2060` mirrors every other code this document names, and the
#          prose tokens are required as SUBSTRINGS, never as one mandated sentence.
#
# HTML COMMENTS ARE STRIPPED before matching (#97/#98 running the other way): a required-present clause
#          over a document is satisfied by an HTML comment, which renders as NOTHING - measured on this
#          exact file, where appending one commented TODO flipped a two-token contract check from exit 1
#          to exit 0. Fenced code blocks are NOT stripped: a fence RENDERS, and this document carries
#          43,387 bytes of fenced content, so stripping fences would reject its own house style.
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'docs/plans/02-schemas-and-contracts.md' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist.')
    exit 1
}

$raw = Get-Content -LiteralPath $subject -Raw
if ($raw -match '<!--(?:(?!-->)[\s\S])*$') {
    Write-Output ('PRECONDITION: ' + $subject + ' contains an UNTERMINATED HTML comment. Refusing to strip to end-of-file, which would delete the rest of the document over one stray token. Fix the comment first.')
    exit 1
}
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')

$head = [regex]::Match($doc, '(?m)^###\s+4\.8\s')
if (-not $head.Success) {
    Write-Output ('SECTION 4.8 IS MISSING from ' + $subject + '. GR2060 shipped in task 4 and the contract moves in the same change-set as the code (invariant 4). Place it after section 4.7 and before the child-process contract heading; the heading form mirrors its shipped 4.7 sibling.')
    exit 1
}

# Body = this heading to the next heading of the same or higher level, or end of file.
$rest = $doc.Substring($head.Index + $head.Length)
$next = [regex]::Match($rest, '(?m)^#{1,3}\s')
$body = if ($next.Success) { $rest.Substring(0, $next.Index) } else { $rest }

$failures = New-Object System.Collections.Generic.List[string]

if ($body -notmatch 'GR2060') {
    $failures.Add('SECTION 4.8 DOES NOT NAME GR2060 in its own body. The section is the code contract; a reader who greps the code must land here, not in a cross-reference elsewhere in the document.')
}

$hasClosed = $body -match 'PlanIsClosed'
$hasPrefix = $body -match 'wavePrefixIsIncomplete'
if (-not ($hasClosed -and $hasPrefix)) {
    $failures.Add('THE TWO-SUPPRESSIONS PARAGRAPH IS MISSING from section 4.8 body: it must name BOTH PlanIsClosed and wavePrefixIsIncomplete and say they are not interchangeable - PlanIsClosed suppresses an EMPTY STUB WAVE and returns true for an authored PARTIAL PREFIX, which is why the JIT gate needs its own excuse. Found PlanIsClosed=' + $hasClosed + ', wavePrefixIsIncomplete=' + $hasPrefix + '. This is the trap that cost the design a milestone of rework; a document that omits it teaches the mistake.')
}

if ($body -notmatch '(?i)excus') {
    $failures.Add('THE EXCUSED-NOT-VANISHED RULE IS MISSING from section 4.8 body: it must say that a finding excused at the JIT gate still appears in the gate-decision report and still errors under a plain validate. Suppression is about which VERDICT a finding may cast, never about whether an operator SEES it.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== SSOT section 4.8 is incomplete (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output 'SSOT section 4.8 is present and its body carries GR2060, the two-suppressions paragraph and the excused-not-vanished rule.'
exit 0
