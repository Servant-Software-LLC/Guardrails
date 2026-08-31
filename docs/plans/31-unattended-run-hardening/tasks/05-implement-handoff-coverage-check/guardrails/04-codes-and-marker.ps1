# catches: a code allocation that collides with a RESERVED-BY-NAME gap, or a next-free marker left
#          stale so the next allocator takes GR2068 again. DiagnosticCodes.cs's own comment block
#          says why this matters: three codes are reserved by NAME in design documents that have not
#          shipped yet - GR2060 (doc 19 section 1), GR2061 (doc 18 section 3.4) and GR2054 (doc 17
#          section 13.2, the v2 routing code) - and a marker that still reads GR2068 sends the next
#          author straight into a collision with this very plan.
#
#          It also carries the constant-equals-literal assertion plan 31 section 7 asks the
#          implementation stage for. That was specified as "a pin", but section 13 pins this task's
#          writeScope to three src/ files with NO tests/** path, so no pin is writable here - and task
#          04's tests are forbidden from naming the constants at all, because they do not compile at
#          task 04's time. Realized as a structural check instead, which is strictly more than the pin
#          would have covered: a unit test can compare a constant to a literal, but it cannot see the
#          marker or the reserved gaps.
#
# WHY A SOURCE-SHAPE CHECK (the #468 demotion order, worked): "the next-free marker reads GR2070" and
#          "GR2060/GR2061/GR2054 are not taken" are facts about a COMMENT and about which literals
#          exist in a constants file. Neither is observable at runtime - a wrong marker changes no
#          behaviour at all, which is exactly why it goes unnoticed until the next collision. There is
#          no test that could carry it. It ships with a committed .valid/.invalid sample pair in
#          ../samples/.
#
# MEASURED BASELINES on master @1490d2a, against the exact subject each clause scans (#478):
#          HandoffPathUnreachable ............. 0    (expected: this task adds it)
#          HandoffRowSplitAcrossTasks ......... 0    (expected: this task adds it)
#          'CURRENT next-free code: GR2070' ... 0    (expected: this task advances the marker to it)
#          '"GR2060"' / '"GR2061"' / '"GR2054"' 0 each  (expected: healthy bans, must STAY 0)
#          'GR10xx' .......................... 3    (NONZERO ON ARRIVAL, and that is correct: the
#                                                    ladder note already exists. This clause is a
#                                                    DO-NOT-DELETE ratchet, not a "make it appear"
#                                                    floor - its whole job is to notice a restatement
#                                                    being dropped while the GR20xx line is edited.
#                                                    That is the named reason it is exempt from the
#                                                    zero-baseline rule, #478.)
#          For reference the marker reads 'CURRENT next-free code: GR2068' today (count 1), which is
#          precisely what this task must change.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the verifier runs
# this script with the sample path as argv[0] AND in $env:GR_SUBJECT, so a sample-aware guardrail MUST
# let it override the hardcoded target. Without the override a sample run scans the real repo instead,
# both halves see the same untouched bytes, and BOTH exit 1 - the ValidHalfFailed shape, whose own
# diagnosis is "the guardrail may not be reading the sample at all". Author-time smoke-testing that
# stages samples into the real paths does NOT exercise this; only `guardrails samples verify` does.
#   $env:GR_SUBJECT='docs/plans/31-unattended-run-hardening/tasks/05-implement-handoff-coverage-check/samples/04-codes-and-marker.valid.cs';   <this script>  # expect 0
#   $env:GR_SUBJECT='docs/plans/31-unattended-run-hardening/tasks/05-implement-handoff-coverage-check/samples/04-codes-and-marker.invalid.cs'; <this script>  # expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Loading/DiagnosticCodes.cs' }
# GR_SUBJECT arrives ABSOLUTE from `guardrails samples verify`; joining it to the workspace would
# yield a nonsense path and PRECONDITION-fail, which reads exactly like a real finding.
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. This task edits it; an absent file means something far more wrong than a code allocation."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $full     # NEVER matched against directly, never reassigned
# Comments are NOT stripped here, deliberately and against the usual rule: the next-free marker IS a
# comment, and both it and the GR10xx ladder note are things this guardrail exists to check.
#
# The COST of that choice, stated rather than glossed: a comment CAN satisfy the `const string X =
# "GRnnnn";` clauses. Commenting out a real declaration, or writing the declaration's text inside a
# comment, passes them. An earlier revision of this header claimed "a comment cannot fake" that shape;
# it can, and only the fact that comments are kept makes it possible. Two things bound the residual:
# guardrail 01 builds the project, so a commented-out constant that anything references is a compile
# error there; and guardrail 02 runs the pins, which need the codes to actually be emitted. This
# guardrail's own contribution is the marker, the ladder and the reserved gaps - the three facts no
# compiler and no test can see - and for those, keeping comments is the only way to see them at all.
$code = $raw

