# catches: shipped code whose CONTRACT never moved - the SSOT still lists three tierSource values while
#          run.json writes a fourth, and the model-tiering DoR still calls this section deferred while
#          describing D15a (one same-tier retry per rung) and a broad trigger set, BOTH of which the
#          reviewed charter overruled. A design of record that contradicts the harness is worse than no
#          document: the next reader re-litigates a settled question, or implements against the wrong one.
#
# DOCUMENTATION target: exempt from the committed .valid/.invalid sample pair (#468 - you cannot
#          synthesize a meaningful invalid sample of a design document), so the PRECEDENT check is the
#          mandatory substitute. Every literal demanded below points at a sibling precedent in the same
#          document, named beside its clause.
#
# HTML COMMENTS ARE STRIPPED FIRST (the doc-target rule). An <!-- ... --> renders as NOTHING, so a
#          required token surviving only inside one is invisible text: a two-token contract check has
#          been measured flipping exit 1 -> exit 0 on a single appended "<!-- TODO: document ... -->"
#          line. Fenced code blocks are NOT stripped - a fence RENDERS, and this SSOT documents its own
#          wire format inside fences, so stripping them would reject a correct document written in its
#          own house style.
#
# SECTION 7 IS THE SUBJECT of every DoR clause, extracted ONCE. The predecessor scanned the WHOLE
#          document for the citation while its message said "section 7 does not cite ...", and it
#          tested NOTHING about D15a or the trigger set. Measured: a workspace with section 7
#          BYTE-UNTOUCHED - still stating D15a, still listing action-failed and invalid-fragment as
#          triggers - exited 0 on a changed heading marker, a changed table row, and a citation
#          appended ~500 lines away at EOF. That is the reviewed charter's two overruled decisions
#          surviving a green gate, which is the exact failure this file's `catches:` line claims.
#
# POSITIVE-FIRST, because a bare forbid on 'same-tier' / 'action-failed' would false-RED a CORRECT
#          reconciliation. This document's house form keeps a superseded decision as a LIVE bullet
#          carrying its verdict in caps (grep RESOLVED - 6 hits), and '- **D15a - ...**' is a live
#          bullet today. So the clauses DEMAND the verdict; exactly ONE forbids anything, and it is
#          anchored on the rejected rule's operative clause over section 7 minus blockquotes and
#          strikethrough, so the document may quote what it superseded.
#
# Required-present baselines, MEASURED at authoring time against the exact subject each clause scans
#          (#478):
#            SSOT  `"escalated"`                                   = 0
#            SSOT  escalatedFrom                                   = 0
#            SSOT  escalation ladder (case-insensitive)            = 0
#            sec7  228-escalation-ladder.charter.md                = 0
#            sec7  **D15a ... (SUPERSEDED|OVERRULED|...|RESOLVED)  = 0   <- see the note at the clause
#            sec7  action-failed ... (no longer|does not escalate) = 0
#            sec7  guardrail-failed ... only                       = 1   <- DISCLOSED, not hidden
#          The last one arrives ALREADY SATISFIED: section 7's own "v2 open items" list proposes
#          "escalate only on `guardrail-failed`" as DA F5, which the charter then adopted. It is kept
#          as a FLOOR the rewrite must not fall through, and it is NOT what catches the untouched
#          section - the citation, D15a-verdict and action-failed clauses are (each measured 0).
#          Forbidden-present clauses are exempt from the baseline rule and are EXPECTED present now:
#            sec7  ^## 7. The escalation ladder (#228) [v2   = 1  (the heading this task must un-defer)
#            live  never before that rung has had one same-tier retry = 1  (D15a's operative clause)
#            DoR   ^|...**v2 (#228)**                        = 1  (the capability-table ROW)
#          All three forbidden patterns are LINE- or MARKUP-anchored on a USE, not a mention
#          (#470/#76): a superseding note that QUOTES the old heading, the old rule or the old table
#          marker inside a blockquote does not match, so the doc can say what it changed without
#          failing its own gate.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$ssotPath = Join-Path $ws 'docs/plans/02-schemas-and-contracts.md'
$dorPath  = Join-Path $ws 'docs/plans/17-model-tiering.md'

# PRECONDITION - the ONE legitimate early exit: without a subject every clause below would report a
# missing token, which is a confident wrong message about a file that is not there.
foreach ($p in @($ssotPath, $dorPath)) {
    if (-not (Test-Path -LiteralPath $p)) {
        Write-Output "PRECONDITION: $p is missing - this task's two subjects must both exist. Nothing below can be evaluated."
        exit 1
    }
}

