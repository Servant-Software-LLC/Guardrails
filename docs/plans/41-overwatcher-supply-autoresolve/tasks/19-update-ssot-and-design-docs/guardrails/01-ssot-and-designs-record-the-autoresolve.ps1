# catches: the design-41 contract landing in CODE while the three documents that define it stay silent -
#          and the markdown-specific ways such a check goes wrong.
#          (a) COMMENT STRIP. Every required-present clause reads comment-stripped text. An HTML comment
#              renders as NOTHING, so a token appearing only inside one is invisible text: measured
#              elsewhere, a two-token contract check flipped from exit 1 to exit 0 when a single
#              <!-- TODO: ... --> line was appended. A bare lazy (?s)<!--.*?--> is itself wrong on a doc
#              that MENTIONS <!-- in prose (the mention opens a match running to the next real -->), so
#              fenced blocks and inline code spans are MASKED first, then unmasked.
#          (b) FENCES ARE NOT STRIPPED. A fence RENDERS, so a token documented in a usage fence is
#              legitimate house style, not evasion - measured on this SSOT: 43,387 bytes of fenced content
#              across 26 blocks, and 2 of its 36 PlanDefinition occurrences live inside one. Stripping
#              fences would reject a correct document written in its own style.
#          (c) WHITESPACE IS FLATTENED before matching. These documents are hard-wrapped, so a multi-word
#              token can straddle a line break. MEASURED on this SSOT: the literal 'or the overwatcher'
#              scores 0 against the raw file and 1 against the flattened one, because section 1 wraps
#              between 'or the' and 'overwatcher'. Matching multi-word tokens against unflattened text
#              false-REDS a correct edit over a line break.
#          Multi-clause, so it ACCUMULATES into $failures and dumps ONCE. The only early exits are
#          PRECONDITIONS: a subject missing, or an unterminated '<!--' that makes the strip untrustworthy
#          (refusing to strip to EOF, which would delete the rest of the document over one stray token).
#
#          MEASURED BASELINES - 2026-09-16, branch design/712-overwatcher-supply-autoresolve at 27545904,
#          using THIS script's own strip and flatten, case-sensitive. Zero is the expected answer for a required-present clause; a NONZERO
#          count means the CLAUSE is wrong, not this comment.
#            docs/plans/02-schemas-and-contracts.md
#              auto-supplied 0 | 9.2.2 0 | missing-resource 0 | (?<!flight-)resource-supply 0 |
#              'Supplied-By: overwatcher' 0 | '[supplied] by' 0 | OverwatchSupplyAutoResolve 0 |
#              MissingResourceSignal 0 | sentences(missing-resource AND exception) 0
#            docs/plans/40-in-flight-resource-supply.md
#              41-overwatcher-supply-autoresolve 0 | #712 0
#            docs/plans/12-autonomous-mode.md
#              sentences(auto-supplied AND mergeOnSuccess) 0
#          The section-1 clause is the exception, and deliberately so: it is a DELETION check, which
#          appending cannot satisfy. 'or the overwatcher' measures 1 TODAY and must reach 0.
#
#          PRE-SATISFIED TOKENS, DELIBERATELY NOT PINNED BARE - each is already in its subject, so a bare
#          clause would be green on arrival and certify nothing for the life of the plan:
#            'resource-supply' 6 in the SSOT - EVERY hit is the filename 40-in-flight-resource-supply.md,
#              which is exactly why that clause carries the (?<!flight-) lookbehind;
#            'advisory' 42, 'observed' 16, 'auto-resolve' 16, 'no-verdict' 3 in the SSOT - so the two other
#              new decision values (advisory, observed) are NOT pinned as bare tokens;
#            'mergeOnSuccess' 10 in 12-autonomous-mode.md - read only inside a sentence clause.
#
#          PRECEDENT CHECK - the mandatory substitute for the committed .valid/.invalid sample pair, which
#          a DOCUMENTATION deliverable is exempt from (you cannot synthesize a meaningful invalid design
#          doc). Every literal below is spelled the way its own target already spells the analogous thing:
#            auto-supplied            <- the sibling decision tokens auto-applied / proceeded-best-guess /
#                                        no-verdict (SSOT sections 2.1 and 7)
#            9.2.2                    <- the '#### 9.2.1' heading and its 'section 9.2.1' cross-references
#            missing-resource         <- permission-wall, a hyphenated lowercase trigger in the SAME
#                                        trigger list (SSOT section 9.2)
#            resource-supply          <- the fixes[] op shape { kind, authority, target? } (section 8).
#                                        The lookbehind is the only constraint on its form, so
#                                        "resource-supply", `resource-supply` and kind: resource-supply
#                                        are all accepted - the clause never dictates one spelling.
#            Supplied-By: overwatcher <- the derived trailer 'Supplied-By: <by>' (section 7)
#            [supplied] by            <- the existing console line
#                                        '[supplied] N resource(s) committed <commit>: <paths>' (8.1)
#            OverwatchSupplyAutoResolve / MissingResourceSignal
#                                     <- OverwatchFixClassifier, NeedsHumanTriage, UnauthoredContentNote
#            41-overwatcher-supply-autoresolve
#                                     <- design-of-record references by filename stem. Pinning the STEM
#                                        accepts BOTH the committed '...-autoresolve.md' and design 41's
#                                        own '...-autoresolve.charter.md' spelling, rather than dictating
#                                        one where both are legitimate.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$ssotPath = 'docs/plans/02-schemas-and-contracts.md'
$d40Path  = 'docs/plans/40-in-flight-resource-supply.md'
$d12Path  = 'docs/plans/12-autonomous-mode.md'

