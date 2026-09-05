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
# Required-present baselines, MEASURED at authoring time against the exact subject each clause scans
#          (#478) - all 0, so none of these clauses is satisfied before this task runs:
#            SSOT  `"escalated"`                       = 0
#            SSOT  escalatedFrom                       = 0
#            SSOT  escalation ladder (case-insensitive) = 0
#            DoR   228-escalation-ladder.charter.md    = 0
#          Forbidden-present clauses are exempt from that rule and are EXPECTED to be present now:
#            DoR   ^## 7. The escalation ladder (#228) [v2   = 1  (the heading this task must un-defer)
#            DoR   **v2 (#228)**                             = 1  (the capability-table row)
#          Both forbidden patterns are LINE- or MARKUP-anchored on a USE, not a mention (#470/#76): a
#          superseding note that QUOTES the old heading inside a blockquote does not match, so the doc
#          can say what it changed without failing its own gate.
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

# ── DoR: required-present (the citation) and forbidden-present (the two deferral markers) ─────────
if ($dor.Text) {
    # PRECEDENT: this document already cites the review round that settled a decision - grep it for
    # RESOLVED (6 hits at authoring time) and for its "charter review" references.
    if ($dor.Text -cnotmatch '228-escalation-ladder\.charter\.md') {
        $failures += "docs/plans/17-model-tiering.md section 7 does not cite docs/plans/228-escalation-ladder.charter.md - the reviewed plan of record that chose budget option A over D15a and narrowed the trigger set to guardrail-failed only. Without the citation the next reader re-litigates a settled question."
    }
    # Line-anchored on the HEADING ITSELF (a USE), so a superseding note quoting the old marker inside
    # a blockquote or a list does not trip it.
    if ($dor.Text -cmatch '(?m)^## 7\. The escalation ladder \(#228\)\s+\[v2') {
        $failures += "docs/plans/17-model-tiering.md still heads section 7 with the [v2 - deferred] marker, but the ladder has shipped. The design of record now contradicts the harness."
    }
    if ($dor.Text -cmatch '\*\*v2 \(#228\)\*\*') {
        $failures += "docs/plans/17-model-tiering.md still marks the escalation-ladder row of its capability table **v2 (#228)**, but the ladder has shipped."
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== the design of record does not match what shipped: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
