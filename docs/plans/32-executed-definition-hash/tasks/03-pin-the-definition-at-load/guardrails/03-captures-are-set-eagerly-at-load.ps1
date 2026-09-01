# catches: the OTHER half of the same defect - a capture whose SHAPE is a correct bodiless auto-property
#          (guardrail 02 is happy) but which the loader never actually fills, or fills through a fallback
#          to disk. Three concrete wrong implementations, all of which compile and none of which any
#          behavioural pin in this plan can see:
#            1. the properties are declared and the loader never assigns them - every write site then
#               stamps NULL, SSOT section 7.2 reads a null recorded hash as "unknown, assume unchanged",
#               and the plan ships having silenced definition drift rather than fixed it. Guardrail 04's
#               shipped suites do not notice: they seed their hashes by hand;
#            2. the assignment carries a `?? TaskDefinitionHash.Compute(task)` tail - section 5.2 calls
#               this "the cheapest wrong implementation of this entire plan", because it reads like
#               defensive coding, passes every behavioural pin, and restores the defect for any node the
#               loader did not build;
#            3. the map is folded over some OTHER enumeration than TaskDefinitionFiles.Enumerate, which
#               breaks section 5.3's closing rule: the two surfaces may disagree about WHEN, they may
#               never disagree about WHAT DEFINES A TASK.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): case 2 is the one that
#          decides it. A `??` fallback is behaviourally IDENTICAL to the correct implementation for every
#          node the loader built - which in production is every node there is. The only input that
#          discriminates is a hand-constructed node, which exists only in tests, and no test in this plan
#          may construct one and drive a write site (section 11 keeps tests/** out of the implementation
#          stages). The property is genuinely unobservable at runtime, so this is the demotion order's
#          last rung, and it ships with a committed .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. REQUIRED clauses
#          read $code (comments gone, literals intact); FORBIDDEN clauses read $scan (literals gone too).
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, against src/Guardrails.Core/Loading/
#          PlanLoader.cs, with each clause's own case sensitivity (#478):
#            new TaskNode                       1   the single construction site the whole design rests
#                                                   on (section 5.2). This clause is an EQUALITY, not a
#                                                   floor: a second construction site would be a second
#                                                   place to forget the pin, and the argument that "there
#                                                   is no re-pin hook list to maintain" would stop being
#                                                   structural.
#            DefinitionHashAtLoad               0   this task's deliverable
#            DefinitionFilesAtLoad              0   this task's deliverable
#            TaskDefinitionFiles                0   this task's deliverable
#            Lazy<                              0   forbidden-present
#            .DS_Store / Thumbs.db / .swp /
#            .orig / .rej                       0   forbidden-present, one clause each. This stage is
#                                                   where the pressure to inline a second copy of the
#                                                   ignore list is highest, because the predicate that
#                                                   would do the filtering is still private here.
#            with { ... Directory = / Action =  0   forbidden-present. The two existing clones (:949
#                                                   DependsOn, :952 Tasks) rebind neither, which is what
#                                                   makes section 5.2's "both captures ride through"
#                                                   argument hold.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the sample path
# arrives as argv[0] AND in $env:GR_SUBJECT, ABSOLUTE. Joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/03-pin-the-definition-at-load/samples/03-captures-are-set-eagerly-at-load.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/03-pin-the-definition-at-load/samples/03-captures-are-set-eagerly-at-load.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Loading/PlanLoader.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is a SHIPPED file this task edits in place; guardrail 01 would have failed first if it were merely broken."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: still exactly ONE construction site -------------------------------------------------
# -cmatch / case-sensitive counting throughout: C# identifiers are case-SENSITIVE and PowerShell -match
# is not, so a case-insensitive require-present clause false-GREENS on text C# would never compile.
$ctors = [regex]::Matches($code, 'new\s+TaskNode\b').Count
if ($ctors -ne 1) {
    $failures += "src has $ctors 'new TaskNode' expression(s) in $rel; the design rests on there being exactly ONE. Section 5.2: because the loader is the only constructor, the pin's lifetime is the TaskNode's lifetime BY CONSTRUCTION - there is no re-pin hook list to maintain and no way to forget one. A second construction site is a second place to forget it, and turns a structural argument into a checklist."
}

# --- REQUIRED: the aggregate is assigned EAGERLY, from the hasher, at load --------------------------
# The assignment and the Compute call must be in the same expression. This is the ONE place in src where
# DefinitionHashAtLoad and Compute( legitimately meet; everywhere else that pairing is the fallback the
# next clause bans and stage 6's committed anchor test pins repo-wide.
if ($code -cnotmatch 'DefinitionHashAtLoad\s*=\s*[^;]*TaskDefinitionHash\s*\.\s*Compute\s*\(') {
    $failures += "$rel never assigns DefinitionHashAtLoad from TaskDefinitionHash.Compute(...). Declaring the property and leaving the loader silent makes every write site stamp NULL - and SSOT section 7.2 reads a null recorded hash as 'unknown, assume unchanged', so the plan would ship having SILENCED definition drift rather than fixed it. Section 5.2 shows the shape: build the node, then 'return node with { DefinitionHashAtLoad = TaskDefinitionHash.Compute(node), ... }' - Compute needs a fully-built node (it reads Directory and the RESOLVED Action.Path), which is why it cannot sit inside the object initializer."
}