function Get-RenderedText {
    param([string]$Path)
    $raw = Get-Content -Raw -LiteralPath $Path
    $stripped = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
    # Fail on a RESIDUAL unterminated '<!--' - never on a marker COUNT. Measured: this SSOT carries 4
    # '<!--' and 8 '-->', because '-->' is also a Mermaid arrow, so a balance check false-fires on a
    # correct document and no edit an agent is allowed to make could satisfy it. Stripping first and
    # asking what is LEFT is the doctrine's own form, and it is the only one that survives contact.
    if ($stripped -match '<!--') {
        return @{ Text = $null; Error = "an unterminated '<!--' survives the comment strip - refusing to continue, because stripping to end-of-file would delete the rest of the document over one stray token" }
    }
    return @{ Text = $stripped; Error = $null }
}

$failures = @()

$ssot = Get-RenderedText -Path $ssotPath
if ($ssot.Error) { $failures += "docs/plans/02-schemas-and-contracts.md: $($ssot.Error)" }

$dor = Get-RenderedText -Path $dorPath
if ($dor.Error) { $failures += "docs/plans/17-model-tiering.md: $($dor.Error)" }

# ── SSOT: required-present, over RENDERED text ────────────────────────────────────────────────────
if ($ssot.Text) {
    # PRECEDENT: the tierSource table already spells its tokens backticked-and-quoted - `"task"`,
    # `"plan-default"`, `"override"`. The new row is written the same way.
    if ($ssot.Text -cnotmatch '`"escalated"`') {
        $failures += 'the SSOT does not carry the tierSource token `"escalated"` - the journal now writes a fourth value the contract table does not list. Add the row beside `"task"` / `"plan-default"` / `"override"`, in the same backticked-and-quoted form those three use.'
    }
    # PRECEDENT: the provenance wire example already documents camelCase keys of exactly this shape -
    # `requestedModel`, `tierSource`, `baseCommit`.
    if ($ssot.Text -cnotmatch 'escalatedFrom') {
        $failures += 'the SSOT does not mention the provenance key escalatedFrom - run.json now writes it on an escalated attempt and the contract does not describe it. Document it beside tier / tierSource, the way requestedModel is documented.'
    }
    if ($ssot.Text -notmatch '(?i)escalation ladder') {
        $failures += "the SSOT never names the ESCALATION LADDER - the mechanism that produces the new tierSource value. A table row with no prose behind it leaves the trigger set (guardrail-failed only), the shared retry budget, and the escalated-vs-Climbed distinction undocumented."
    }
}

