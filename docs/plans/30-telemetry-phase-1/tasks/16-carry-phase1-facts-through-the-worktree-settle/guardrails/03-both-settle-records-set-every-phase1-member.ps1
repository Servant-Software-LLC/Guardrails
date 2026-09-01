# catches: the silent-vanish defect this whole task exists to close, in the half a test cannot reach - a
#          Scheduler.RecordSucceededSettle that builds its own AttemptRecord and leaves one of the
#          Phase-1 members off it. The worktree settle is the DEFAULT path, so a member set on the
#          PendingAttempt (guardrail 02's tests prove that half) and never READ here reaches nothing at
#          all: the fact is computed, carried across the settle boundary, and dropped one line before it
#          would have been journalled. Every run stays green and every test stays green.
#
#          It also catches the opposite mistake - a fix that RECOMPUTES a Phase-1 fact here instead of
#          reading `pending`. A second computation site is a second answer, and the two can disagree
#          without either one looking wrong. The bucket in particular must arrive off the carrier, never
#          off a Classify call made here.
#
#          And it catches the REGRESSION shape: `Usage = pending.Usage` and `Provenance = pending.Provenance`
#          are already on this initializer, and Provenance is how the model digest (task 10) and route
#          warmth (task 14) reach this path AT ALL. Dropping either line while adding the new ones would
#          trade one silent loss for another.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked). Everything else in this plan
#          was demoted to a test; this is one of only TWO survivors. The property here is not a behaviour
#          of one object - it is a fact about TWO CONSTRUCTION SITES AGREEING: the journaller builds a
#          PendingAttempt, and a private method on a 4000-line Scheduler, reached only through a real
#          worktree provider under the integration lock, turns it into the record. Observing that
#          agreement at runtime means standing up an entire parallel run with git segments, a plan branch
#          and an integration commit - which is an integration test of the scheduler, not a unit test of
#          this datum, and which the #468 gate's rung 1 therefore does not reach.
#          `Scheduler.RecordSucceededSettle` is `private`; there is no seam.
#          It is the SECOND line of defence. The FIRST is
#          tests/Guardrails.Core.Tests/Execution/WorktreeSettlePhase1Tests.cs (task 15), which guardrail
#          02 runs, and which covers the journaller half behaviourally.
#          It ships with a committed .valid/.invalid pair in ../samples/.
#
# TWO-VARIABLE STRIP: $raw is NEVER matched against and never reassigned. $code has comments removed.
#          $scan has string literals removed too, and EVERY clause below reads $scan - so a member named
#          in a comment, in a doc comment, or inside a log/exception message is invisible to all of them.
#          That is deliberate: a comment saying "Turns = pending.Turns" is exactly the cheapest wrong
#          implementation of a check like this one.
#
# MEASURED BASELINES against src/Guardrails.Core/Execution/Scheduler.cs at authoring time, on master,
#          case-SENSITIVE (#478). "expected 0" means this task's deliverable; a nonzero would mean the
#          CLAUSE is wrong, not the code:
#            new Journal.AttemptRecord      1   EXPECTED nonzero - the site itself. The equality clause
#                                               below is about a SECOND one appearing, not about this one.
#            Usage = pending.Usage          1   EXPECTED nonzero - regression clause (#475's line).
#            Provenance = pending.Provenance 1  EXPECTED nonzero - regression clause; the digest and
#                                               warmth ride it, so it is how two Phase-1 facts arrive.
#            Turns = pending.Turns          0   this task's deliverable
#            Segments = pending.Segments    0   this task's deliverable
#            RecordSettleWithAttempt(       1   EXPECTED nonzero - the recorder call the bucket rides.
#            pending.Bucket                 0   this task's deliverable, and it is matched ONLY inside
#                                               that call's argument list - see the clause's own comment
#                                               for why a method-wide match is defeated by a discard.
#            TaskFingerprintBucket          0   forbidden-present: the bucket is READ here, never recomputed
#
# ACCUMULATE (#478): one distinguishable message per clause, dumped once at the end.
$ErrorActionPreference = 'Continue'

