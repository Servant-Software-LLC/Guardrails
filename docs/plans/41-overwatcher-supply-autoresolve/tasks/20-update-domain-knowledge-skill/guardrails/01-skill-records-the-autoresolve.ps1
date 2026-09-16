# catches: the domain model moving without the document every AGENT reads. This capability only fires when
#          a halting agent names the missing file by its exact workspace-relative path, so a skill that
#          does not say so leaves the feature silently never firing - the agent writes a vague question and
#          nothing is ever auto-resolved. The SSOT is the contract; THIS file is where an agent learns it.
#          (a) COMMENT STRIP. Every required-present clause reads comment-stripped text: an HTML comment
#              renders as NOTHING, so a token appearing only inside one is invisible text and must not
#              satisfy a clause (measured elsewhere: a two-token contract check flipped from exit 1 to
#              exit 0 when a single <!-- TODO: ... --> line was appended). A bare lazy (?s)<!--.*?--> is
#              itself wrong on a doc that MENTIONS <!-- in prose - measured on
#              .claude/skills/plan-breakdown/SKILL.md, the lazy form removes 169,334 bytes, 44.8% of the
#              file, in ONE span - so fenced blocks and inline code spans are MASKED first, then unmasked.
#          (b) FENCES ARE NOT STRIPPED: a fence RENDERS, so a token documented in a usage fence is
#              legitimate house style, not evasion.
#          (c) WHITESPACE IS FLATTENED before matching, because this file is hard-wrapped and a multi-word
#              token can straddle a line break. MEASURED on the sibling SSOT: 'or the overwatcher' scores 0
#              raw and 1 flattened, because the line wraps between 'or the' and 'overwatcher'. Matching
#              multi-word tokens against unflattened text false-REDS a correct edit over a line break.
#          Multi-clause, so it ACCUMULATES into $failures and dumps ONCE. The only early exits are
#          PRECONDITIONS: the subject missing, or an unterminated '<!--' that makes the strip untrustworthy.
#
#          MEASURED BASELINES - 2026-09-16, branch design/712-overwatcher-supply-autoresolve at 27545904,
#          using THIS script's own strip and flatten, case-sensitive, against .claude/skills/guardrails-domain-knowledge/SKILL.md. Zero is
#          the expected answer for a required-present clause; a NONZERO count means the CLAUSE is wrong,
#          not this comment:
#            'Supplied-By: overwatcher' 0 | workspace-relative 0 | missing-resource 0 | #712 0 |
#            dial-near-critical regex 0 | sentences(blocked-work AND one of
#            overwatcher|missing-resource|Supplied-By) 0
#
#          PRE-SATISFIED TOKENS, DELIBERATELY NOT PINNED BARE - already present, so a bare clause would be
#          green on arrival and certify nothing:
#            'blocked-work' 1 - it already appears in the needsHuman-kind bullet ('blocked-work' |
#              'defective-guardrail'), so it is read ONLY inside the sentence clause below;
#            'needsHuman' 7, 'plan branch' 28, 'auto-resolve' 5, 'overwatcher' 5, 'dial' 3.
#
#          PRECEDENT CHECK - the mandatory substitute for the committed .valid/.invalid sample pair, which
#          a DOCUMENTATION deliverable is exempt from (you cannot synthesize a meaningful invalid skill
#          doc). Every literal below is spelled the way this file already spells the analogous thing:
#            Supplied-By: overwatcher <- this file's own trailer precedent, the 'Refreshed-From:' trailer
#                                        in the refreshed[] bullet, written Name: value; and the SSOT's
#                                        derived 'Supplied-By: <by>'
#            workspace-relative       <- the SSOT's 'the workspace-relative paths that landed' (section 7).
#                                        NOTE why the file's own 'the WORKSPACE path the file must have'
#                                        is NOT accepted as an alternation: it is already present, so
#                                        accepting it would make this clause pre-satisfied and toothless.
#                                        The new bullet must state the RELATIVE form, which is the precise
#                                        fact that makes the auto-resolve fire (./name for a root file).
#            missing-resource         <- this file's own hyphenated-token style: hook-rejected,
#                                        trial-gate-failed, partially-delivered, blocked-work
#            #712                     <- this file cites issues bare: #525, #373, #269
#            dial + critical          <- MEASURED, this file spells the dial three ways ('the #361 dial',
#                                        'autonomy-dial value') and contains the word 'critical' ZERO
#                                        times, while design 40 writes 'dial:critical' 4 times. Demanding
#                                        the literal 'dial:critical' would therefore dictate vocabulary
#                                        the target artifact has never used, so the clause accepts ANY
#                                        phrasing that puts the two words near each other, in either
#                                        order: dial:critical, 'dial: critical', 'the #361 dial at
#                                        critical', 'at the critical dial'. Both forms are legitimate, so
#                                        the clause accepts both rather than picking one.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = '.claude/skills/guardrails-domain-knowledge/SKILL.md'

