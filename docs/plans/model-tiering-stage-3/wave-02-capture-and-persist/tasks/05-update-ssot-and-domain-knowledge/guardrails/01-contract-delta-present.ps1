# catches: the contract shipping in code and NOT in the document that is supposed to be its single source
#          of truth - the exact drift #349 exists to close, reproduced one level up. Every clause here is
#          a source-shape check over PROSE, which is the last rung of the demotion order and is used only
#          because the property genuinely has no runtime proxy: nothing executes a markdown file, so no
#          test can assert what the SSOT says.
#
#          DOCUMENTATION deliverables are exempt from the two-sided sample pair (#468) - you cannot
#          synthesize a meaningful invalid sample of a design doc - so the PRECEDENT check is the
#          mandatory substitute, and every literal token below is a token these files ALREADY use in the
#          same position:
#            - `"requestedModel"` as a quoted JSON key in a `"provenance": { ... }` example block, beside
#              the existing `"tierSource"` / `"effort"` entries in that same block;
#            - `requestedModel` as a bare identifier in DoR 9.3, which already names `resolvedModel`,
#              `runner`, `kind`, `tier` and `tierSource` bare in prose;
#            - `requestedModel` in the domain-knowledge skill's model-tiering section, which already
#              names schema members bare.
#          No clause pins a sentence, an adjective, or a heading a human might legitimately word
#          differently.
#
# MEASURED BASELINE 2026-08-23 against the wave-2 entry tree, case-sensitively, per subject:
#   requestedModel in 02-schemas-and-contracts.md ......... 0
#   requestedModel in 17-model-tiering.md ................. 0
#   requestedModel in guardrails-domain-knowledge/SKILL.md  0
#   "resolvedModel": (quoted JSON key) anywhere in scope ... 0   <- forbidden; 0 on arrival is correct
$ErrorActionPreference = 'Continue'
$failures = @()

$ssot  = 'docs/plans/02-schemas-and-contracts.md'
$dor   = 'docs/plans/17-model-tiering.md'
$skill = '.claude/skills/guardrails-domain-knowledge/SKILL.md'

foreach ($f in @($ssot, $dor, $skill)) {
    if (-not (Test-Path $f -PathType Leaf)) {
        # PRECONDITION: a missing subject makes every clause below it crash on a null read.
        Write-Output "$f does not exist - this task's own subject is missing from the tree"
        exit 1
    }
}

$ssotText  = Get-Content -Raw -Path $ssot
$dorText   = Get-Content -Raw -Path $dor
$skillText = Get-Content -Raw -Path $skill

# --- 1. the SSOT: the key, and it is IN the provenance example block ----------------------------
if ($ssotText -notmatch '"requestedModel"\s*:') {
    $failures += "$ssot has no `"requestedModel`" key - the field this wave added to AttemptProvenance is absent from the schema that documents it"
}
else {
    # Placement, not just presence: a key documented somewhere else in a 6,000-line file is not
    # documented in the provenance block a reader goes to. Bounded proximity to the sibling entry
    # `"tierSource"`, both orders, multiline-dotall.
    if ($ssotText -notmatch '(?s)"requestedModel"[\s\S]{0,1200}"tierSource"' -and
        $ssotText -notmatch '(?s)"tierSource"[\s\S]{0,1200}"requestedModel"') {
        $failures += "$ssot documents `"requestedModel`" but not within ~1200 chars of `"tierSource`" - it is outside the `"provenance`" example block, where a reader looking up the provenance shape will not find it"
    }
}

# --- 2. the DoR 9.3 amendment -------------------------------------------------------------------
if ($dorText -notmatch 'requestedModel') {
    $failures += "$dor never mentions requestedModel - 9.3 still describes the shape the charter review superseded"
}
elseif ($dorText -notmatch '(?s)###\s*9\.3[\s\S]{0,6000}requestedModel') {
    $failures += "$dor mentions requestedModel but not inside section 9.3 - the amendment must land in the section that stated the superseded shape, not beside it"
}

# --- 3. the domain-knowledge skill ---------------------------------------------------------------
if ($skillText -notmatch 'requestedModel') {
    $failures += "$skill never mentions requestedModel - the skill's own frontmatter makes it SELF-UPDATING when a contract moves, and provenance.model changing meaning is exactly that"
}

# --- 4. the negative assertion: the refused key stays refused ------------------------------------
# Anchored on a KEY USE (a quoted JSON key followed by a colon) and on a C# property DECLARATION -
# never the bare word. All three documents legitimately DISCUSS resolvedModel to explain why it does
# not exist, and a bare ban would red exactly the prose that records the decision (#470).
foreach ($pair in @(@($ssot, $ssotText), @($dor, $dorText), @($skill, $skillText))) {
    if ($pair[1] -match '"resolvedModel"\s*:') {
        $failures += "$($pair[0]) declares a `"resolvedModel`" JSON key. The settled contract refuses it: one field per fact, plus a second field only for the disagreement. Describing why it does not exist is fine; documenting it as a key is not"
    }
}
foreach ($src in @('src/Guardrails.Core/Journal/JournalModel.cs')) {
    if (Test-Path $src -PathType Leaf) {
        $code = Get-Content -Raw -Path $src
        # Strip line comments and XML doc comments before scanning: JournalModel.cs's own doc comment
        # explains WHY there is no resolvedModel, and that mention must not red this clause.
        $stripped = ($code -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
        if ($stripped -match 'string\?\s+ResolvedModel\b') {
            $failures += "$src declares a ResolvedModel property - the settled contract refuses it"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== provenance contract delta: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The code shipped in tasks 02/04. These three documents are what a reader consults instead of the code, and a contract that lives only in the implementation is the drift this wave exists to close."
    exit 1
}
exit 0
