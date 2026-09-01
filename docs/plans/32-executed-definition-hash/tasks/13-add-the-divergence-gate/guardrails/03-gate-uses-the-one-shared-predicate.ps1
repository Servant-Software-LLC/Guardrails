# catches: a SECOND copy of the editor-artifact ignore list. Section 6.2 decides that the gate and the
#          watch share ONE predicate "so a future addition cannot reach one and miss the other", and
#          section 15.2 traces every pressure on this stage pointing at the same escape - inline the list,
#          or skip it - because HashText and TaskDefinitionFiles are forbidden and a new source file is
#          forbidden.
#
#          An inlined copy is BEHAVIOURALLY IDENTICAL to the shared predicate today. It passes P10, P16,
#          P9 through P15, the terminal gate, and every review. It becomes wrong on the day someone adds
#          a sixth pattern to one home and not the other - which is the only day it matters, and by then
#          nobody is looking at this stage.
#
#          It also re-checks the four Scheduler READ sites, because this stage writes the same file stage
#          5 did and a settle-time diff is written right beside them. A read pinned here is the same
#          catastrophic wrong fix section 11 forbids, arriving eight stages later than the guardrail that
#          was watching for it.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): this is the archetypal
#          "X must USE Y" case, and the catalogue's own answer - an AGREEMENT property test enumerating
#          the input domain and asserting the two sides agree - CANNOT be written here, for the reason
#          that makes the defect survive review: when the implementation is correct there is no second
#          side to compare against. The property is "there exists no second copy", which is unobservable
#          at runtime by construction, since an equivalent copy is behaviourally indistinguishable. That
#          is the demotion order's last rung, and it ships with a committed .valid/.invalid pair in
#          ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. REQUIRED clauses read
#          $code (comments gone, literals intact); the BANS read $scan (literals gone too) - which matters
#          in BOTH directions here, since the ignore patterns are string literals: the ban on a second
#          copy therefore reads $code, not $scan, and says so at its clause.
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, against
#          src/Guardrails.Core/Execution/Scheduler.cs, case-SENSITIVE (#478):
#            IsEditorArtifact         0   this stage's deliverable (the gate's call into the one home)
#            DefinitionFilesAtLoad    0   this stage's deliverable
#            .DS_Store                0   forbidden-present, and the whole point: this file must never
#                                         name an ignore pattern, because naming one is what a second
#                                         copy of the list looks like.
#            Thumbs.db / .swp / .orig / .rej   0 each   same.
#            TaskDefinitionHash.Compute(   6  EXPECTED nonzero - a tests-untouched REGRESSION clause. Two
#                                         of the six are stage 5's write sites and are gone by the time
#                                         this stage runs, so the floor asserted below is the FOUR reads,
#                                         checked per member rather than as a count.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the sample path
# arrives as argv[0] AND in $env:GR_SUBJECT, ABSOLUTE. Joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/13-add-the-divergence-gate/samples/03-gate-uses-the-one-shared-predicate.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/13-add-the-divergence-gate/samples/03-gate-uses-the-one-shared-predicate.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Execution/Scheduler.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is a SHIPPED file this task edits in place; guardrail 01 would have failed first if it were merely broken."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: the gate calls the ONE shared predicate ---------------------------------------------
# -cmatch: C# identifiers are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
if ($code -cnotmatch '\bIsEditorArtifact\s*\(') {
    $failures += "$rel never calls IsEditorArtifact. Section 6.2 requires the gate and the plan-edit watch to share ONE ignore predicate, and stage 5 promoted it to internal static for exactly this. If the call does not compile, stage 5's promotion did not land - ESCALATE with needsHuman rather than inlining the list, which is the escape section 15.2 says every other pressure points at."
}

