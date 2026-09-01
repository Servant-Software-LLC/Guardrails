# catches: the SSOT updated and the skill left describing the Phase 0 corpus. That matters more than it
#          sounds: guardrails-domain-knowledge is what an AGENT reads INSTEAD of the ~7,000-line SSOT,
#          so a stale skill is not a documentation lag - it is the operative description for every
#          future agent working in this repo. Its telemetry section currently ends with "Phases 1-3 of
#          the epic ... are open under #533 -- do not describe them as shipped", which after this plan
#          is an instruction to be WRONG about Phase 1.
#
#          Four specific losses, in the order they would hurt an agent:
#            1. the ROW SHAPE - an agent that does not know schemaVersion is 2 will read a mixed corpus
#               as one population, which is the survivorship failure of section 2 repeated at the
#               schema level;
#            2. the BUCKET VALUES - the whole point of Phase 1 is that like-work-to-like-work is now
#               expressible, and an agent that cannot name the six buckets cannot use it;
#            3. the DIGEST and ROUTE WARMTH - and in particular that a Claude row's digest is
#               permanently null by provider fact. An agent that does not know this will file it as a
#               bug, or worse, "fix" it by fabricating a value;
#            4. `telemetry census` and #577 - an agent that meets the census's number without knowing
#               Phase 1 owns the census ONLY will treat closing the attribution gap as in-scope work
#               this plan left unfinished, and #577 is exactly the issue that says it is not.
#
# DOCUMENTATION TARGET - EXEMPT from the two-sided sample pair, and the exemption is NAMED rather than
#          taken silently (#468/#302): there is no meaningful INVALID sample of a skill document. The
#          mandatory substitute is the PRECEDENT check, and every token below has a sibling precedent in
#          THIS SAME file:
#            camelCase wire keys   - `costUsd` 2, `inputTokens` 1, `outputTokens` 1, `definitionHash` 5,
#                                    `schemaVersion` 1 occurrence today. The digest / warmth / duration
#                                    clauses are matched case-INSENSITIVELY because BOTH forms are
#                                    legitimate: this file names wire keys in camelCase and harness
#                                    members in PascalCase, so demanding one spelling would demand a
#                                    house style the file does not have.
#            literal wire VALUES   - `guardrail-failed` 3, `undifferentiated` 1. `code+tests` is matched
#                                    case-SENSITIVELY for the same reason: it is the string the harness
#                                    writes, not a phrase to be capitalized at the start of a bullet.
#            the verb form         - `telemetry ingest`, `telemetry report`, `telemetry purge`, one
#                                    occurrence each, all in this file's own telemetry section.
#            issue references      - `#533` 2, `#535` 1, `#556` 1.
#          Every prose clause is matched case-INSENSITIVELY and as a PHRASE rather than a sentence,
#          because this file has its own voice and the task asks for the FACTS, not for wording: a
#          clause that demanded a sentence would be asking the document to speak the plan's language
#          instead of its own, which is exactly what the harness contract tells every agent not to do.
#
# HTML COMMENTS ARE STRIPPED BEFORE MATCHING (#97/#98 inverted for a doc target): over SOURCE the
#          comment-blind failure is a false RED; over a DOCUMENT it is a false GREEN, because a clause
#          over a doc is almost always required-present. An HTML comment renders as NOTHING - invisible
#          text, not thin prose - so a `<!-- TODO: write up the census verb -->` would otherwise
#          discharge a clause whose whole purpose is that an agent can READ the answer. This file
#          carries 2 comment openers today, both terminated. FENCED CODE BLOCKS ARE NOT STRIPPED: a
#          fence renders, this file documents wire shapes inside fences as house style, and stripping
#          them would reject a correct document written the way this one already is.
#
# MEASURED BASELINES on master @d87c766, against .claude/skills/guardrails-domain-knowledge/SKILL.md,
#          with each clause's own case sensitivity (#478). Measured on the COMMENT-STRIPPED text:
#            (?i)modelDigest                 0   this task's deliverable
#            (?i)routeWarm                   0   this task's deliverable
#            (?i)actionMs|guardrailMs|
#              segmented durations           0   this task's deliverable (any of the three accepted)
#            code\+tests                     0   this task's deliverable (case-SENSITIVE: a wire value)
#            schema version .. 2             0   this task's deliverable (case-INSENSITIVE: prose)
#            telemetry census                0   this task's deliverable (case-INSENSITIVE)
#            #577                            0   this task's deliverable
#          EVERY ROW IS ZERO, and that is worth stating rather than leaving implicit: this file already
#          carries a telemetry section with `schemaVersion`, `costUsd` and the null-is-not-zero rule in
#          it, so it LOOKS current - and says nothing at all about any Phase 1 fact. There are no
#          retention clauses here; nothing is pre-satisfied, so every failure below is a real gap.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = '.claude/skills/guardrails-domain-knowledge/SKILL.md'
$full = Join-Path $ws $rel