foreach ($p in @($ssotPath, $d40Path, $d12Path)) {
    if (-not (Test-Path -LiteralPath $p)) {
        # PRECONDITION: every clause below would crash on a missing subject.
        Write-Output "PRECONDITION: $p does not exist."
        exit 1
    }
}

# Returns the RENDERED, whitespace-flattened text of a markdown file: HTML comments removed (they render
# as nothing), fenced blocks and inline code spans preserved (they render), hard wraps collapsed.
# Returns $null when an unterminated '<!--' makes the strip untrustworthy.
function Get-RenderedFlat([string]$path) {
    $raw = Get-Content -Raw -LiteralPath $path
    $masked = [regex]::Replace($raw, '(?s)```.*?```|`[^`\r\n]*`', {
        param($m) $m.Value.Replace('<!--', [string][char]0x01).Replace('-->', [string][char]0x02)
    })
    if ($masked -match '<!--(?![\s\S]*?-->)') { return $null }
    $doc = ([regex]::Replace($masked, '(?s)<!--.*?-->', '')).Replace([string][char]0x01, '<!--').Replace([string][char]0x02, '-->')
    return ($doc -replace '\s+', ' ')
}

$ssot = Get-RenderedFlat $ssotPath
$d40  = Get-RenderedFlat $d40Path
$d12  = Get-RenderedFlat $d12Path

foreach ($pair in @(@($ssotPath, $ssot), @($d40Path, $d40), @($d12Path, $d12))) {
    if ($null -eq $pair[1]) {
        # PRECONDITION: the comment strip cannot be bounded, so no clause below can be trusted.
        Write-Output "PRECONDITION: $($pair[0]) contains an unterminated '<!--' outside any code span. Refusing to strip to EOF, which would delete the rest of the document over one stray token. Close the comment."
        exit 1
    }
}

function Get-Sentences([string]$flat) {
    return [regex]::Split($flat, '(?<=[.!?])\s+')
}

$failures = @()

# ---------------------------------------------------------------------------------------------------
# docs/plans/02-schemas-and-contracts.md - the SSOT
# ---------------------------------------------------------------------------------------------------
$ssotClauses = @(
    @{ token = 'auto-supplied'; regex = $false
       why   = 'the new task-boundary decision value is not recorded, so nothing in the contract says a certified auto-resolve holds delivery the way proceeded-best-guess does (sections 2.1, 7, 8, 9.2.2 and both delivery-interlock token sets)' }
    @{ token = '9.2.2'; regex = $false
       why   = 'the new subsection is neither written nor cross-referenced, so the one overwatcher action that changes the run base has no home in the contract' }
    @{ token = 'missing-resource'; regex = $false
       why   = 'the new overwatch trigger is not named, so overwatch.jsonl gains a trigger value the schema document never lists' }
    @{ token = '(?<!flight-)resource-supply'; regex = $true
       why   = 'the fixes[] op kind is not recorded. NOTE the lookbehind: a bare resource-supply already scores 6 in this file and every one of those hits is the FILENAME 40-in-flight-resource-supply.md, so the clause requires the token somewhere OTHER than that filename. Any spelling of the op itself satisfies it' }
    @{ token = 'Supplied-By: overwatcher'; regex = $false
       why   = 'the trailer the auto-resolve commit carries is not recorded, so section 7 derives Supplied-By: <by> without ever stating the value the overwatcher supply writes' }
    @{ token = '[supplied] by'; regex = $false
       why   = 'the section 8.1 console line still reads [supplied] N resource(s) ... with no supplier. A human watching a run cannot tell an operator drain from an overwatcher auto-supply, which is the whole point of adding by to the event' }
    @{ token = 'OverwatchSupplyAutoResolve'; regex = $false
       why   = 'the deterministic certifier is not named, so the contract states a prompt may propose without naming the gate that certifies - the invariant section 9.2.2 exists to carry' }
    @{ token = 'MissingResourceSignal'; regex = $false
       why   = 'the Tier-1 detector shared with the halt text is not named, so nothing says WHAT decides a halt is shaped like a missing resource' }
)
foreach ($c in $ssotClauses) {
    $pattern = if ($c.regex) { $c.token } else { [regex]::Escape($c.token) }
    if ($ssot -cnotmatch $pattern) {
        $failures += "MISSING '$($c.token)' in ${ssotPath} - $($c.why)"
    }
}