# ── DoR: SECTION 7 IS EXTRACTED ONCE, and every section-7 clause is scoped to it ──────────────────
# The whole-document scan was the hole: a workspace with section 7 BYTE-UNTOUCHED - still stating D15a
# and still listing action-failed / invalid-fragment as triggers - exited 0 on nothing but a changed
# heading marker, a changed table row, and a citation appended ~500 lines away at EOF. Scoping to the
# section is also what makes clause "does not cite" a TRUE sentence rather than a whole-file claim.
if ($dor.Text) {
    $sec7 = [regex]::Match($dor.Text, '(?ms)^## 7\. The escalation ladder.*?(?=^## |\z)').Value

    if ([string]::IsNullOrWhiteSpace($sec7)) {
        # NOT a fall-through to the clauses below: with no section extracted every one of them would
        # report a missing token, which is a confident wrong message about a section that was RENAMED.
        $failures += "docs/plans/17-model-tiering.md has no section whose heading starts '## 7. The escalation ladder' - this task reconciles that section, it does not rename or remove it. Restore the heading (dropping only its [v2 - deferred] marker) and record the supersessions inside it."
    }
    else {
        # Blockquote- and strikethrough-free view of section 7. The single negative clause below runs
        # over THIS, so the document may QUOTE the rule it is superseding (the form a supersession
        # note takes) without failing its own gate - a USE, not a mention (#470/#76).
        $live = ($sec7 -split "`n" | Where-Object { $_ -notmatch '^\s*>' -and $_ -notmatch '~~' }) -join "`n"

        # POSITIVE-FIRST. A bare forbid on 'same-tier' / 'action-failed' would false-RED a CORRECT
        # reconciliation: this document's house form keeps a superseded decision as a LIVE bullet
        # carrying its verdict in caps (grep RESOLVED - 6 hits at authoring time), and
        # '- **D15a - ...**' is a live bullet today. So what is DEMANDED is the verdict, not the
        # absence of the words.

        # PRECEDENT: this document already cites the review round that settled a decision - grep it for
        # RESOLVED (6 hits at authoring time) and for its "charter review" references.
        if ($sec7 -cnotmatch '228-escalation-ladder\.charter\.md') {
            $failures += "docs/plans/17-model-tiering.md section 7 does not cite docs/plans/228-escalation-ladder.charter.md - the reviewed plan of record that chose budget option A over D15a and narrowed the trigger set to guardrail-failed only. The citation must sit INSIDE section 7, where the decision it settles is written; appended elsewhere in the document it does not reach the reader who is re-litigating this section."
        }
        # The BUDGET verdict, asserted rather than assumed. D15a (one same-tier retry per rung) is
        # option B; the charter chose option A. A section that still states D15a and says nothing
        # about its status IS the design of record contradicting the harness.
        # ANCHORED ON THE BOLD DECISION LABEL '**D15a', not on any mention of the string. MEASURED:
        # the un-anchored form arrives ALREADY SATISFIED (baseline 1) - section 7's third D15a mention
        # ("D15a must not re-open ...") sits ~200 chars before the pre-existing "(D5 - the #201/#228
        # open question, RESOLVED)", a caps verdict about a DIFFERENT decision. Anchoring on the bold
        # label (the one form this document uses to NAME a decision - 1 occurrence today) takes the
        # baseline to 0 at both 400 and 600 chars, so the clause asserts the budget verdict instead of
        # collecting a neighbour's.
        if ($sec7 -cnotmatch '(?s)\*\*D15a.{0,400}(SUPERSEDED|OVERRULED|NO LONGER IN FORCE|RESOLVED)') {
            $failures += "docs/plans/17-model-tiering.md section 7 still states D15a (one same-tier retry per rung, the charter's option B) with no verdict recorded beside it. The maintainer chose option A - each guardrail failure climbs one rung, total attempts unchanged, no budget reset. Mark D15a in this document's own house form: a caps verdict within a few lines of the decision it overturns - SUPERSEDED, OVERRULED, NO LONGER IN FORCE or RESOLVED (grep RESOLVED for six worked examples). A superseded decision that is merely deleted is indistinguishable from one nobody noticed."
        }
        # The TRIGGER SET stated as CLOSED. Baseline 1 (see the header) - already satisfied today by
        # the DA-F5 open item's "escalate only on `guardrail-failed`", so this clause is a floor the
        # rewrite must not fall through, not the clause that catches the untouched section.
        if ($sec7 -cnotmatch '(?is)guardrail-failed.{0,200}\bonly\b|\bonly\b.{0,200}guardrail-failed') {
            $failures += "docs/plans/17-model-tiering.md section 7 never states the trigger set as CLOSED. It must say guardrail-failed ONLY - the word 'only' beside the outcome - because a reader who cannot see the set is closed will assume the neighbouring outcomes escalate too."
        }
        # ... and the outcomes that were REMOVED from it named as removed. action-failed is the one
        # the charter's own rationale singles out (DA F5: it conflates infrastructure faults with
        # capability), so it is the one the document has to answer for.
        if ($sec7 -cnotmatch '(?s)action-failed.{0,400}(no longer|do not escalate|does not escalate|SUPERSEDED)') {
            $failures += "docs/plans/17-model-tiering.md section 7 still lists action-failed among the escalation triggers without recording that it no longer escalates. Say so beside it, in one of the forms this check reads: 'no longer', 'do not escalate', 'does not escalate', or a caps SUPERSEDED. invalid-fragment, timeout, max-turns and output-cap do not escalate either - the charter narrowed the set to guardrail-failed alone."
        }
        # THE ONE NEGATIVE, and it is anchored on the rejected rule's OPERATIVE CLAUSE over $live, so
        # quoting D15a inside a blockquote (or striking it through) while superseding it stays legal.
        if ($live -cmatch '(?i)never before that rung has had one same-tier retry') {
            $failures += "docs/plans/17-model-tiering.md section 7 still asserts D15a's operative rule as live text - 'never before that rung has had one same-tier retry'. That is the charter's option B and it did not ship. Quote it inside a blockquote, or strike it through, if the supersession note needs to name what changed; do not leave it standing as a statement of the design."
        }
        # Line-anchored on the HEADING ITSELF (a USE), and [^\S\r\n]* is HORIZONTAL whitespace only:
        # \s+ spans newlines, so an un-deferred heading whose NEXT paragraph happened to open with
        # '[v2 ...' matched and false-REDded a correct document.
        if ($sec7 -cmatch '(?m)^## 7\. The escalation ladder \(#228\)[^\S\r\n]*\[v2') {
            $failures += "docs/plans/17-model-tiering.md still heads section 7 with the [v2 - deferred] marker, but the ladder has shipped. The design of record now contradicts the harness."
        }
    }

    # The capability table is near the TOP of the document, NOT inside section 7, so this clause stays
    # whole-document - anchored on a TABLE ROW (a line beginning '|'), so a supersession note that
    # quotes the old **v2 (#228)** marker in prose or a blockquote does not trip it.
    if ($dor.Text -cmatch '(?m)^\|.*\*\*v2 \(#228\)\*\*') {
        $failures += "docs/plans/17-model-tiering.md still marks the escalation-ladder row of its capability table **v2 (#228)**, but the ladder has shipped."
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== the design of record does not match what shipped: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
