# catches: a capture that is COMPUTED ON ACCESS rather than held from load - a Lazy<>, a ??=, or (the
#          form that actually defeated draft 2 of section 9) an expression-bodied property:
#
#              public string DefinitionHashAtLoad => TaskDefinitionHash.Compute(this);
#
#          That line satisfies every write site "reads .DefinitionHashAtLoad" check verbatim while
#          leaving the defect 100% intact: the hash is still computed from CURRENT DISK, just one call
#          frame further away. Section 11 names it first among the things an unattended run of this plan
#          must not be allowed to do, and says why no behavioural test reliably catches it: a lazy
#          capture is byte-identical to an eager one on every run in which nobody edits the plan folder
#          inside the exact window, which is every run the suite exercises.
#
#          The check is therefore a SHAPE check, not a call-site check: a property that is a bodiless
#          auto-property CANNOT compute anything in any syntax, and a file that never mentions the hash
#          function cannot call it. Two clauses, jointly closing the whole family rather than the one
#          spelling an author happened to imagine (Risk 6's lesson - two earlier greps were both
#          satisfied by the UNFIXED tree).
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): the property is "this value
#          was computed at construction and never recomputed". At runtime an eager capture and a lazy one
#          are INDISTINGUISHABLE unless the underlying bytes change between construction and first
#          access - which is exactly the mid-run window this plan exists to make observable, and which no
#          unit test can create for a property nothing has called yet. The ideal instrument would be an
#          AGREEMENT property test, and there is no second side to compare against: a lazy implementation
#          that agrees on every input the suite can supply is the definition of this defect. Genuinely
#          unobservable at runtime, so this is the demotion order's last rung, and it ships with a
#          committed .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. REQUIRED clauses
#          read $code (comments gone, string literals intact); FORBIDDEN clauses read $scan (literals
#          gone too), so a doc comment saying "we deliberately do not call TaskDefinitionHash here" and a
#          message string naming it are both invisible to the bans.
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, against src/Guardrails.Core/Model/
#          TaskNode.cs, with each clause's own case sensitivity (#478):
#            DefinitionHashAtLoad   0    this task's deliverable
#            DefinitionFilesAtLoad  0    this task's deliverable
#            TaskDefinitionHash     0    forbidden-present; exempt from the baseline rule anyway, but
#                                        recorded because the whole clause rests on it being 0 TODAY -
#                                        the type genuinely does not mention the hasher, so the ban
#                                        costs a correct implementation nothing.
#            WaveDefinitionHash     0    same. (Note WaveNode.cs is NOT 0 - it carries a <see cref> doc
#                                        comment - which is why the equivalent anchor in stage 6 must
#                                        strip comments. This file needs no such carve-out.)
#            Lazy<                  0    forbidden-present.
#            =>                     0    the file today has no expression-bodied member at all.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the verifier runs
# this script with the sample path as argv[0] AND in $env:GR_SUBJECT. Without the override a sample run
# scans the real repo instead, both halves see the same untouched bytes, and BOTH exit the same way -
# the ValidHalfFailed shape, whose own diagnosis is "the guardrail may not be reading the sample at all".
# Author-time smoke-testing that stages samples into the real paths does NOT exercise this; only
# `guardrails samples verify` does.
#   $env:GR_SUBJECT='<plan>/tasks/03-pin-the-definition-at-load/samples/02-captures-are-bodiless-autoproperties.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/03-pin-the-definition-at-load/samples/02-captures-are-bodiless-autoproperties.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Model/TaskNode.cs' }
# GR_SUBJECT arrives ABSOLUTE; joining it to the workspace would yield a nonsense path and
# PRECONDITION-fail, which reads exactly like a real finding.
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