# ACCUMULATE (#478): one distinguishable message per clause, dumped once at the end.
$failures = @()

# --- REQUIRED: the two constants, each bound to its literal ----------------------------------------
$required = @(
    @{ Name = 'HandoffPathUnreachable'
       Code = 'GR2068'
       Why  = 'the provably-broken half: a handoff row names a resolvable path that NO task can write.' },
    @{ Name = 'HandoffRowSplitAcrossTasks'
       Code = 'GR2069'
       Why  = 'the CONFIRM half, and the one that carries all of #553''s motivating value - both plan-28 failures are GR2069s.' }
)
foreach ($r in $required) {
    # -cmatch: C# identifiers and the code literals are case-SENSITIVE.
    $pattern = 'const\s+string\s+' + [regex]::Escape($r.Name) + '\s*=\s*"' + $r.Code + '"'
    if ($code -cnotmatch $pattern) {
        $failures += "$rel does not declare: const string $($r.Name) = ""$($r.Code)""; - $($r.Why) Spell it like its neighbours (see OpenAiCompatWeakOrUnreachable at the foot of the shipped block)."
    }
}

# --- REQUIRED: the next-free marker is advanced --------------------------------------------------
# ANCHORED at the start of a comment line, deliberately. Measured: this file carries the phrase TWICE -
# the live marker at :991, and a HISTORICAL one at :565 quoted inside another comment
# (`// "CURRENT next-free code: GR2047".`). An unanchored clause would be satisfiable by editing the
# quoted history and leaving the real marker stale, and its diagnostic would name GR2047 - a confident
# wrong value. `^\s*//\s*CURRENT` excludes the quoted form because a `"` precedes it there.
$markerPattern = '(?m)^\s*//\s*CURRENT next-free code:\s*GR2070'
if ($code -cnotmatch $markerPattern) {
    $found = @([regex]::Matches($code, '(?m)^\s*//\s*CURRENT next-free code:\s*(GR\d+)') |
               ForEach-Object { $_.Groups[1].Value })
    $stale = if ($found.Count -gt 0) { " The live marker line(s) read: $($found -join ', ')." }
             else { " No live `// CURRENT next-free code: GRnnnn` line was found at all - do not delete it, and do not move it inside a quote." }
    $failures += "$rel's next-free marker does not read GR2070.$stale This plan takes GR2068 AND GR2069, so the next allocator must be sent to GR2070 or it collides with this very change. The marker block's own note asks you to update this line when you allocate (issue #320)."
}

# --- REQUIRED: the GR10xx ladder is restated, not dropped ----------------------------------------
# The block's own note says a doc stating only one ladder is half a fact - which is how the
# domain-knowledge skill came to claim "GR1010 / GR2055" long after both were taken.
if ($code -cnotmatch 'GR10xx') {
    $failures += "$rel no longer mentions the GR10xx ladder. The two ladders advance independently, and the marker block's own note says a doc stating only one of them is half a fact. Restate it unchanged rather than dropping it while editing the GR20xx line."
}

# --- FORBIDDEN: the three reserved-by-name gaps stay unallocated ----------------------------------
$reserved = @(
    @{ Code = 'GR2060'; Owner = 'docs/plans/19-producer-coverage.md section 1 (a gate requires content nothing in the plan can produce)' },
    @{ Code = 'GR2061'; Owner = 'docs/plans/18-integration-proof-proximity.md section 3.4 (the deferred seam-ledger lint)' },
    @{ Code = 'GR2054'; Owner = 'docs/plans/17-model-tiering.md section 13.2, RoutingNumericNonPositive (the v2 code)' }
)
foreach ($g in $reserved) {
    if ($code -cmatch ('const\s+string\s+\w+\s*=\s*"' + $g.Code + '"')) {
        $failures += "$rel now allocates $($g.Code), which is RESERVED BY NAME for $($g.Owner). Take GR2068 and GR2069 as the plan specifies and leave the gap alone - a collision here is discovered months later by the design that reserved it."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== diagnostic codes and marker: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Codes sound: GR2068/GR2069 declared and bound to their literals, the next-free marker reads GR2070, the GR10xx ladder is restated, and the three reserved gaps are untouched."
exit 0
