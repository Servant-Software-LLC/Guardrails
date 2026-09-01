# catches: the two opposite ways this stage can go wrong in one 4000-line file, and Risk 6 says both
#          have already beaten a draft of this check.
#
#          TOO LITTLE: a worktree settle that still recomputes from disk. Section 9 records the first
#          draft - a grep for the literal expression at W3 - which matched ONCE on the unfixed tree and
#          ZERO times at W1, W2 and W4, so "fixing" only W3 would have turned it green with the defect
#          intact in serial mode, revalidate AND the default worktree settle. This check counts the CALL
#          instead, per surviving member, which is what makes a seventh write site added later - written
#          any way at all - fail here.
#
#          TOO MUCH: a fix that pinned the READ sites as well. Section 11 calls that out first among the
#          things an unattended run of this plan must not do: "Pinning R1 would make P1 pass and silence
#          definition drift entirely - a strictly worse product than today." Stage 7's P6a and P6b are
#          the behavioural half of that defence; this is Risk 6's structural half, the PER-SITE POSITIVE
#          COUNT of the reads: "counting the reads is what makes a seventh write site written ANY way
#          fail the build; pattern-matching the writes only catches the one spelling the author happened
#          to imagine."
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): the read half IS carried by
#          tests - P6a and P6b - and they stay. What no test can carry is the SET: "these four members
#          and no others still call Compute in this file." A fifth read site that agrees with the other
#          four is behaviourally invisible, and a per-member check is the only instrument that can say
#          WHICH member drifted. This is also why the durable, repo-lifetime form of the same property is
#          stage 6's COMMITTED anchor test rather than a plan-folder guardrail: this file evaporates when
#          the run ends, and the hazard does not. This one exists because stage 6 runs AFTER this stage
#          and could otherwise only report a defect it has no writeScope to fix (the #553 shape).
#          It ships with a committed .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. The REQUIRED clauses
#          read $code (comments gone, literals intact); the BANS read $scan (literals gone too), so a
#          comment naming a member and an exception message quoting one are both invisible to them.
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, against
#          src/Guardrails.Core/Execution/Scheduler.cs, case-SENSITIVE (#478):
#            TaskDefinitionHash.Compute(     6   EXPECTED nonzero, and it is the number this whole check
#                                               is about: four READS that must survive (DetectDefinition
#                                               Drift, BuildResolvedTasks, ConsumePendingAnswers,
#                                               ClassifyTaskGateAsync) plus two WRITES that must go
#                                               (SettleAsync, SettleGreenIfWorktreeAsync). A
#                                               tests-untouched-shaped REGRESSION clause for the four,
#                                               and this stage's deliverable for the two.
#            DefinitionHashAtLoad            0   this stage's deliverable
#            DefinitionHashAtLoad ??         0   forbidden-present
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the sample path
# arrives as argv[0] AND in $env:GR_SUBJECT, ABSOLUTE. Joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/05-stamp-the-pin-worktree/samples/03-writes-read-the-pin-reads-read-disk.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/05-stamp-the-pin-worktree/samples/03-writes-read-the-pin-reads-read-disk.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Execution/Scheduler.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
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

# Tolerates the Journal. prefix, whitespace, and a hoisted local: what is matched is the INVOCATION, not
# one literal expression - the defeat that beat draft 1 of section 9.
$callPattern = '(?:\bJournal\s*\.\s*)?\bTaskDefinitionHash\s*\.\s*Compute\s*\('

# --- Member regions, and the trap this cost at author time -----------------------------------------
# The first version of this block anchored on '^    <modifier> ... <member> (' - modifier and member
# name on ONE line. Measured against the real file: DetectDefinitionDrift declares its tuple return type
# on the line ABOVE its name, so the clause could NEVER match and reported "no longer declares a member
# named DetectDefinitionDrift" against a file that plainly does. That is the #468 lesson in miniature -
# the VALID half of the sample pair is the only half that can expose a clause which can never match,
# because under the invalid half everything is failing anyway.
#
# The fix: cut the file into REGIONS at every 4-space-indented access modifier, then identify each region
# by whether its SIGNATURE HEAD (the first 400 characters, which spans a multi-line parameter list)
# carries the member name in a declaration position. Crude, and sufficient here: each of these six
# members is the only one of its name in the file, and a real C# parser is out of scope for a guardrail.
$declStarts = [regex]::Matches($code, '(?m)^    (?:public|private|internal|protected)\b')
$regions    = @()
for ($i = 0; $i -lt $declStarts.Count; $i++) {
    $start  = $declStarts[$i].Index
    $end    = if ($i + 1 -lt $declStarts.Count) { $declStarts[$i + 1].Index } else { $code.Length }
    $regions += ,$code.Substring($start, $end - $start)
}

function Get-MemberRegion {
    param([string[]] $Regions, [string] $Member)
    foreach ($region in $Regions) {
        # The SIGNATURE is the region up to its opening brace at 4-space indent - NOT a fixed character
        # window. Measured at author time: a 400-character head window matched 'SettleAsync(' inside
        # SettleGreenIfWorktreeAsync's BODY (it delegates to it on the second line), so the lookup
        # returned the wrong region and the write-site ban went green against a file that still
        # recomputed. A call is not a declaration; cutting at the brace is what separates them.
        $brace = [regex]::Match($region, '(?m)^    \{')
        $sig   = if ($brace.Success) { $region.Substring(0, $brace.Index) } else { $region }
        # -cmatch: C# identifiers are case-SENSITIVE and PowerShell -match is not (taxonomy 3). The
        # [(<] tail admits a generic method; the signature span admits a multi-line parameter list.
        if ($sig -cmatch ('\b' + [regex]::Escape($Member) + '\s*[(<]')) {
            return $region
        }
    }
    return $null
}

