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
#          02 runs. Four of its five tests cover the journaller half behaviourally; the fifth
#          (TheWorktreeSettle_JournalsTheBucketAndTheDefinitionHashInTheirOwnSlots) DOES drive this
#          settle - a real Scheduler over a real RunJournal with RecordingWorktreeProvider and a fake
#          executor whose result carries a PendingAttempt, exactly as SchedulerWaveExecutionTests and
#          Execution/ExecutedDefinitionDivergenceTests already do. So "no test can reach this method" is
#          FALSE for the recorder call's ARGUMENT BINDING and true only for the initializer's member set:
#          a behavioural test can see WHICH JOURNAL FIELD a value landed in, and cannot see whether the
#          Turns/Segments values it observes came off `pending` or were recomputed here. That is the
#          residue these clauses carry, and it is why they stay.
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
#            bucket: pending.Bucket         0   this task's deliverable. Matched ONLY inside that call's
#                                               argument list (a method-wide match is defeated by a
#                                               discard) and ONLY in the NAMED form - see the note below.
#            definitionHash in that call    1   EXPECTED nonzero - it is already passed today. The clause
#                                               is about it STILL being passed once a second string?
#                                               parameter lands beside it.
#            TaskFingerprintBucket          0   forbidden-present: the bucket is READ here, never recomputed
#
# WHY THE BUCKET CLAUSE REQUIRES THE NAMED FORM, and why no clause here opens ISchedulerJournal.cs.
#          On the widened member, `definitionHash` and `bucket` are BOTH `string?` and ADJACENT, so every
#          confusion between the two COMPILES. Two are real, and both were reproduced by mutating the
#          sample and running this script:
#            (A) POSITIONAL SLIP - `RecordSettleWithAttempt(task.Id, record, status, mergeSequence,
#                pending.Bucket)`. It costs two things at once: the bucket is dropped, so every worktree
#                run renders (unbucketed) - the exact section 3.2 defect - AND TaskJournalEntry.
#                DefinitionHash is stamped with a BUCKET STRING, which is what a resume's drift check
#                compares and what #322's safe-suffix rewind corroborates a Guardrails-Task-Hash: trailer
#                against (a trailered commit whose hash is not recorded is REFUSED).
#            (B) DECLARATION REORDER - the two parameters declared the other way round on
#                ISchedulerJournal, with the call text in this file byte-identical. The arguments swap and
#                nothing in Scheduler.cs changes, so no amount of reading THIS file finds it.
#          A NAMED argument binds by parameter name regardless of declaration order, so requiring
#          `bucket: pending.Bucket` does not merely DETECT (B) - it makes (B) INCAPABLE OF SWAPPING
#          SILENTLY. Precisely: with the bucket named, a reorder either binds correctly by name, or - if
#          `bucket` were reordered into the slot the positional `definitionHash` argument occupies - the
#          compiler rejects the call outright (CS1744, a named argument for a parameter already given
#          positionally), which guardrail 01's build catches. Silence is the failure mode that matters
#          here, and the named form removes it. That is why there is deliberately NO clause here reading
#          src/Guardrails.Core/Execution/ISchedulerJournal.cs: a second file to keep in sync, to police an
#          ordering the call can no longer be silently wrong about, would buy nothing. The companion
#          `definitionHash` clause closes (A)'s other half - the dropped argument.
#          NOTE what that companion clause does NOT do: it asserts the argument is PRESENT, not that its
#          VALUE is right - `definitionHash: null` would satisfy it. Values are the behavioural test's
#          job, below.
#          This is NOT an unannounced style rule: this task's action.prompt.md MANDATES the named form at
#          Site 2 and gives this same reason, so an implementation that follows the prompt passes.
#          These clauses assert the ARGUMENTS are present and correctly bound; what asserts the VALUES
#          actually land in their own journal fields is behavioural and belongs to task 15 -
#          TheWorktreeSettle_JournalsTheBucketAndTheDefinitionHashInTheirOwnSlots drives a real settle
#          through a real Scheduler (real RunJournal + RecordingWorktreeProvider, no git) with a bucket
#          and a definition hash that cannot be mistaken for one another.
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
    # reads the member, passes a method-wide grep, and delivers nothing. That is the cheapest wrong
    # implementation of this task, so the span matched is the recorder call's own argument list - from
    # the invocation to the statement's semicolon, which admits a multi-line argument list without
    # admitting the rest of the method.
    #
    # NAMED, not positional: see the header note. `bucket` sits beside `definitionHash`, both `string?`,
    # so a positional argument is one slot away from stamping a bucket into the definition-hash field.
    $call = [regex]::Match($member, '\bRecordSettleWithAttempt\s*\(')
    if (-not $call.Success) {
        $failures += "RecordSucceededSettle no longer calls RecordSettleWithAttempt. That is the recorder which journals the attempt record and the settle TOGETHER, and it is the only one carrying the task-grain bucket parameter - which THIS task adds to the ISchedulerJournal member, on top of the RunJournal recorders 06-journal-the-bucket-serial widened. Falling back to the attempt-less RecordSettle here would drop the whole attempt record, not merely the bucket."
    }
    else {
        # NOT $args: that is a PowerShell automatic variable, and shadowing it in a script that may later
        # grow a function is a defect waiting to happen.
        $semi    = $member.IndexOf(';', $call.Index)
        $argList = if ($semi -lt 0) { $member.Substring($call.Index) } else { $member.Substring($call.Index, $semi - $call.Index) }
        if ($argList -cnotmatch 'bucket\s*:\s*pending\s*\.\s*Bucket') {
            $failures += "RecordSucceededSettle does not pass the bucket to RecordSettleWithAttempt as the NAMED argument 'bucket: pending.Bucket'. The bucket is a TASK-grain fact declared on TaskJournalEntry, so it does NOT go in the AttemptRecord initializer (that would not compile) - it goes through the recorder's own optional bucket parameter, and this task's action prompt mandates the named form at Site 2. Two reasons, and neither is style. First, 'bucket' and 'definitionHash' are BOTH string? and ADJACENT on the widened member, so a POSITIONAL argument that slips one slot compiles silently and costs two facts at once: the bucket is dropped (every worktree run renders (unbucketed) - the exact section 3.2 defect) AND TaskJournalEntry.DefinitionHash is stamped with a bucket string, which is what a resume's drift check compares and what the #322 safe-suffix rewind corroborates a Guardrails-Task-Hash: trailer against. Second, a named argument binds by parameter NAME, so it stays correct even if someone later declares the two parameters in the other order on ISchedulerJournal - a reordering that would silently swap two positional arguments while this file's text never changed. Reading pending.Bucket somewhere else in the method does not deliver it either: this clause matches the CALL's argument list precisely because a discard would satisfy anything looser."
        }
        # REQUIRED, REGRESSION: definitionHash must STILL be passed. The mutant this closes is the other
        # half of the positional slip above - a call that hands pending.Bucket to definitionHash's slot
        # and drops definitionHash entirely. The clause above sees only the bucket argument, so without
        # this one that mutant satisfies the whole script. \b so definitionHashAtSettle does not count.
        if ($argList -cnotmatch '\bdefinitionHash\b') {
            $failures += "RecordSucceededSettle's RecordSettleWithAttempt call no longer passes definitionHash. That argument is not this plan's work - it is #274 Part A's, already shipped - but adding a second string? parameter beside it is exactly when it goes missing: a positional call that puts pending.Bucket where definitionHash used to sit compiles, drops the bucket, and leaves TaskJournalEntry.DefinitionHash null or holding a bucket string. That field is what a resume's drift check compares and what the #322 safe-suffix rewind corroborates a commit's Guardrails-Task-Hash: trailer against - a trailered commit whose hash is not recorded is REFUSED - so losing it here does not fail loudly, it makes a later rewind refuse work it should have kept. Pass BOTH: definitionHash and bucket: pending.Bucket. (definitionHashAtSettle is a different parameter and does not satisfy this.)"
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

Write-Output "Worktree settle sound: RecordSucceededSettle's AttemptRecord initializer reads Turns, Segments, Usage and Provenance off pending; the task-grain bucket travels through the recorder call as the NAMED argument bucket: pending.Bucket, beside a definitionHash that is still passed; nothing is recomputed here; and there is exactly one such construction site."
exit 0
