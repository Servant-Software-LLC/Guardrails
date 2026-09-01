# catches: a write site that still reaches disk. Three shapes, all of which compile, none of which any
#          behavioural pin in this plan can distinguish from the correct implementation:
#            1. the substitution was never made and TaskDefinitionHash.Compute(task) is still there -
#               guardrail 02 would still be green in serial mode IF the pin happens to equal the disk
#               hash on the fixture, which it does on every run whose fixture does not edit inside the
#               exact window;
#            2. `task.DefinitionHashAtLoad ?? TaskDefinitionHash.Compute(task)' - section 5.2's
#               "cheapest wrong implementation of this entire plan". For every node the LOADER built the
#               two branches are identical, and in production the loader builds every node there is, so
#               no test can tell them apart. It reads like defensive coding and it silently restores the
#               defect for any node the loader did not build;
#            3. a private helper one frame away that does either of the above.
#
#          Section 9 records two earlier drafts of this check and BOTH WERE SATISFIED BY THE UNFIXED
#          TREE. The first pinned the literal expression `handle.DefinitionHash = Journal.TaskDefinition
#          Hash.Compute' - which matches ONCE today (SettleGreenIfWorktreeAsync) and ZERO times at W1, W2
#          and W4, so fixing only W3 would have turned it green with the defect intact in serial mode,
#          revalidate AND the default worktree settle. The second asked that the write-site expressions
#          "read .DefinitionHashAtLoad", which an expression-bodied property satisfies verbatim with the
#          defect 100% intact. This check is written from those defeats: it counts the CALL down to zero
#          in the files that own the sites, rather than pattern-matching the one spelling an author
#          happened to imagine (Risk 6).
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): shape 2 decides it. A
#          coalescing fallback is behaviourally IDENTICAL to the correct implementation on every input a
#          test can supply, because the discriminating input - a node the loader did not build - exists
#          only in tests, and section 11 keeps tests/** out of every implementation stage. The ideal
#          instrument would be an AGREEMENT property test and there is no second side to compare
#          against. Genuinely unobservable at runtime, so this is the demotion order's last rung, and it
#          ships with a committed .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. The REQUIRED clause
#          reads $code (comments gone, literals intact); the BANS read $scan (literals gone too), so a
#          comment explaining "we deliberately do not call TaskDefinitionHash.Compute here" and a message
#          string naming it are both invisible to them - which matters, because a good implementer will
#          want to write exactly that comment.
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, with each clause's own case
#          sensitivity (#478), per file:
#            AttemptJournaler.cs  TaskDefinitionHash.Compute(   1   -> must become 0
#            TaskExecutor.cs      TaskDefinitionHash.Compute(   1   -> must become 0
#            both                 DefinitionHashAtLoad          0   this task's deliverable
#            both                 DefinitionHashAtLoad ??       0   forbidden-present
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the verifier runs
# this script with the sample path as argv[0] AND in $env:GR_SUBJECT. When it is set it REPLACES THE
# WHOLE LIST with the one file named - which is what makes a single-file sample meaningful here, since a
# sample can only model one of the two write sites and requiring both would false-red every sample run.
# The path arrives ABSOLUTE; joining it to the workspace would yield a nonsense path and
# PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/04-stamp-the-pin-serial-and-revalidate/samples/03-no-disk-fallback-at-the-serial-sites.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/04-stamp-the-pin-serial-and-revalidate/samples/03-no-disk-fallback-at-the-serial-sites.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$targets = if ($env:GR_SUBJECT) {
    @($env:GR_SUBJECT)
} else {
    @('src/Guardrails.Core/Execution/AttemptJournaler.cs', 'src/Guardrails.Core/Execution/TaskExecutor.cs')
}

# ACCUMULATE (#478): one distinguishable message per clause per file, dumped once at the end, so ONE
# attempt learns every gap rather than one gap per attempt.
$failures = @()

foreach ($rel in $targets) {
    $full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

    # PRECONDITION - the one legitimate early exit per file: without the subject every clause is
    # meaningless. `continue', not `exit', so the other file is still reported in the same attempt.
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        $failures += "PRECONDITION: $rel does not exist. It is a SHIPPED file this task edits in place; guardrail 01 would have failed first if it were merely broken."
        continue
    }

    $raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
    $code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
    $code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments
    $scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
    $scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
    $scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

    # --- FORBIDDEN: the call is GONE, however it is spelled ----------------------------------------
    # Tolerates the `Journal.' prefix, whitespace, and a hoisted local: what is banned is the INVOCATION,
    # not one literal expression. Counting the call down to zero is what makes a SEVENTH write site added
    # later - written any way at all - fail here, which pattern-matching the write never could (Risk 6).
    $calls = [regex]::Matches($scan, '(?:\bJournal\s*\.\s*)?\bTaskDefinitionHash\s*\.\s*Compute\s*\(').Count
    if ($calls -gt 0) {
        $failures += "$rel still calls TaskDefinitionHash.Compute( ($calls occurrence(s)). Section 4.3's rule is 'reads recompute from disk; writes of the EXECUTED-DEFINITION RECORD read the pin', and both write sites in this file are writes: stamp task.DefinitionHashAtLoad instead. Section 9 requires ZERO occurrences in this file when the stage is done."
    }

    # --- FORBIDDEN: no coalescing fallback ---------------------------------------------------------
    if ($scan -cmatch 'DefinitionHashAtLoad\s*(\?\?|\?\?=)') {
        $failures += "$rel coalesces off the pin. Section 5.2 calls this THE CHEAPEST WRONG IMPLEMENTATION OF THIS ENTIRE PLAN: for every node the loader built the two branches are identical, so it passes every behavioural pin here, and it silently restores the defect for any node the loader did not build. A null pin records a NULL hash - SSOT section 7.2 already defines and handles that state ('recorded hash absent => unknown, assume unchanged'), and in production it is unreachable because the loader is the only constructor."
    }

    # --- REQUIRED: the pin is actually stamped -----------------------------------------------------
    # Reads $code (literals intact), -cmatch because C# identifiers are case-SENSITIVE and a
    # case-insensitive require-present clause false-GREENS on text C# would never compile (taxonomy 3).
    if ($code -cnotmatch '\bDefinitionHashAtLoad\b') {
        $failures += "$rel never mentions DefinitionHashAtLoad. Removing the Compute call is only half the change: this file's write site must STAMP the load-time pin. If it stamps nothing, every settle here records a null hash and the plan has silenced definition drift rather than fixed it."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== write sites still reaching disk: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Both members already HOLD the TaskNode whose hash they stamp (section 5.2), so this is an expression-level substitution in place - no parameter to thread, no field to add, no object to pass."
    exit 1
}
Write-Output "Serial write sites clean: no TaskDefinitionHash.Compute call remains, no coalescing fallback, and the load-time pin is stamped."
exit 0
