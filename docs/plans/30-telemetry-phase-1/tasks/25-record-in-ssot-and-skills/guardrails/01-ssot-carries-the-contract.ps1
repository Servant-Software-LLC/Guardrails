# catches: a contract change that shipped without its contract. Ten tasks have now changed what the
#          harness records, what the corpus row carries and what the telemetry verb can answer, and the
#          SSOT still describes the Phase 0 shape. The specific losses this check exists to prevent, in
#          the order they would hurt:
#            1. the thirteen new row columns undocumented, so the next reader has to reverse-engineer
#               the corpus schema out of TelemetryIngest.cs - which is the exact failure section 15
#               exists to prevent, one build later;
#            2. the SCHEMA VERSION BUMP unrecorded. This is the worst of the ten because it is silent:
#               the constant's own doc says to bump it whenever a field is added, and a corpus whose
#               contract does not say version 2 exists lets a later analysis pool two row shapes under
#               one number and never notice;
#            3. the JOURNAL GRAIN unrecorded - which member rides the task entry, which rides the
#               provenance, which rides the attempt record. A field list does not carry it, and it is
#               the fact that decides whether a datum reaches the worktree settle at all;
#            4. the ERA BOUNDARY unstated, which turns section 3.2's DECIDED paragraph into folklore:
#               the pre-fix era gets a documented boundary, not a backfill and not a re-baseline, and
#               the option deliberately ruled out is letting an analysis silently mix the two eras;
#            5. `telemetry census` and #577 dropped. That pairing is load-bearing: without it a future
#               reader meets the census's own number and reads it as a bug this plan failed to close,
#               rather than as the scoping measurement section 3.3a asked for.
#
# DOCUMENTATION TARGET - EXEMPT from the two-sided sample pair, and the exemption is NAMED rather than
#          taken silently (#468/#302): you cannot synthesize a meaningful INVALID sample of a design
#          document. The mandatory substitute is the PRECEDENT check - every literal token demanded
#          below has a sibling precedent in THIS SAME document, so the guardrail asks the document to
#          keep speaking its own language rather than to adopt this plan's:
#            camelCase wire keys        - `costUsd` 8, `tierSource` 11, `inputTokens` 3,
#                                         `outputTokens` 3, `definitionHash` 22 occurrences today. The
#                                         digest / warmth / duration / memory clauses are matched
#                                         case-INSENSITIVELY because BOTH forms are legitimate here:
#                                         this document names wire keys in camelCase AND their C#
#                                         members in PascalCase, and demanding one spelling would be
#                                         demanding a house style the document does not have.
#            PascalCase type names      - `AttemptRecord` 5, `AttemptProvenance` 2, `TelemetryRow` 3.
#                                         `AttemptSegments` is matched case-SENSITIVELY because it names
#                                         a TYPE, which has exactly one spelling (the wire key for it is
#                                         `segments`, a different token this clause does not accept).
#            literal wire VALUES        - `guardrail-failed` 19, `undifferentiated` 1. `code+tests` is
#                                         matched case-SENSITIVELY for the same reason: it is the string
#                                         the harness writes, not a phrase.
#            the verb form              - `telemetry ingest`, `telemetry report`, `telemetry purge`, one
#                                         occurrence each. `telemetry census` is the fourth sibling.
#            issue references           - `#556` 6, `#535` 2, `#533` 2. Naming the issue that decided a
#                                         rule is this document's established convention.
#            `boundary`                 - 79 occurrences (the document's own word, in "boundary call").
#                                         The era clause accepts either compound the plan itself uses.
#            `schemaVersion`            - 1 occurrence. The version clause asks for the NUMBER stated
#                                         beside the document's own existing word, in either order, on
#                                         one line - not for a sentence.
#
# HTML COMMENTS ARE STRIPPED BEFORE MATCHING, and over a DOC that is the load-bearing direction. The
#          comment-blind rule (#97/#98) is written for SOURCE, where the failure is a false RED; over a
#          document the same blindness runs the other way and yields a false GREEN, because a clause
#          over a doc is almost always required-present. An HTML COMMENT RENDERS AS NOTHING: invisible
#          text, not thin prose, so a single `<!-- TODO: document the census verb here -->` would
#          otherwise discharge a clause whose whole purpose is that the reader can SEE the answer. This
#          file carries 4 comment openers today, all real and all terminated.
#          FENCED CODE BLOCKS ARE NOT STRIPPED, deliberately: a fence RENDERS, and this document carries
#          43,387 bytes of fenced content across 26 blocks - much of section 7's wire example among them
#          - so a fence-stripping clause would reject a correct document written in its own style.
#
# MEASURED BASELINES on master @d87c766, against docs/plans/02-schemas-and-contracts.md, with each
#          clause's own case sensitivity (#478). Measured on the COMMENT-STRIPPED text, which is what
#          the clauses below match:
#            (?i)modelDigest                 0   this task's deliverable
#            (?i)routeWarm                   0   this task's deliverable
#            (?i)guardrailMs                 0   this task's deliverable
#            (?i)totalMemoryBytes            0   this task's deliverable
#            AttemptSegments                 0   this task's deliverable (case-SENSITIVE: a type name)
#            code\+tests                     0   this task's deliverable (case-SENSITIVE: a wire value)
#            schema version .. 2             0   this task's deliverable (case-INSENSITIVE: prose)
#            telemetry census                0   this task's deliverable (case-INSENSITIVE)
#            era boundary | boundary date    0   this task's deliverable (case-INSENSITIVE: prose)
#            #577                            0   this task's deliverable
#          EVERY ROW IS ZERO, and that is worth stating rather than leaving implicit: there are NO
#          retention clauses in this file - nothing here is pre-satisfied, so every failure below is a
#          real gap and not an artifact of a rewrite deleting text that was already there. A reviewer
#          re-measuring any row and getting nonzero should change the CLAUSE, not this comment.
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
# would delete the rest of a ~7,000-line document over one stray token and turn every clause below into
# a false red with no diagnosis.
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', ' ')
if ($doc -match '<!--') {
    Write-Output "PRECONDITION: $rel contains an UNTERMINATED HTML comment opener. Everything after it renders as nothing, so no clause below can be trusted. Close the comment."
    exit 1
}

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: the new row columns and journal members are NAMED -------------------------------------
# Case-INSENSITIVE on purpose (see the PRECEDENT block): this document names the wire key in camelCase
# AND the C# member in PascalCase, and both are legitimate spellings of the same contract.
$required = [ordered]@{
    'modelDigest' = "the model digest column and journal member (section 3.3, DECIDED in full - schema field AND capture). Without it the SSOT cannot say what stops a re-quantized model under a stable tag pooling with its predecessor, which is the one thing section 15.5's 'two model fingerprints never pool' rule now depends on. Record the provider reality with it: a Claude row's digest is permanently null because the CLI exposes no fingerprint, and an openai-compat row carries one only where the engine volunteers system_fingerprint - a null there is a provider fact, not a defect for a later reader to hunt."
    'routeWarm'   = "the route-warmth flag (section 3.4). It is nullable for a reason the contract has to state: null means no route resolved at all (a script action), because 'not applicable' is not 'cold', and a reader who does not know that will read the nulls as cold attempts and compute a warm/cold split that is wrong in exactly the direction that looks fine."
    'guardrailMs' = "the segmented durations on the attempt (section 3.4). The action time and the guardrail time are two different numbers and the contract has to say so; a single elapsed figure cannot answer whether a slow attempt was the model or the gate."
    'totalMemoryBytes' = "the run environment's memory column (section 3.4). This is the unified-memory comparison the maintainer confirmed on 2026-09-01: the 64GB Mac Studio is a TIGHTER box than the 128GB MacBook, so the same model name runs at a different quantization on each and must not be pooled as one sample. A corpus that records the model name and not the box it ran on cannot make that distinction."
}
foreach ($token in $required.Keys) {
    if ($doc -notmatch ('(?i)' + [regex]::Escape($token))) {
        $failures += "$rel does not mention '$token' (in either the camelCase wire spelling or the PascalCase member spelling - both are accepted). $($required[$token])"
    }
}