# --- REQUIRED: both captures declared as BODILESS AUTO-PROPERTIES ----------------------------------
# Keyed on the declaration UP TO THE OPENING BRACE (section 3.1) so accessor ORDER is irrelevant -
# `{ get; init; }` and `{ init; get; }` are the same property, and a regex anchored on a leading `get`
# would false-fail on one of them. The brace is what does the work: an expression-bodied property has
# `=>` where this expects `{`, so the defeating form cannot match.
# -cmatch throughout: C# identifiers are case-SENSITIVE and PowerShell -match is not, so a
# case-insensitive require-present clause false-GREENS on text C# would never compile (taxonomy 3).
$aggregate = [regex]::Match($code, '(?m)public\s+string\?\s+DefinitionHashAtLoad\s*\{([^}]*)\}')
if (-not $aggregate.Success) {
    $failures += "no bodiless auto-property public string? DefinitionHashAtLoad { ... } in $rel. Section 5.2 pins the shape: NULLABLE (not required - src has one construction site, tests have 27 across 21 files, and required would drag tests/** into a stage section 11 forbids from holding it), and a BRACE rather than =>. An expression-bodied form is the cheapest wrong implementation of this whole plan: it satisfies every write-site check while still computing from current disk."
}
elseif ($aggregate.Groups[1].Value -cnotmatch '\bget\b' -or $aggregate.Groups[1].Value -cnotmatch '\binit\b') {
    $failures += "DefinitionHashAtLoad's accessor block is not { get; init; } (in either order). The loader is the only writer and the value must never change afterwards, which is what makes 'the pin's lifetime is the TaskNode's lifetime' a structural argument rather than a checklist (section 5.2)."
}

$map = [regex]::Match($code, '(?m)public\s+.+?\?\s+DefinitionFilesAtLoad\s*\{([^}]*)\}')
if (-not $map.Success) {
    $failures += "no bodiless auto-property public <nullable dictionary type> DefinitionFilesAtLoad { ... } in $rel. This is the PER-FILE map the divergence gate diffs, and section 5.2 is explicit that it is NOT deferrable to milestone C: stage 13's writeScope cannot reach this file, so if the map is not here it is nowhere. A single aggregate string cannot serve the gate at all - it carries no per-file state, and the gate has to name WHICH files moved."
}
elseif ($map.Groups[1].Value -cnotmatch '\bget\b' -or $map.Groups[1].Value -cnotmatch '\binit\b') {
    $failures += "DefinitionFilesAtLoad's accessor block is not { get; init; } (in either order) - same reason as the aggregate above."
}

# --- FORBIDDEN: this type may not mention a hash function, or defer anything --------------------------
# Reads $scan (comments AND string literals gone), anchored on a USE rather than a mention (#470/#76): a
# doc comment or a message string naming the hasher is invisible here, a call is not.
if ($scan -cmatch '\bTaskDefinitionHash\b' -or $scan -cmatch '\bWaveDefinitionHash\b') {
    $failures += "$rel names a definition-hash class in CODE. Section 9's declaration-shape anchor: this type must contain ZERO occurrences of TaskDefinitionHash / WaveDefinitionHash, because a property that cannot NAME the hash function cannot compute it lazily in any syntax. That is what defeats the expression-bodied form; a call-site check does not. The capture is computed by the LOADER (PlanLoader.LoadTask) and handed in."
}
if ($scan -cmatch '\bLazy\s*<') {
    $failures += "$rel uses Lazy<>. Section 11: 'DefinitionHashAtLoad must not become lazy. A Lazy<>, a ??=, or a computed property that reads disk on access passes every test that does not edit inside the exact window, and silently restores the defect.'"
}
if ($scan -cmatch 'DefinitionHashAtLoad\s*\?\?' -or $scan -cmatch 'DefinitionFilesAtLoad\s*\?\?') {
    $failures += "$rel coalesces off one of the captures (?? ... / ??= ...). A null pin records a NULL hash - there is no fallback to disk at any site, ever (section 5.2). The null case is already handled by SSOT section 7.2's 'recorded hash absent => unknown, assume unchanged', and in production it is unreachable because the loader is the only constructor."
}
if ($scan -cmatch 'required\s+[^\s;=]+\s+DefinitionHashAtLoad' -or $scan -cmatch 'required\s+[^\s;=]+\s+DefinitionFilesAtLoad') {
    $failures += "$rel declares one of the captures required. Section 5.2 decides against it deliberately: tests/** contains 27 new TaskNode expressions across 21 files, and required would turn a two-file change into a repo-wide test edit inside a stage that may not write tests/** at all."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== load-time capture shape: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Both captures are bodiless, nullable, init-only auto-properties. The LOADER computes them (guardrail 03 checks that half); this type only holds them."
    exit 1
}
Write-Output "Capture shape sound: both pins are bodiless nullable init-only auto-properties, and this type names no hash function, no Lazy and no coalescing fallback."
exit 0