# --- REQUIRED: the four surviving READ sites still recompute from CURRENT DISK ----------------------
foreach ($member in @('DetectDefinitionDrift', 'BuildResolvedTasks', 'ConsumePendingAnswers', 'ClassifyTaskGateAsync')) {
    $body = Get-MemberRegion -Regions $regions -Member $member
    if ($null -eq $body) {
        $failures += "$rel no longer declares a member named $member. Section 4.3's taxonomy pins EIGHT surviving TaskDefinitionHash.Compute call sites by file AND member, and four of them are in this file. A renamed or deleted read site is not a refactor this stage is authorised to make."
        continue
    }
    if ($body -cnotmatch $callPattern) {
        $failures += "$member no longer calls TaskDefinitionHash.Compute. It is a READ, and section 4.3's rule is 'reads recompute from disk; writes of the executed-definition record read the pin'. Section 11: 'No task may pin the READ sites. Pinning R1 would make P1 pass and silence definition drift entirely - a strictly worse product than today.' Stage 7's P6a and P6b exist solely to fail that implementation; this clause is the structural half. Put the recompute back."
    }
}

# --- FORBIDDEN: neither WRITE site recomputes any more ---------------------------------------------
foreach ($member in @('SettleAsync', 'SettleGreenIfWorktreeAsync')) {
    $body = Get-MemberRegion -Regions $regions -Member $member
    if ($null -eq $body) {
        $failures += "$rel no longer declares a member named $member. W2 (SettleAsync, the deferred settle and THE DEFAULT FOR A REAL RUN) and W3 (SettleGreenIfWorktreeAsync) are this stage's two write sites; renaming one is not a refactor this stage is authorised to make."
        continue
    }
    if ($body -cmatch $callPattern) {
        $failures += "$member still calls TaskDefinitionHash.Compute. It is a WRITE of the executed-definition record - the journal entry and, for W2, the Guardrails-Task-Hash trailer - so it must stamp task.DefinitionHashAtLoad instead. W2 is the site the ISSUE ITSELF DOES NOT NAME and section 4.2 calls the one that matters most: plan 28's motivating overnight run was a worktree-mode run, whose authoritative settle is this one."
    }
}

# --- FORBIDDEN: no coalescing fallback anywhere in the file ----------------------------------------
if ($scan -cmatch 'DefinitionHashAtLoad\s*(\?\?|\?\?=)') {
    $failures += "$rel coalesces off the pin. Section 5.2 calls this THE CHEAPEST WRONG IMPLEMENTATION OF THIS ENTIRE PLAN: for every node the loader built the two branches are identical, so it passes every behavioural pin, and it silently restores the defect for any node the loader did not build. A null pin records a NULL hash, which SSOT section 7.2 already defines and handles."
}

# --- REQUIRED: the pin is actually stamped somewhere in this file -----------------------------------
if ($code -cnotmatch '\bDefinitionHashAtLoad\b') {
    $failures += "$rel never mentions DefinitionHashAtLoad. Removing the Compute calls from the two write sites is only half the change: both must STAMP the load-time pin. If they stamp nothing, every worktree settle records a null hash and the plan has silenced definition drift rather than fixed it."
}

# --- FORBIDDEN: a private helper that reaches disk BETWEEN the members above (W6) ------------------
# The region cutter above starts a new region at every 4-space access modifier, so a NEW private helper
# is its own region and neither write-site clause sees it. A helper spelled
#     if (task.DefinitionHashAtLoad is { } pin) { return pin; }
#     return TaskDefinitionHash.Compute(task);
# passes every clause above: both write-site regions are clean, and there is no '??' anywhere. The
# file-wide COUNT is what closes it - it is not an adequacy floor (dotnet.md forbids those), it is an
# EQUALITY against a set this plan enumerates by file and member, and stage 6's committed anchor test
# asserts the same set repo-wide and for keeps.
$total = [regex]::Matches($scan, $callPattern).Count
if ($total -ne 4) {
    $failures += "$rel contains $total TaskDefinitionHash.Compute call site(s); after this stage there must be exactly 4 (the four READ sites, with W2 and W3 now stamping the pin). A HIGHER count means a call reappeared somewhere the per-member clauses above do not look - most likely a new private helper holding an 'if (pin is not null) return pin; return Compute(task);' fallback, which every other clause in this file passes because both write-site regions are clean and there is no coalescing operator. A LOWER count means a READ site lost its recompute. Section 4.3 enumerates the surviving sites by file AND member; stage 6's anchor test pins the same set repo-wide."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== writes read the pin, reads read disk: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Two members change and four must not. The escalation-record binding in ClassifyTaskGateAsync is DELIBERATELY left on disk (section 4.4): both sides of #361's answer-file equality read current disk, and they must stay on the same side - pinning the stamping half alone would make a legitimate answer fail its own binding after any mid-run edit."
    exit 1
}
Write-Output "Scheduler split sound: the two worktree write sites stamp the pin, the four read sites still recompute from disk, and nothing coalesces."
exit 0