# --- REQUIRED: the per-file map is assigned, over the SAME enumeration ------------------------------
if ($code -cnotmatch 'DefinitionFilesAtLoad\s*=') {
    $failures += "$rel never assigns DefinitionFilesAtLoad. Section 5.2 is explicit that the per-file map is NOT deferrable to milestone C: stage 13's writeScope cannot reach this file, so an implementation that ships only the aggregate leaves the gate with nothing to diff and its implementer three stages downstream with only bad options."
}
if ($code -cnotmatch '\bTaskDefinitionFiles\b') {
    $failures += "$rel does not reference TaskDefinitionFiles. The per-file map must fold TaskDefinitionFiles.Enumerate(task) - the SAME enumeration TaskDefinitionHash.Compute uses - keyed by that enumeration's own labels. Section 5.3's closing rule: the two surfaces may disagree about WHEN; they may never disagree about WHAT DEFINES A TASK. (It is internal, in namespace Guardrails.Core.Journal, not Loading - same assembly, so a using is all it needs.)"
}

# --- FORBIDDEN: no fallback, no deferral, no identity-rebinding clone -------------------------------
# Reads $scan (comments AND string literals gone), anchored on a USE not a mention (#470/#76).
if ($scan -cmatch 'DefinitionHashAtLoad\s*(\?\?|\?\?=)' -or $scan -cmatch 'DefinitionFilesAtLoad\s*(\?\?|\?\?=)') {
    $failures += "$rel coalesces off one of the captures. A '?? TaskDefinitionHash.Compute(task)' tail is what section 5.2 calls THE CHEAPEST WRONG IMPLEMENTATION OF THIS ENTIRE PLAN: it reads like defensive coding, passes every behavioural pin in this plan, and silently restores the defect for any node the loader did not build. A null pin records a null hash - there is no fallback to disk at any site, ever."
}
foreach ($pattern in @('.DS_Store', 'Thumbs.db', '.swp', '.orig', '.rej')) {
    # Reads $code, NOT $scan: the patterns ARE string literals, so stripping literals would make this ban
    # unfireable by construction - the mirror of #470's dead-end at the forbidden polarity. A comment
    # explaining that the list lives elsewhere is stripped and therefore safe; a literal in code is not.
    if ($code -cmatch [regex]::Escape($pattern)) {
        $failures += "$rel names the ignore pattern '$pattern'. That is a SECOND COPY of the editor-artifact list, and this is the stage where the pressure to write one is HIGHEST: section 5.2 originally described DefinitionFilesAtLoad as the FILTERED map, and the only predicate that could filter it - LivePlanEditWatch.IsEditorArtifact - is still PRIVATE at this point, because stage 5 promotes it and stage 5 is DOWNSTREAM of you. So the map is captured UNFILTERED and stage 13 filters BOTH sides at diff time; that is why your prompt says so and why the plan now says so too. If you believe filtering has to happen here, ESCALATE with needsHuman - do NOT inline the list. Section 15.2 names exactly this escape as the one that silently un-decides section 6.2."
    }
}
if ($scan -cmatch '\bLazy\s*<') {
    $failures += "$rel uses Lazy<>. Section 11: a Lazy, a ??=, or a computed property that reads disk on access passes every test that does not edit inside the exact window, and silently restores the defect. Both captures are computed EAGERLY, at construction, from the bytes the loader is reading."
}
foreach ($member in @('Directory', 'Action')) {
    if ($scan -cmatch ('with\s*\{[^}]*\b' + $member + '\s*=')) {
        $failures += "$rel contains a record 'with' expression that rebinds $member. Section 5.2: a clone that rebound Directory or Action would carry a pin describing a DIFFERENT FOLDER. The two existing clones (QualifyWaveDependencies, around :949 and :952) rebind only DependsOn and Tasks - DependsOn lives inside task.json and is therefore already inside the hash, which is why they are safe. Do not introduce a third that is not."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== eager capture at load: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "src/Guardrails.Core/Hashing/HashText.cs and src/Guardrails.Core/Journal/TaskDefinitionFiles.cs are OUTSIDE this task's writeScope, deliberately: changing the file set or the framing would move every recorded definition hash in every plan (section 11). CALL them; never change them."
    exit 1
}
Write-Output "Eager capture sound: one construction site, both pins assigned at load from the same enumeration, no fallback, no Lazy, no second copy of the ignore list, no identity-rebinding clone."
exit 0