# DELETION clause (section 1). The staging tree is no longer a hand-off point for the overwatcher: the
# auto-resolve commits the file it was certified to supply directly and never stages. The hunk REMOVES
# ', or the overwatcher, section 3.6' from the parenthetical naming who hands off. Appending cannot
# satisfy this, which is what makes it the strongest clause here. MEASURED 1 today.
if ($ssot -cmatch 'or the overwatcher') {
    $failures += "SECTION 1 STILL NAMES THE OVERWATCHER AS A STAGER in ${ssotPath} - the hand-off parenthetical for logs/<runId>/supplied/ still reads 'or the overwatcher'. The design-41 hunk DELETES that item: the missing-resource auto-resolve commits directly and never stages, so a file an operator staged is never drained under by: overwatcher. Adding the new sentence without removing these words leaves the document asserting both"
}

# The section 9.2 hunk, as MEANING rather than as a bare token: one sentence that names the new consult AND
# marks it as the exception to 'never fires when the agent itself emitted needsHuman'. The token
# missing-resource alone would be satisfied by the section 8 or 9.2.2 edit, so this clause is what proves
# the 9.2 hunk itself landed. MEASURED 0 sentences.
$exceptionSentence = @(Get-Sentences $ssot | Where-Object { $_ -cmatch 'missing-resource' -and $_ -match '(?i)exception' })
if ($exceptionSentence.Count -eq 0) {
    $failures += "MISSING the section 9.2 exception in ${ssotPath} - no single sentence says the missing-resource consult is the ONE exception to the rule that the overwatcher never fires when the agent itself emitted needsHuman. Without it the contract reads as forbidding exactly the consult design 41 wires, and the next reader resolves the contradiction against the code"
}

# ---------------------------------------------------------------------------------------------------
# docs/plans/40-in-flight-resource-supply.md - the design whose gate this wires
# ---------------------------------------------------------------------------------------------------
$d40Clauses = @(
    @{ token = '41-overwatcher-supply-autoresolve'
       why   = 'section 3 does not point at the design that wires it. Plan 40 shipped this decision as a gate nothing called; a reader landing on that ruling must be able to follow it forward. Either filename spelling satisfies this - the committed ...-autoresolve.md or design 41 own ...-autoresolve.charter.md' }
    @{ token = '#712'
       why   = 'section 3 does not carry the issue that wired it, so the decision and its implementation stay unlinked in the issue tracker' }
)
foreach ($c in $d40Clauses) {
    if ($d40 -cnotmatch [regex]::Escape($c.token)) {
        $failures += "MISSING '$($c.token)' in ${d40Path} - $($c.why)"
    }
}

# ---------------------------------------------------------------------------------------------------
# docs/plans/12-autonomous-mode.md - the suppression set
# ---------------------------------------------------------------------------------------------------
# A SENTENCE clause, not a bare token: mergeOnSuccess already scores 10 in this file, so proximity is what
# proves auto-supplied landed in the section 11 delivery bullet rather than anywhere in the document.
# MEASURED 0 sentences.
$suppression = @(Get-Sentences $d12 | Where-Object { $_ -cmatch 'auto-supplied' -and $_ -cmatch 'mergeOnSuccess' })
if ($suppression.Count -eq 0) {
    $failures += "MISSING auto-supplied from the suppression set in ${d12Path} - no sentence names auto-supplied alongside mergeOnSuccess. Section 11 lists the decisions that default mergeOnSuccess to OFF; a run that auto-supplied a file and did not record it here delivers machine-judged work to the user branch under a rule that says it should not"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) contract clause(s) failed across the design-41 plan documents ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
