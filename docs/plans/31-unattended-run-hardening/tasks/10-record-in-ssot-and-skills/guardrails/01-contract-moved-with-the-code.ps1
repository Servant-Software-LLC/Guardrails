# catches: an SSOT and a skill that stayed silent while the code moved underneath them - invariant 4
#          broken. Three contract changes landed in stages 2, 3, 5 and 8: salvage now fires on the
#          escalation path, two new validate diagnostics exist, and decisions[] gained two tokens. A
#          reader who opens the SSOT and finds none of them will build the NEXT change on a contract
#          that is a version behind, which is the specific failure invariant 4 exists to prevent.
#
#          It also catches the cheapest wrong implementation of a documentation task: appending a
#          TODO. An HTML comment renders as NOTHING - invisible text, not thin prose - so a clause
#          over a document that did not strip comments would go from exit 1 to exit 0 on a single
#          appended `<!-- TODO: document GR2068 here -->` line, discharging its own stated purpose.
#          Measured elsewhere on this exact SSOT. Every clause below therefore reads
#          COMMENT-STRIPPED text, and an unterminated `<!--` is a hard failure rather than a strip to
#          EOF (which would delete the rest of a 5,000-line document over one stray token).
#
# FENCED CODE BLOCKS ARE NOT STRIPPED, and that is deliberate: a fence RENDERS, so a token documented
#          inside a usage fence is legitimate house style. Measured on this SSOT: 26 fenced blocks
#          carrying 43,387 bytes, and 2 of its 36 `PlanDefinition` occurrences live inside one - a
#          fence-stripping clause would reject a correct document written in its own style.
#
# DOCUMENTATION EXEMPTION FROM THE SAMPLE PAIR, named rather than silent (#468): these are prose
#          targets, and no meaningful INVALID sample of a design document can be synthesized - an
#          author can always write the tokens and mean nothing by them. The compensating control is
#          the PRECEDENT CHECK: every literal demanded below is quoted from a form the target
#          document ALREADY uses, so a correct author writing in the document's own voice satisfies it
#          without contorting anything. Each clause names its precedent.
#
# MEASURED BASELINES on master @1490d2a, against the exact subject each clause scans (#478) - every
#          required token is 0, which is the correct shape for a task that has not run:
#            SSOT: GR2068 0 | GR2069 0 | HandoffPathUnreachable 0 | HandoffRowSplitAcrossTasks 0 |
#                  plan-edit 0 | LivePlanEditWatch 0 | restrictToScope 0
#            domain-knowledge SKILL.md: GR2068 0 | GR2069 0 | plan-edit 0
#            architect agent: GR2068 0 | GR2069 0   (NOTE `filesTouched` already measures 2 there, so
#                  it is deliberately NOT one of the required tokens - a clause on it would be
#                  satisfied on arrival and would certify nothing.)
#          The SSOT carries 4 HTML comments today and the SKILL.md 2, which is why the strip is not
#          hypothetical.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# ACCUMULATE (#478): one distinguishable message per clause, dumped once. A documentation task's
# feedback is the only signal its author gets, so every message names the file and the remedy.
$failures = @()

function Get-RenderedText([string]$rel) {
    $full = Join-Path $ws $rel
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return $null }
    $raw = Get-Content -Raw -LiteralPath $full          # NEVER matched against, never reassigned

    # Strip PAIRED comments non-greedily, THEN look for a residual opener. Do NOT count-balance
    # '<!--' against '-->': measured on this very SSOT, it carries 4 openers and 8 '-->' occurrences,
    # because '-->' is also a Mermaid edge arrow in the diagram fences. A balance check therefore
    # false-reds the real document and skips every clause below it - which is a guardrail that reports
    # "unterminated comment" on a perfectly well-formed file and dead-ends the task. The residual test
    # is what the doctrine actually asks for, and it is the one that cannot be fooled by prose.
    $stripped = [regex]::Replace($raw, '(?s)<!--.*?-->', ' ')
    if ($stripped -match '<!--') {
        return @{ Error = "carries an UNTERMINATED '<!--' (an opener with no matching '-->' after it). Close it: this guardrail refuses to strip to EOF, which would silently hide the rest of the document from every clause below." }
    }
    return @{ Text = $stripped }
}

