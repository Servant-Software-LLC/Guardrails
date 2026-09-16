# catches: the cheapest wrong implementation of design 41 §2.1 - CALLING MissingResourceSignal while
#          LEAVING the private ResourcePathToken regex in place beside it. The tests in
#          SuppliedHaltTextTests cannot see that: they assert only on rendered text, so an implementation
#          that satisfies every one of them and still keeps a second, divergent spelling of "names a
#          path" in this file passes the whole pair. §2.1's requirement is that there be ONE predicate,
#          shared by the halt text and the overwatcher consult; two spellings is precisely the defect it
#          exists to remove, and the survivor is what the next reader reaches for.
#
#          It also pins the two wording ratchets, which are likewise invisible to a text-only test once
#          the new assertions pass: the option Description must stop ENUMERATING the two tokens, and the
#          banner must stop calling a machine decision a best-guess.
#
# WHY A SOURCE-SHAPE CHECK: "there is exactly one producer of this match" is a statement about the FILE,
#          not about any output it can produce. Both the one-predicate and the two-predicate versions
#          render identical text for every input the suite can supply - that is what makes the leftover
#          copy survivable - so no behavioural assertion can discriminate them. Last rung of the #468
#          demotion order, and it ships with a committed .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (§11a): $raw is NEVER matched against and never reassigned. REQUIRED clauses read
#          $code (comments gone, string literals INTACT); the two wording bans ALSO read $code, because
#          the strings they ban are option/banner text that lives in string literals - reading $scan
#          there would strip the very thing being checked. The ResourcePathToken ban reads $scan, so a
#          doc comment explaining why the private regex was removed does not re-trip it.
#
# MEASURED BASELINES on this task's branch point, against src/Guardrails.Cli/Commands/RunCommand.cs,
# each with its clause's own case sensitivity (Select-String -CaseSensitive, #478):
#   MissingResourceSignal                         0   required-present; this task's deliverable
#   ResourcePathToken                             2   forbidden-present, NONZERO BY DESIGN - see below
#   proceeded-best-guess / proceeded-unreviewed   1   forbidden-present, NONZERO BY DESIGN - see below
#   A best-guess that a later attempt             1   forbidden-present, NONZERO BY DESIGN - see below
#
# The three nonzero counts are DECLARED RATCHETS, the named-reason case §478 allows: each names text
# that EXISTS on the base and whose DELETION is this task's deliverable, so "red on arrival" is the
# intended starting state, not the #470 collision. They are forbidden-present clauses, which the
# baseline-zero rule does not govern anyway.
#
# A clause deliberately NOT written: `machine decision` as a required-present term. It is already
# present TWICE on the untouched tree (RunCommand.cs lines 65 and 2424), so the clause would be
# satisfied before this task did anything - the exact pre-satisfaction §478 exists to catch. The bans on
# the enumeration and on "A best-guess" carry the wording requirement instead, because those are the
# strings that must GO.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the verifier runs
# this script with the sample path as argv[0] AND in $env:GR_SUBJECT. Without the override a sample run
# scans the real repo instead, both halves see the same bytes, and BOTH exit the same way - the
# ValidHalfFailed shape, whose own diagnosis is "the guardrail may not be reading the sample at all".
#   $env:GR_SUBJECT='<plan>/tasks/16-implement-halt-text-and-interlock-wording/samples/03-halt-text-uses-the-shared-predicate.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/16-implement-halt-text-and-interlock-wording/samples/03-halt-text-uses-the-shared-predicate.invalid.cs' -> expect 1
# RE-RUN BOTH after ANY edit to this file, not just the clause you touched.
$rel = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Cli/Commands/RunCommand.cs' }
# GR_SUBJECT arrives ABSOLUTE; joining it to the workspace would yield a nonsense path and
# PRECONDITION-fail, which reads exactly like a real finding.
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is a SHIPPED file this task edits in place; guardrail 01 would have failed first if it were merely broken."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                 # NEVER matched against, never reassigned
# #561 / GR2037 — neutralize string literals BEFORE stripping comments. This file derives $scan from
# $code, so getting the order wrong here corrupted BOTH levels: a literal spelling '/*' forged a delimiter
# and blanked real code before any literal pass ran. Only / and * INSIDE literals are replaced here, so a
# literal's CONTENT still satisfies a required-present $code clause (#470); $scan below still blanks
# literal content outright for the forbidden clauses.
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })   # raw strings
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })  # verbatim
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })  # ordinary
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', ' ')      # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')      # // and /// line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')     # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')    # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')    # ordinary strings

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: the shared predicate is actually CALLED from this file ------------------------------
if ($code -cnotmatch '\bMissingResourceSignal\b') {
    $failures += "$rel never names MissingResourceSignal. Design 41 §2.1 moves the path-token match out of this file into Guardrails.Core as ONE predicate shared by the halt text and the overwatcher's missing-resource consult. Grep the real API before calling it - `grep -n 'public' src/Guardrails.Core/Execution/MissingResourceSignal.cs` - and do not re-derive the match here."
}

