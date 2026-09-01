# catches: a contract change that shipped without its contract. Invariant 4 says the SSOT edit lands in the
#          SAME change-set as the code, and fifteen stages have now changed what the harness records and
#          delivers. The specific losses this check exists to prevent, in the order they would hurt:
#            1. the section 7 wire comment still saying the hash is "stamped at this task's most recent
#               successful settle" - which is the DEFECT, restated as the contract, in the document every
#               future reader trusts;
#            2. the section 7.2 boundary call still documenting the partial-liveness hazard as ACCEPTED,
#               which would leave the SSOT contradicting the harness;
#            3. the new in-run divergence-gate subsection missing, so an operator meeting an exit-2
#               divergence halt has nowhere to read what it means;
#            4. section 14 item 7 quietly dropped - the ONE reachable exception to "never the current
#               on-disk bytes" (the [a] drift-accept branch). Section 14 puts it in the contract precisely
#               so it is not folklore, and it is the easiest of the eight to skip because it is a sentence
#               appended to an edit that was already made.
#
# DOCUMENTATION TARGET - EXEMPT from the two-sided sample pair, and the exemption is NAMED rather than
#          taken silently (#468/#302): you cannot synthesize a meaningful INVALID sample of a design
#          document. The mandatory substitute is the PRECEDENT check - every literal token demanded below
#          has a sibling precedent in this same document, so the guardrail asks the document to keep
#          speaking its own language rather than to adopt this plan's:
#            'definitionHash'            - 18 occurrences today; the field's own spelling.
#            'definitionHashAtSettle'    - the sibling naming convention (camelCase wire key beside its
#                                          camelCase neighbour), the shape section 7's example already uses
#                                          for every optional key.
#            'DefinitionHashAtLoad'      - PascalCase because it names a C# MEMBER, which is how section 7.2
#                                          already refers to TaskDefinitionHash and PlanDefinitionHash.
#            '#556'                      - 2 occurrences today; issue references are this document's
#                                          established way of naming a decision's origin.
#
# HTML COMMENTS ARE STRIPPED BEFORE MATCHING, and over a DOC that is the load-bearing direction. The
#          comment-blind rule (#97/#98) is written for SOURCE, where the failure is a false RED; over a
#          document the same blindness runs the other way and yields a false GREEN, because a clause over a
#          doc is almost always required-present. Measured elsewhere: a two-token contract check over this
#          exact file went from exit 1 to exit 0 when a single '<!-- TODO: document ... here -->' line was
#          appended - its stated purpose discharged by a commented-out TODO. An HTML comment RENDERS AS
#          NOTHING: invisible text, not thin prose. This file carries 4 comment openers today, all real.
#          FENCED CODE BLOCKS ARE NOT STRIPPED: a fence RENDERS, and section 14's edits 1, 5 and 7 land
#          INSIDE jsonc fences, so stripping fences would delete the very text this check demands.
#
# MEASURED BASELINES on design/32-executed-definition-hash @4a308ab, against
#          docs/plans/02-schemas-and-contracts.md, with each clause's own case sensitivity (#478). Run
#          against the RAW file; every hit below was then checked for where it lives, because this
#          guardrail matches COMMENT-STRIPPED text and a hit inside an HTML comment would not count:
#            definitionHashAtSettle          0   this stage's deliverable (case-SENSITIVE)
#            DefinitionHashAtLoad            0   this stage's deliverable (case-SENSITIVE)
#            ExecutedDefinitionDivergence    0   this stage's deliverable (case-SENSITIVE)
#            RecordDriftAccepted             0   this stage's deliverable - section 14 item 7's ONE
#                                                reachable exception. Zero today is exactly why the item
#                                                is easy to skip: nothing in the document currently hints
#                                                that the exception exists.
#            reads recompute from disk       0   this stage's deliverable (case-INSENSITIVE: prose)
#            partially LIVE                  1   EXPECTED nonzero - a RETENTION clause, not a delivery.
#                                                Section 14 item 2 REPLACES that bullet in its entirety
#                                                and the replacement KEEPS the phrase, so this clause
#                                                asserts the boundary call survived the rewrite rather
#                                                than being deleted with the accepted-limitation text it
#                                                used to introduce. Green before and after, by design.
#            #556                            2   EXPECTED nonzero - the same shape. Both occurrences are
#                                                plan 31's own boundary calls naming this issue as the
#                                                fix; the clause asserts the citation is still there
#                                                after a rewrite that touches the paragraphs around it.
#          THE TWO NONZERO ROWS ARE THE ONLY RETENTION CLAUSES HERE, and they are named as such rather
#          than left to look like ordinary required-present clauses that happen to be pre-satisfied. A
#          reviewer re-measuring them should get 1 and 2, not 0.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = 'docs/plans/02-schemas-and-contracts.md'
$full = Join-Path $ws $rel

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is the schema SSOT and this task edits it in place."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $full