# --- REQUIRED: the new record TYPE, case-SENSITIVE (a type has one spelling) -------------------------
if ($doc -cnotmatch 'AttemptSegments') {
    $failures += "$rel does not name AttemptSegments. This is the new journal record that carries the action and guardrail durations, and naming the TYPE is what lets the new subsection say which grain it rides - it hangs off AttemptRecord, so unlike the provenance members it needs a carrier on PendingAttempt to reach the worktree settle. That asymmetry is the whole reason JournalModel.cs documents the trap in place; a field list that does not name the type cannot express it."
}

# --- REQUIRED: the schema version actually says the row shape changed --------------------------------
# Case-INSENSITIVE and matched as PROXIMITY, in either order, on one line: the clause asks for the NUMBER
# stated beside the document's own existing word, never for a sentence of this plan's devising.
if ($doc -notmatch '(?i)(schema\s*version\b[^.\n]{0,80}\b2\b|\b2\b[^.\n]{0,40}schema\s*version)') {
    $failures += "$rel does not state that the corpus row's schema version is now 2. TelemetryRow.CurrentSchemaVersion's own doc comment says to bump it whenever a field is added, and thirteen columns were added - so a contract that still implies version 1 lets a later analysis pool two row shapes under one number and never notice. This is the quietest of the losses this guardrail covers: nothing fails, the table just becomes wrong."
}