# --- FORBIDDEN: the private regex may not survive alongside the shared predicate --------------------
# Reads $scan (comments AND string literals gone), so a doc comment recording the removal does not
# re-trip the ban. NONZERO ON ARRIVAL BY DESIGN: deleting this member is the deliverable.
if ($scan -cmatch '\bResourcePathToken\b') {
    $failures += "$rel still declares or uses ResourcePathToken. Deleting it is the point: §2.1 requires ONE producer of the path-token match, and the tests cannot see a leftover private copy because both spellings render identical text for every input the suite supplies. Remove the field and its use; MissingResourceSignal replaces both."
}

# RENAME-PROOF SECOND ANCHOR (review finding). The clause above bans an IDENTIFIER, and an identifier is
# something the author may freely vary: keeping the private regex and renaming it PathTokenPattern, beside
# a real MissingResourceSignal call, satisfies every other clause here. So also ban the CONSTRUCT.
# Reads $raw DELIBERATELY: the declaration's pattern lives in a string literal, which $scan blanks and in
# which $safe rewrites '/' to '#', so neither derived level can see it.
# MEASURED on the untouched tree: 'private static readonly Regex' occurs EXACTLY ONCE in this file, at the
# ResourcePathToken declaration this task deletes -> baseline 1, a removal deliverable, red on arrival.
# OVER-MATCH CHECK (#470): that single occurrence IS the member being removed, so the ban cannot false-red
# code that must survive. (A ban on 'new Regex(' or '[GeneratedRegex' was considered and REJECTED: the
# declaration uses a target-typed `new(...)`, so both measure 0 today AND 0 after a rename - a clause that
# could never fire, which is the defect this review just removed from the wording ban below.)
if ($raw -cmatch 'private static readonly Regex') {
    $failures += "$rel still declares a private compiled Regex for the path-token match. Renaming ResourcePathToken does not satisfy §2.1 - it requires ONE producer, and a second live spelling drifts silently because both render identical text for every input the suite supplies. Delete the declaration and call MissingResourceSignal."
}

# --- FORBIDDEN: the two wording ratchets ------------------------------------------------------------
# These read $code, NOT $scan: the banned strings are option and banner text living in string literals,
# which $scan would strip - the clause would then be vacuously green however the wording was left.
#
# THE SEPARATOR IS MATCHED AS '.', NOT AS '/', AND THAT IS THE WHOLE FIX (review finding).
# The first spelling of this clause hunted for the literal 'proceeded-best-guess / proceeded-unreviewed'
# and COULD NEVER FIRE. The $safe pass above replaces / and * INSIDE string literals with '#', and this
# banned text IS a string literal (the --merge-on-success Description), so by the time $code exists it
# reads 'proceeded-best-guess # proceeded-unreviewed'. Proven with a mutant: a file keeping the
# enumeration verbatim exited 0. Its DECLARED baseline was wrong too - raw 1, $code 0 - which is why the
# per-CLAUSE census (#478) catches this and a per-SCRIPT red never could: the script exited 1 the whole
# time, on its siblings.
# '.' matches the neutralized '#' and an un-neutralized '/' alike, so the clause now fires either way.
# RE-MEASURED after the change, over this script's own $code: 1 (a removal deliverable, red on arrival).
if ($code -cmatch 'proceeded-best-guess\s*.\s*proceeded-unreviewed') {
    $failures += "$rel still enumerates 'proceeded-best-guess / proceeded-unreviewed' in the --merge-on-success description. Design 41 §6 puts a THIRD token, auto-supplied, into the one shared delivery-interlock set, so the enumeration - and the 'this flag matters only for those two cases' tail beside it - are now false. Name a machine decision without enumerating only those two."
}

if ($code -cmatch 'A best-guess that a later attempt') {
    $failures += "$rel still tells the operator 'A best-guess that a later attempt superseded is stale' in the #597 JUDGE THE DECISION FIRST block. An auto-supplied decision is not a guess - it is a machine decision that put a file in the tree. Generalise that sentence. Leave the surrounding interpolation of suppressing.Decision / suppressing.Subject alone: it is already token-agnostic, which is why the banner needs no per-token knowledge."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== shared-predicate / interlock wording: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "One predicate produces the path-token match (§2.1), and the interlock wording names a machine decision rather than two specific tokens (§6)."
    exit 1
}

Write-Output "Shared predicate sound: MissingResourceSignal is the single producer of the path-token match, and neither wording ratchet remains."
exit 0