# Strip HTML comments, and FAIL on a residual unterminated opener rather than stripping to EOF - which
# would delete the rest of a 6,900-line document over one stray token and turn every clause below into a
# false red with no diagnosis.
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', ' ')
if ($doc -match '<!--') {
    Write-Output "PRECONDITION: $rel contains an UNTERMINATED HTML comment opener. Everything after it renders as nothing, so no clause below can be trusted. Close the comment."
    exit 1
}

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: the new wire keys and members are named ---------------------------------------------
# -cmatch: these are identifiers with a fixed casing convention, and a case-insensitive require-present
# clause false-GREENS on a spelling the code would never produce (taxonomy 3).
$required = [ordered]@{
    'definitionHashAtSettle' = "section 14 item 1's new OPTIONAL wire key. It is the durable record of an executed-definition divergence, and section 6.3 pins its trigger: 'Its presence is driven by the GATE VERDICT, never by hash inequality.' An SSOT that does not name it leaves the only machine-readable signal of the whole mechanism undocumented."
    'DefinitionHashAtLoad'   = "section 14 items 1 and 2's member name. The contract is that the stamped hash is the LOAD-TIME one, computed eagerly at TaskNode construction, and every WRITE site stamps it - the journal entry, the Guardrails-Task-Hash trailer, and via WaveDefinitionHash the wave record. Naming the member is what makes the rule checkable by the next reader."
    'ExecutedDefinitionDivergence' = "section 14 item 4's report record. It is a TERM OF RunReport.AllSucceeded, so delivery does not fire, the run is not reported green, and the CLI exits 2 - the single most consequential behaviour change in this plan, and the one an operator meets first."
}
foreach ($token in $required.Keys) {
    if ($doc -cnotmatch [regex]::Escape($token)) {
        $failures += "$rel does not mention '$token'. $($required[$token])"
    }
}

# --- REQUIRED: the RULE, in the document's own words ------------------------------------------------
# Case-INSENSITIVE here, deliberately: this is prose, not an identifier, and section 14 item 2 renders it
# in bold within a blockquote. The clause asks for the SENTENCE, not for one word of it.
if ($doc -notmatch '(?i)reads\s+recompute\s+from\s+disk') {
    $failures += "$rel does not carry the rule 'reads recompute from disk; writes read the pin' (section 14 item 2). That one sentence is the whole taxonomy: it is what tells a future author which of the twelve call sites they may touch, and section 4.3 records that an earlier draft of this plan got the split WRONG - three sites listed as reads were durable writes - which is exactly the mistake the rule exists to prevent."
}
if ($doc -notmatch '(?i)partially\s+LIVE') {
    $failures += "$rel no longer describes the plan folder as only partially LIVE during a run (section 14 item 2 replaces that bullet IN ITS ENTIRETY, and the replacement keeps the phrase). The two liveness classes are the asymmetry the whole defect lives in: task.json and the DAG are held from load, while the action file and the guardrail scripts are re-read per attempt."
}

# --- REQUIRED: the ONE exception is in the contract, not in folklore --------------------------------
if ($doc -cnotmatch 'RecordDriftAccepted') {
    $failures += "$rel does not name RunJournal.RecordDriftAccepted (section 14 item 7). Item 1's comment says the recorded value is 'the bytes the attempt EXECUTED, never the current on-disk bytes' - and that is FALSE in exactly one reachable case: the operator's [a] drift-accept overwrites it with a current-disk value without re-running the task. Section 4.2 calls this the write site 'nobody had counted', missed by both the first draft and the first adversarial pass because it calls no hash function at all. An exception left out of the contract is an exception nobody knows about."
}

# --- REQUIRED: the origin is cited ------------------------------------------------------------------
if ($doc -cnotmatch '#556') {
    $failures += "$rel does not cite #556. This is a section 7.2 CONTRACT CHANGE - the plan's opening line says shipping one unreviewed is how a contract change goes unreviewed - and the document's own convention is to name the issue that decided a rule (this token appears twice today, from plan 31's own boundary calls)."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== SSOT contract: $($failures.Count) of section 14's edits are missing from $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Section 14 carries the verbatim replacement text for all seven SSOT edits. Locate every anchor by its TEXT, never by a line number: this document is ~6,900 lines and a concurrent change was in flight when the plan was written, so every line reference past section 12 has moved."
    exit 1
}
Write-Output "SSOT carries the contract: both new names, the report record, the reads-recompute rule, the partial-liveness boundary call, the drift-accept exception, and the issue citation."
exit 0