# GR_SUBJECT is the `guardrails samples verify` contract: the sample path arrives as argv[0] AND in
# $env:GR_SUBJECT, ABSOLUTE, with cwd = the workspace. It is used AS GIVEN - joining it to the workspace
# would yield a nonsense path and PRECONDITION-fail, which reads exactly like a real finding (#559
# halted a run in this repo on precisely that). The default below is repo-relative and resolves against
# the same cwd.
#   $env:GR_SUBJECT='<abs>/tasks/16-carry-phase1-facts-through-the-worktree-settle/samples/03-both-settle-records-set-every-phase1-member.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<abs>/tasks/16-carry-phase1-facts-through-the-worktree-settle/samples/03-both-settle-records-set-every-phase1-member.invalid.cs' -> expect 1
# RE-RUN BOTH after ANY edit to this file, not just the half you touched.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/Scheduler.cs" }

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $f -PathType Leaf)) {
    Write-Output "PRECONDITION: $f does not exist. It is a SHIPPED file this task edits in place; guardrail 01 would have failed first if it were merely broken. This is NOT a finding about the implementation."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $f                     # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary and interpolated strings

$failures = @()

# --- Locate the member, then the initializer inside it ----------------------------------------------
# Region cutter: split at every 4-space-indented access modifier, then identify a region by whether its
# SIGNATURE HEAD - the span before its own opening brace at 4-space indent - declares the member. Cutting
# at the brace is what separates a DECLARATION from a CALL: RecordSucceededSettle is invoked from other
# members of this file, and a fixed character window would match one of those calls and hand back the
# wrong region.
$declStarts = [regex]::Matches($scan, '(?m)^    (?:public|private|internal|protected)\b')
$regions    = @()
for ($i = 0; $i -lt $declStarts.Count; $i++) {
    $start = $declStarts[$i].Index
    $end   = if ($i + 1 -lt $declStarts.Count) { $declStarts[$i + 1].Index } else { $scan.Length }
    $regions += ,$scan.Substring($start, $end - $start)
}

function Get-MemberRegion {
    param([string[]] $Regions, [string] $Member)
    foreach ($region in $Regions) {
        $brace = [regex]::Match($region, '(?m)^    \{')
        $sig   = if ($brace.Success) { $region.Substring(0, $brace.Index) } else { $region }
        # -cmatch: C# identifiers are case-SENSITIVE and PowerShell -match is not. The [(<] tail admits a
        # generic method; the signature span admits a multi-line parameter list.
        if ($sig -cmatch ('\b' + [regex]::Escape($Member) + '\s*[(<]')) {
            return $region
        }
    }
    return $null
}

# Brace-matched extraction. Safe on $scan because string literals are already gone, so no '{' inside a
# literal can unbalance the count.
function Get-BalancedBlock {
    param([string] $Text, [int] $From)
    $open = $Text.IndexOf('{', $From)
    if ($open -lt 0) { return $null }
    $depth = 0
    for ($i = $open; $i -lt $Text.Length; $i++) {
        $ch = $Text[$i]
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') {
            $depth--
            if ($depth -eq 0) { return $Text.Substring($open, $i - $open + 1) }
        }
    }
    return $null
}