if (-not (Test-Path -LiteralPath $subject)) {
    # PRECONDITION: every clause below would crash on a missing subject.
    Write-Output "PRECONDITION: $subject does not exist."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $subject

# Inside a fenced block or an inline code span, '<!--' is literal text a reader SEES - Markdown renders it
# - so it cannot open a comment. Mask those regions, strip, then UNMASK, which also preserves fence content
# and so honours the counter-rule that a token documented in a usage fence legitimately satisfies a clause.
$maskOpen  = [string][char]0x01
$maskClose = [string][char]0x02
$masked = [regex]::Replace($raw, '(?s)```.*?```|`[^`\r\n]*`', {
    param($m) $m.Value.Replace('<!--', $maskOpen).Replace('-->', $maskClose)
})

# Belt and braces: after masking, an opener with no closer really is unterminated.
if ($masked -match '<!--(?![\s\S]*?-->)') {
    # PRECONDITION: the comment strip cannot be bounded, so no clause below can be trusted.
    Write-Output "PRECONDITION: $subject contains an unterminated '<!--' outside any code span. Refusing to strip to EOF, which would delete the rest of the document over one stray token. Close the comment."
    exit 1
}

$doc = ([regex]::Replace($masked, '(?s)<!--.*?-->', '')).Replace($maskOpen, '<!--').Replace($maskClose, '-->')

# Hard wraps collapsed: a multi-word token must not be missed because it straddles a line break.
$flat = $doc -replace '\s+', ' '
$sentences = [regex]::Split($flat, '(?<=[.!?])\s+')

$failures = @()

$clauses = @(
    @{ token = 'Supplied-By: overwatcher'; regex = $false
       why   = 'the skill does not say the harness commits the file onto the plan branch as an overwatcher supply. An agent that does not know the commit is attributed reads the new file as another task work, which is exactly the confusion the by field exists to prevent' }
    @{ token = 'workspace-relative'; regex = $false
       why   = 'the skill does not say the halt question must name the missing file by its exact workspace-relative path. This is THE fact that makes the auto-resolve fire: a vague question is not matched, nothing is supplied, and the feature silently never runs. The file existing phrase about the WORKSPACE path describes the supply verb argument, not the halt question, and cannot carry this' }
    @{ token = 'missing-resource'; regex = $false
       why   = 'the trigger is not named, so an agent reading a run overwatch records cannot connect them to the halt it wrote' }
    @{ token = '#712'; regex = $false
       why   = 'the issue that introduced the capability is not cited, so a reader cannot reach the design of record from the skill' }
    @{ token = 'dial[^.]{0,40}critical|critical[^.]{0,40}dial'; regex = $true
       why   = 'the skill does not say the auto-resolve engages only at the critical dial. Without the condition an agent either expects it at every dial and stops naming paths carefully, or never expects it at all. ANY phrasing that puts dial and critical in the same clause satisfies this - dial:critical, the #361 dial at critical, at the critical dial - so this never dictates one spelling' }
)
foreach ($c in $clauses) {
    $pattern = if ($c.regex) { $c.token } else { [regex]::Escape($c.token) }
    if ($flat -cnotmatch $pattern) {
        $failures += "MISSING '$($c.token)' in ${subject} - $($c.why)"
    }
}

# A SENTENCE clause, not a bare token: blocked-work already scores 1 in this file (the needsHuman-kind
# bullet), so proximity is what proves the new bullet states the CLASSIFICATION rule rather than merely
# re-listing the kinds. The auto-resolve is refused outright on a defective-guardrail kind, so an agent
# that misclassifies the halt loses it. MEASURED 0 sentences.
$classification = @($sentences | Where-Object { $_ -cmatch 'blocked-work' -and $_ -cmatch 'overwatcher|missing-resource|Supplied-By' })
if ($classification.Count -eq 0) {
    $failures += "MISSING the halt-classification rule in ${subject} - no sentence ties the blocked-work kind to the missing-resource auto-resolve. The auto-resolve is refused outright when the halt kind is defective-guardrail, so an agent that classifies a missing file as a defective guardrail silently loses the auto-resolve. Say, in one sentence, that the halt must be classified blocked-work for the overwatcher to supply the file"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) contract clause(s) failed in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
