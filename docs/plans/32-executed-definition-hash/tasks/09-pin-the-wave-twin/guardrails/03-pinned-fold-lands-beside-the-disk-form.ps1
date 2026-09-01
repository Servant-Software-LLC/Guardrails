# catches: the pinned wave fold REPLACING the disk-reading Compute(wave) instead of landing beside it,
#          and a pinned form that quietly falls back to disk.
#
#          The replacement failure is the expensive one and it is invisible to stage 8's pins: P7a and
#          P7b both go green if the WRITE stamps the right value, however that value was obtained. But
#          section 5.4 keeps the disk form for every READ - the wave-drift compare, the wave-proceed
#          answer key, and ReviewMarker's key hash - and section 5.5 stakes the "no re-staled review
#          marker" claim on it: "a WAVE marker keys on WaveDefinitionHash, which is untouched FOR READS -
#          section 5.4 adds a pinned form BESIDE Compute(wave) rather than replacing it."
#
#          Guardrail 02 carries the BEHAVIOURAL half by running the shipped WaveDefinitionHashTests,
#          which drives Compute(wave) directly. This check carries what no test can: that the disk form
#          still reads DISK. A replacement that computed from pins would keep that suite green wherever
#          its fixtures are unedited - which is all of them - and would only surface as a resume halt
#          much later.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): the two forms are
#          behaviourally identical on every unedited fixture, and an unedited fixture is what a unit test
#          has. The discriminating input is a MID-RUN EDIT against a READ path, which is stage 7's P6a/P6b
#          at task level and has no wave-level analogue this plan authors. Add the coalescing-fallback
#          clause, which is unobservable at runtime for the same reason it is at task level (section 5.2),
#          and this is the demotion order's last rung. It ships with a committed .valid/.invalid pair in
#          ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. REQUIRED clauses read
#          $code (comments gone, literals intact); the BANS read $scan (literals gone too).
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, against
#          src/Guardrails.Core/Journal/WaveDefinitionHash.cs, case-SENSITIVE (#478):
#            TaskDefinitionHash.Compute(   1   EXPECTED nonzero - this file IS one of the eight surviving
#                                              call sites (section 4.3 row 11, the disk form's task
#                                              fold). A tests-untouched REGRESSION clause: it must still
#                                              be here afterwards.
#            DefinitionHashAtLoad          0   this stage's deliverable
#            DefinitionHashAtLoad ??       0   forbidden-present
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the sample path
# arrives as argv[0] AND in $env:GR_SUBJECT, ABSOLUTE. Joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/09-pin-the-wave-twin/samples/03-pinned-fold-lands-beside-the-disk-form.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/09-pin-the-wave-twin/samples/03-pinned-fold-lands-beside-the-disk-form.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Journal/WaveDefinitionHash.cs' }
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

# Matched as an INVOCATION, tolerating the Journal. prefix and whitespace - not one literal spelling.
$callPattern = '(?:\bJournal\s*\.\s*)?\bTaskDefinitionHash\s*\.\s*Compute\s*\('

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: the DISK form survives, and still reads disk ----------------------------------------
# -cmatch: C# identifiers are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
if ($code -cnotmatch 'public\s+static\s+string\s+Compute\s*\(\s*WaveNode\b') {
    $failures += "$rel no longer declares 'public static string Compute(WaveNode ...)'. Section 5.4 keeps it UNCHANGED for every READ - the wave-drift compare, the wave-proceed answer key, and ReviewMarker's key hash - and there are EIGHT WaveDefinitionHash.Compute call sites across Scheduler.cs, ReviewMarker.cs and RunCommand.cs bound to it, one of them in a different assembly. Add a pinned SIBLING; never repurpose this one."
}
if ($code -cnotmatch '(?:\bJournal\s*\.\s*)?\bTaskDefinitionHash\s*\.\s*Compute\s*\(') {
    $failures += "$rel no longer calls TaskDefinitionHash.Compute. This file is one of the EIGHT surviving call sites section 4.3 pins (row 11 - the disk form's task fold), and stage 6's committed anchor test asserts that set by file and member. If the disk form now folds pins instead of recomputing, it has stopped being a read - and the wave-drift compare that depends on it is comparing a pin against a pin, which checks nothing."
}

# --- REQUIRED: a PINNED form exists too ------------------------------------------------------------
if ($code -cnotmatch '\bDefinitionHashAtLoad\b') {
    $failures += "$rel never mentions DefinitionHashAtLoad. The pinned fold is this stage's deliverable: it folds each constituent task's DefinitionHashAtLoad plus WaveNode.DefinitionHashAtLoad, and write site W5 (the wave-completion stamp) calls it. Without it, section 14.5's 'the wave hash changes iff a constituent task hash changes' stays false - and section 3 is explicit that leaving milestone B out is not neutral, it breaks a documented invariant."
}

# --- FORBIDDEN: no coalescing fallback -------------------------------------------------------------
if ($scan -cmatch 'DefinitionHashAtLoad\s*(\?\?|\?\?=)') {
    $failures += "$rel coalesces off a pin. Section 5.2's rule is the same at wave level as at task level: a null pin records a null hash, with no fallback to disk at any write site, ever. A '?? Compute(...)' tail is behaviourally identical for every node the loader built - which in production is all of them - so it passes every behavioural pin while restoring the defect for anything else."
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
if ($total -ne 1) {
    $failures += "$rel contains $total TaskDefinitionHash.Compute call site(s); after this stage there must be exactly 1 (the disk form task fold - section 4.3 row 11). A HIGHER count means a call reappeared somewhere the per-member clauses above do not look - most likely a new private helper holding an 'if (pin is not null) return pin; return Compute(task);' fallback, which every other clause in this file passes because both write-site regions are clean and there is no coalescing operator. A LOWER count means a READ site lost its recompute. Section 4.3 enumerates the surviving sites by file AND member; stage 6's anchor test pins the same set repo-wide."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave fold: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Two functions, not one. The pinned fold must also be BYTE-IDENTICAL to the disk fold on an unedited tree - the wave-drift COMPARE is still a disk read, so any framing difference makes every completed wave read as drifted on the next resume. Guardrail 02 runs the six shipped resume tests that gate exactly that."
    exit 1
}
Write-Output "Wave fold sound: the disk-reading Compute(WaveNode) survives and still recomputes, a pinned form exists beside it, and nothing coalesces."
exit 0