$member = Get-MemberRegion -Regions $regions -Member 'RecordSucceededSettle'
$init   = $null
if ($null -eq $member) {
    $failures += "$f no longer declares a member named RecordSucceededSettle. That is the worktree-mode SUCCESS settle - the DEFAULT path for a real run - and renaming or deleting it is not a refactor this task is authorised to make. Every clause below is about what that member's AttemptRecord initializer reads off pending, so none of them could be evaluated."
}
else {
    $where = [regex]::Match($member, '\bnew\s+Journal\s*\.\s*AttemptRecord\b')
    if (-not $where.Success) {
        $failures += "RecordSucceededSettle no longer constructs a new Journal.AttemptRecord. That construction IS the worktree settle's record - the one Scheduler.RecordSettleWithAttempt journals - and this task's whole subject is which members it reads off pending. Note the Journal. qualifier: it is the only AttemptRecord construction site outside AttemptJournaler.cs and TaskExecutor.RevalidateAsync, and a bare new AttemptRecord grep misses it."
    }
    else {
        $init = Get-BalancedBlock -Text $member -From $where.Index
        if ($null -eq $init) {
            $failures += "the new Journal.AttemptRecord in RecordSucceededSettle has no balanced { ... } initializer this check could read. If the record is now built some other way (a helper, a with expression, a constructor call), the members below cannot be verified where they must be verified. This is a check that could not run, not a finding about a specific member: say so if you escalate."
        }
    }
}

if ($null -ne $init) {
    # --- REQUIRED, REGRESSION: the two lines already there stay there ------------------------------
    # Both are ALREADY-SHIPPED carriers, and Provenance is how the model digest (task 10) and route
    # warmth (task 14) reach this settle path at all - they ride AttemptProvenance precisely so no
    # separate carrier is needed. Dropping either while adding the new members trades one silent loss
    # for another.
    if ($init -cnotmatch 'Usage\s*=\s*pending\s*\.\s*Usage') {
        $failures += "the AttemptRecord initializer in RecordSucceededSettle no longer sets Usage = pending.Usage. That line is #475's fix and its removal is the exact defect this task exists to prevent, one member over: the tokens axis would reach SERIAL runs only, while worktree is the default. Put it back."
    }
    if ($init -cnotmatch 'Provenance\s*=\s*pending\s*\.\s*Provenance') {
        $failures += "the AttemptRecord initializer in RecordSucceededSettle no longer sets Provenance = pending.Provenance. Two Phase-1 facts ride the provenance rather than carrying their own member - the model digest (task 10) and route warmth (task 14) - and this single line is how BOTH of them reach the worktree settle. Removing it silently drops two facts that no other clause here checks for. Put it back."
    }

    # --- REQUIRED: this task's two attempt-grain deliverables ---------------------------------------
    if ($init -cnotmatch 'Turns\s*=\s*pending\s*\.\s*Turns') {
        $failures += "the AttemptRecord initializer in RecordSucceededSettle does not set Turns = pending.Turns. 12-record-the-turn-count journals the turn count on the SERIAL path; this initializer is the worktree path, and worktree is the DEFAULT. Setting the carrier on the PendingAttempt without reading it here means the number is computed, carried across the settle boundary, and dropped one line before it would have been journalled - with every run and every test still green. JournalModel.cs documents this failure: grep 'A member hung directly off the attempt record'."
    }
    if ($init -cnotmatch 'Segments\s*=\s*pending\s*\.\s*Segments') {
        $failures += "the AttemptRecord initializer in RecordSucceededSettle does not set Segments = pending.Segments. Same failure as Turns, one member over: 12a-segment-the-attempt-durations journals the action and guardrail durations on the serial path only, and this is the default path. RunReport.cs carries the worked example on PendingAttempt.Usage - grep 'WITHOUT this line the value the record above sets reaches serial runs only'."
    }
}