# --- REQUIRED: the gate diffs the PER-FILE map, not two aggregates ----------------------------------
if ($code -cnotmatch '\bDefinitionFilesAtLoad\b') {
    $failures += "$rel never reads DefinitionFilesAtLoad. Section 6.3: the gate diffs TWO PER-FILE MAPS over the same filtered surface - it never compares two aggregates, and in particular never compares the full-surface DefinitionHashAtLoad against a filtered recompute, because those hash different file sets and would differ on any task carrying an editor artifact with nobody having edited anything. The per-file map is also what lets the report NAME which files moved, which section 6.3 requires."
}

# --- FORBIDDEN: a second copy of the ignore list ---------------------------------------------------
# Reads $code, NOT $scan: the patterns ARE string literals, so stripping literals would make this ban
# unfireable by construction - the mirror of #470's dead-end, at the forbidden polarity. A comment
# EXPLAINING that the list lives elsewhere is stripped and therefore safe; a literal in code is not.
foreach ($pattern in @('.DS_Store', 'Thumbs.db', '.swp', '.orig', '.rej')) {
    if ($code -cmatch [regex]::Escape($pattern)) {
        $failures += "$rel names the ignore pattern '$pattern'. That is a SECOND COPY of the editor-artifact list, and it is behaviourally identical to the shared one TODAY - it passes every pin in this plan. It becomes wrong on the day someone adds a sixth pattern to one home and not the other, which is the only day it matters. Call LivePlanEditWatch.IsEditorArtifact; section 6.2 keeps the list in exactly one place so a future addition cannot reach one and miss the other."
    }
}

# --- REQUIRED: the four READ sites still recompute (re-checked, because this file is written twice) --
$declStarts = [regex]::Matches($code, '(?m)^    (?:public|private|internal|protected)\b')
$regions    = @()
for ($i = 0; $i -lt $declStarts.Count; $i++) {
    $start = $declStarts[$i].Index
    $end   = if ($i + 1 -lt $declStarts.Count) { $declStarts[$i + 1].Index } else { $code.Length }
    $regions += ,$code.Substring($start, $end - $start)
}
$callPattern = '(?:\bJournal\s*\.\s*)?\bTaskDefinitionHash\s*\.\s*Compute\s*\('
foreach ($member in @('DetectDefinitionDrift', 'BuildResolvedTasks', 'ConsumePendingAnswers', 'ClassifyTaskGateAsync')) {
    $body = $null
    foreach ($region in $regions) {
        # The SIGNATURE is the region up to its opening brace at 4-space indent, NOT a fixed character
        # window: measured at author time in stage 5's sibling check, a head window matched a CALL inside
        # another member's body and returned the wrong region entirely.
        $brace = [regex]::Match($region, '(?m)^    \{')
        $sig   = if ($brace.Success) { $region.Substring(0, $brace.Index) } else { $region }
        if ($sig -cmatch ('\b' + [regex]::Escape($member) + '\s*[(<]')) { $body = $region; break }
    }
    if ($null -eq $body) {
        $failures += "$rel no longer declares a member named $member. Section 4.3 pins EIGHT surviving TaskDefinitionHash.Compute call sites by file AND member; four of them are in this file, and stage 6's committed anchor test asserts that set repo-wide."
    }
    elseif ($body -cnotmatch $callPattern) {
        $failures += "$member no longer calls TaskDefinitionHash.Compute. It is a READ. Section 11: 'No task may pin the READ sites. Pinning R1 would make P1 pass and silence definition drift entirely - a strictly worse product than today.' This stage writes the same file stage 5 did, and a settle-time diff sits right beside them; put the recompute back."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== shared predicate / read sites: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The gate compares the IGNORE-LIST-FILTERED surface; the RECORDED hash keeps the full one. HashText.cs is outside this task's writeScope precisely so those two cannot be conflated - filtering what the hash COVERS would move every recorded definition hash in every plan."
    exit 1
}
Write-Output "Gate wiring sound: it calls the one shared ignore predicate, diffs the per-file map, names no ignore pattern of its own, and all four read sites still recompute from disk."
exit 0