# --- REQUIRED: the bucket VALUES, verbatim, case-SENSITIVE (they are wire values, not phrases) -------
if ($doc -cnotmatch 'code\+tests') {
    $failures += "$rel does not name the 'code+tests' bucket. Section 15.5 already stratifies on a fingerprint bucket that has, until now, had exactly one value - '(unbucketed)'. The six real values are the contract; 'code+tests' is the one that proves the list is the harness's and not a paraphrase, because it is the shape section 3.2 measured at 67 of 74 multi-root tasks and named rather than filing under 'other'. Keep the report legend's rule with them: a bucket is a fact about a task, never one read off its name."
}

# --- REQUIRED: the census verb, and WHOSE issue the fix is -------------------------------------------
if ($doc -notmatch '(?i)telemetry\s+census') {
    $failures += "$rel does not document the 'telemetry census' verb. It is the fourth sibling of ingest/report/purge, each of which section 15 documents, and it is the only one that reads PLAN FOLDERS rather than the corpus - a distinction a reader cannot guess and will get wrong, because every other telemetry verb takes a --corpus-root."
}
if ($doc -cnotmatch '#577') {
    $failures += "$rel does not cite #577. Section 3.3a DECIDED that Phase 1 owns the CENSUS ONLY and that the fix is #577's own issue, and that sentence is doing real work in the contract: without it a future reader meets the census's own number - the fraction of unattributed rows that is a genuine recording gap - and reads it as a bug this plan failed to close. This document's own convention is to name the issue that decided a rule (#556 appears 6 times, #535 and #533 twice each)."
}

# --- REQUIRED: the pre-fix era boundary is written down, not folklore --------------------------------
if ($doc -notmatch '(?i)(era\s+boundary|boundary\s+date)') {
    $failures += "$rel does not record the pre-fix era boundary. Section 3.2's DECIDED paragraph chose a documented boundary date over a backfill (unbounded work against unknown yield) and over a re-baseline (discarding real spend history to fix an attribution problem) - and the option it deliberately ruled out is letting an analysis silently mix the pre-fix and post-fix eras, which is precisely the flattering-numbers failure this plan exists to prevent. A boundary that is filtered on in code but stated nowhere is exactly that silent mix one build later."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== SSOT contract: $($failures.Count) of the section 15 edits are missing from $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Locate every anchor by its TEXT, never by a line number: this document is ~7,000 lines and moves. Every clause above is matched as a token or a phrase, never as a sentence - write each fact in this document's own voice, and do not reword surrounding text to match a pattern."
    exit 1
}
Write-Output "SSOT carries the contract: the digest, route warmth, the segmented durations, the run environment, the AttemptSegments record, the schema-version bump, the bucket values, the census verb, #577's ownership of the fix, and the era boundary."
exit 0