$targets = @(
    @{
        Rel      = 'docs/plans/02-schemas-and-contracts.md'
        Required = @(
            @{ Token = 'GR2068'
               Why   = 'section 12 item 7 adds a section 9.6 row for it. Precedent in this same table: the shipped `GR2067` row.' },
            @{ Token = 'GR2069'
               Why   = 'section 12 item 7 adds a second section 9.6 row. It is GR2069, not GR2068, that catches BOTH plan-28 failures, so a document naming only one of them names the wrong one.' },
            @{ Token = 'HandoffPathUnreachable'
               Why   = 'the section 9.6 row names the constant beside its code, exactly as the `GR2067` row names `OpenAiCompatWeakOrUnreachable`.' },
            @{ Token = 'HandoffRowSplitAcrossTasks'
               Why   = 'same - the second row names its constant.' },
            @{ Token = 'plan-edit'
               Why   = 'section 12 item 5 adds it to section 7 decisions[] as a boundary token alongside drift / wave / task. Precedent: the existing tokens are documented there as backticked literals.' },
            @{ Token = 'restrictToScope'
               Why   = 'section 12 item 1 records that the escalation path filters the staged set to the task writeScope - the divergence that keeps an out-of-scope edit out of a durable, agent-readable patch. A section 3.2 that does not name the filter has not recorded the change.' },
            @{ Token = 'LivePlanEditWatch'
               Why   = 'section 12 item 6(b) records where the watch polls and which five harness writers re-baseline it plan-wide. Naming the type is how a reader finds it.' }
        )
    },
    @{
        Rel      = '.claude/skills/guardrails-domain-knowledge/SKILL.md'
        Required = @(
            @{ Token = 'GR2068'
               Why   = 'section 12 item 10 - a new validate diagnostic exists, and this skill is where an agent looks for the diagnostic quick-reference.' },
            @{ Token = 'GR2069'
               Why   = 'the second of the pair, and the one that carries all of #553 motivating value.' },
            @{ Token = 'plan-edit'
               Why   = 'the new decisions[] boundary token. An agent reading a run.json needs to recognise it here without opening the SSOT.' }
        )
    },
    @{
        Rel      = '.claude/agents/guardrails-architect.md'
        Required = @(
            @{ Token = 'GR2068'
               Why   = 'section 12 item 11 - the architect writes the handoff table, so the architect is who needs to know which code fires on a stale path.' },
            @{ Token = 'GR2069'
               Why   = 'and which fires on a row split across tasks. NOTE `filesTouched` is deliberately NOT required here: it already appears twice in this file, so a clause on it would be satisfied on arrival and certify nothing (#478).' }
        )
    }
)

foreach ($t in $targets) {
    $doc = Get-RenderedText $t.Rel
    if ($null -eq $doc) {
        $failures += "PRECONDITION: $($t.Rel) does not exist."
        continue
    }
    if ($doc.ContainsKey('Error')) {
        $failures += "$($t.Rel) $($doc.Error)"
        continue
    }
    foreach ($r in $t.Required) {
        # -cmatch: these are code identifiers and diagnostic codes, case-SENSITIVE. A
        # case-insensitive clause would accept 'gr2068' in prose.
        if ($doc.Text -cnotmatch [regex]::Escape($r.Token)) {
            $failures += "$($t.Rel) does not mention '$($r.Token)' outside an HTML comment. $($r.Why)"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== contract not moved with the code: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Invariant 4: the contract moves in the SAME change-set as the code it describes. An HTML comment does NOT count - it renders as nothing, so an HTML-comment TODO satisfies no clause here. Write the two '.claude/' files with ONE needsHarnessWrite request carrying an ARRAY of two edits entries; a direct write to '.claude/' is refused unconditionally."
    exit 1
}
Write-Output "Contract moved: the SSOT, the domain-knowledge skill and the architect agent all name the changes that landed."
exit 0