# PRECONDITION - the one legitimate early exit.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is the skill an agent reads instead of the SSOT, and this task updates it in place. If the harness write did not land, re-emit the needsHarnessWrite request - a Claude Code subprocess cannot write under .claude/ directly."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $full

# Strip HTML comments; FAIL on a residual unterminated opener rather than stripping to EOF, which would
# delete the rest of the document over one stray token and turn every clause below into a false red.
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', ' ')
if ($doc -match '<!--') {
    Write-Output "PRECONDITION: $rel contains an UNTERMINATED HTML comment opener. Everything after it renders as nothing, so no clause below can be trusted. Close the comment."
    exit 1
}

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: the new attempt facts, either spelling ------------------------------------------------
if ($doc -notmatch '(?i)modelDigest') {
    $failures += "$rel does not name the model digest (modelDigest / ModelDigest - either spelling is accepted). An agent reading only this file must know two things about it: it is what stops a re-quantized model under a stable tag pooling with its predecessor, and a Claude row's digest is PERMANENTLY NULL because the CLI stream carries a model tag and no fingerprint. The second half is the one that matters operationally - without it the next agent files the nulls as a defect, or fabricates a value to make them go away."
}
if ($doc -notmatch '(?i)routeWarm') {
    $failures += "$rel does not name route warmth (routeWarm / RouteWarm - either spelling is accepted). It is nullable, and the null is the load-bearing part: null means no route resolved at all (a script action), because 'not applicable' is not 'cold'. An agent that reads null as cold computes a warm/cold split that is wrong in the direction that looks fine."
}
if ($doc -notmatch '(?i)(actionMs|guardrailMs|segmented\s+durations?)') {
    $failures += "$rel does not describe the attempt's segmented durations (actionMs / guardrailMs, or the phrase 'segmented durations' - any of the three is accepted). The action time and the guardrail time are two different numbers: a single elapsed figure cannot answer whether a slow attempt was the model or the gate. Record them under the same null-is-not-zero rule this file already states for cost and tokens - a runner that reported nothing must not make the corpus assert the attempt took no time."
}

# --- REQUIRED: the bucket vocabulary, verbatim, case-SENSITIVE (a wire value, not a phrase) ----------
if ($doc -cnotmatch 'code\+tests') {
    $failures += "$rel does not name the 'code+tests' bucket. The six values (test-authoring, implementation, structural, code+tests, documentation, no-write) ARE the Phase 1 deliverable that makes like-work-to-like-work expressible, and an agent that cannot name them cannot use the corpus for the comparison it now supports. Keep the rule with them: the bucket is computed from the task's write surface and guardrail archetypes, NEVER from its name."
}

# --- REQUIRED: the row shape says it changed ---------------------------------------------------------
# Proximity, either order, on one line - the number beside this file's own existing word, not a sentence.
if ($doc -notmatch '(?i)(schema\s*version\b[^.\n]{0,80}\b2\b|\b2\b[^.\n]{0,40}schema\s*version)') {
    $failures += "$rel does not say the corpus row's schema version is now 2. This file already tells an agent that every row carries schemaVersion; after Phase 1 that is true and useless, because it does not say which shapes exist. An agent querying the corpus across the bump will pool two row shapes into one population - the survivorship failure of section 2, repeated one level down at the schema."
}

# --- REQUIRED: the census verb, and whose issue the FIX is -------------------------------------------
if ($doc -notmatch '(?i)telemetry\s+census') {
    $failures += "$rel does not mention the 'telemetry census' verb. It is the fourth sibling of the three this file already lists (ingest, report, purge) and the only one that reads PLAN FOLDERS rather than the corpus - a distinction an agent cannot guess, because every other telemetry verb takes a --corpus-root."
}
if ($doc -cnotmatch '#577') {
    $failures += "$rel does not cite #577. Section 3.3a DECIDED that Phase 1 owns the CENSUS ONLY and that the fix for the model-attribution gap is #577's own issue. This is the fact most likely to cost a future agent real work: meeting the census's number without it, an agent treats closing the gap as unfinished business from this plan and starts writing provenance code nobody asked for. This file's convention is to name the issue that decided a rule (#533 twice, #535 and #556 once each)."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== domain-knowledge skill: $($failures.Count) of the Phase-1 facts are missing from $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Affected section only, and CITE the SSOT rather than restating it. Every clause above is matched as a token or a phrase, never as a sentence: write each fact in this file's own voice. Remember this file cannot be written directly - emit a needsHarnessWrite request with an 'edits' array."
    exit 1
}
Write-Output "Domain-knowledge skill carries the Phase-1 semantics: the digest, route warmth, the segmented durations, the bucket values, the schema-version bump, the census verb, and #577's ownership of the fix."
exit 0