if ($null -ne $member) {
    # --- REQUIRED: the TASK-grain bucket travels through the RECORDER CALL, not the initializer -----
    # Bucket is declared on TaskJournalEntry, NOT on AttemptRecord (a task's bucket is constant across
    # its own retries within one run), so `Bucket = pending.Bucket` inside the initializer would not
    # compile. It rides RecordSettleWithAttempt's optional bucket parameter instead.
    #
    # SCOPED TO THE CALL, not to the method, and that narrowing is the whole strength of this clause.
    # A method-wide match for `pending.Bucket` is satisfied by a DISCARD - `_ = pending.Bucket;` - which
    # reads the member, passes a method-wide grep, and delivers nothing. Nor would the behavioural tests
    # notice: task 15's tests assert on the PendingAttempt the journaller builds, which is the OTHER side
    # of this hand-off. That combination is the cheapest wrong implementation of this task, so the span
    # matched is the recorder call's own argument list - from the invocation to the statement's
    # semicolon, which admits a multi-line argument list without admitting the rest of the method.
    $call = [regex]::Match($member, '\bRecordSettleWithAttempt\s*\(')
    if (-not $call.Success) {
        $failures += "RecordSucceededSettle no longer calls RecordSettleWithAttempt. That is the recorder which journals the attempt record and the settle TOGETHER, and it is the only one carrying the task-grain bucket parameter - which THIS task adds to the ISchedulerJournal member, on top of the RunJournal recorders 06-journal-the-bucket-serial widened. Falling back to the attempt-less RecordSettle here would drop the whole attempt record, not merely the bucket."
    }
    else {
        # NOT $args: that is a PowerShell automatic variable, and shadowing it in a script that may later
        # grow a function is a defect waiting to happen.
        $semi    = $member.IndexOf(';', $call.Index)
        $argList = if ($semi -lt 0) { $member.Substring($call.Index) } else { $member.Substring($call.Index, $semi - $call.Index) }
        if ($argList -cnotmatch 'pending\s*\.\s*Bucket') {
            $failures += "RecordSucceededSettle does not pass pending.Bucket to RecordSettleWithAttempt. The bucket is a TASK-grain fact declared on TaskJournalEntry, so it does NOT go in the AttemptRecord initializer (that would not compile) - it goes through the recorder's own optional bucket parameter. Reading the member somewhere else in the method does not deliver it: this clause matches the CALL's argument list precisely because a discard would satisfy anything looser, and task 15's tests only cover the journaller's side of this hand-off. Without it, a worktree run's task entry carries no bucket and the corpus report renders (unbucketed) for the majority of real runs."
        }
    }
}

# --- FORBIDDEN: the bucket is READ off the carrier, never recomputed here ---------------------------
# A second computation site is a second answer. The journaller computes the bucket from the TaskNode it
# already receives; the scheduler's job is to carry that value, not to derive its own - which could
# differ (a different overload, a stale writeScope) without either site looking wrong.
if ($scan -cmatch '\bTaskFingerprintBucket\b') {
    $failures += "$f names TaskFingerprintBucket. The scheduler must READ the bucket off pending, never recompute it: a second computation site is a second answer, and the two can disagree while both look correct. The value was computed once in AttemptJournaler and carried here on PendingAttempt.Bucket - use it."
}

# --- FORBIDDEN: a SECOND worktree record-construction site that no clause above governs -------------
# Not an adequacy floor - an EQUALITY against a set this plan enumerates. A new settle path constructing
# its own AttemptRecord is exactly how the vanishing recurs, and every clause above is scoped to the one
# member, so a second site would be invisible to all of them.
$sites = [regex]::Matches($scan, '\bnew\s+Journal\s*\.\s*AttemptRecord\b').Count
if ($sites -ne 1) {
    $failures += "$f contains $sites new Journal.AttemptRecord construction site(s); there must be exactly 1 (RecordSucceededSettle's). A HIGHER count means a second settle path builds its own record, and every clause above is scoped to RecordSucceededSettle - so the new site is unchecked, which is precisely how a Phase-1 member vanishes on one path while passing every test on the other. A LOWER count means the settle no longer records a real attempt at all."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== both settle records set every Phase-1 member: $($failures.Count) problem(s) in $f ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Worktree is the DEFAULT execution mode. A Phase-1 fact set on the PendingAttempt and not read here is not a partial success - it is a fact that reaches nothing, on the path most runs take, with a green run and a green suite either side of it."
    exit 1
}

Write-Output "Worktree settle sound: RecordSucceededSettle's AttemptRecord initializer reads Turns, Segments, Usage and Provenance off pending, the task-grain bucket travels through the recorder call, nothing is recomputed here, and there is exactly one such construction site."
exit 0
