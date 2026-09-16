# catches: a terminal-gate disclosure that RE-IMPLEMENTS the reader instead of calling it - typically by
#          reading document.Supplied directly and formatting its own "supplied by <by> at <sha10>". That
#          implementation can pass every test in SuppliedTerminalGateHaltTests: the assertions are on
#          rendered substrings, and a hand-rolled renderer that happens to produce the same substrings
#          for the fixture's inputs is indistinguishable from the real reader at that level.
#
#          It is the SECOND half that decides the matter, and it is the half a hand-rolled version
#          silently drops: UnauthoredContentNote reads BOTH supplied[] and refreshed[], and its own doc
#          comment says a consumer that reads only one of them is the defect it exists to prevent. The
#          DECIDED answer to d41-terminal-gate-names-supply is "supplied AND refreshed content". So the
#          requirement is not "produce this text" but "call THIS reader", and only a source-shape check
#          can state that.
#
# WHY A SOURCE-SHAPE CHECK: "there is one reader of unauthored content, shared with the wave gate" is a
#          statement about the FILE. Scheduler.BuildGateHalt already calls UnauthoredContentNote for wave
#          entry/exit halts; the whole point of this task is that the terminal gate joins it rather than
#          growing a parallel implementation that will drift the first time the rendering changes. No
#          behavioural assertion over one fixture can discriminate the two. Last rung of the #468
#          demotion order, and it ships with a committed .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (§11a): $raw is NEVER matched against and never reassigned. REQUIRED clauses read $code
#          (comments gone, string literals intact) so a `<see cref="UnauthoredContentNote"/>` doc comment
#          cannot satisfy them. The FORBIDDEN clauses read $scan (literals gone too) so a message string
#          mentioning a section name is invisible to the bans.
#
# MEASURED BASELINES on this task's branch point, against src/Guardrails.Cli/PlanGuardrailPhase.cs, each
# with its clause's own case sensitivity (Select-String -CaseSensitive, #478):
#   UnauthoredContentNote   0   required-present; this task's deliverable
#   HeadlineSuffix          0   required-present; this task's deliverable
#   DetailLines             0   required-present; this task's deliverable
#   \.Supplied\b            0   forbidden-present - correctly green on arrival (the file does not read
#                               the journal's sections today, which is exactly the gap being closed)
#   \.Refreshed\b           0   forbidden-present - same
# The file is a SHIPPED file this task edits in place, so these are real measured counts, not "n/a".
# No ANCESTOR task writes these tokens into this subject: this task's ancestors are
# 17-author-tests-terminal-gate-names-supply (writeScope tests/** only) and 14-implement-autoresolve-wiring
# (writeScope src/Guardrails.Core/Execution/Scheduler.cs + SchedulerFactory.cs), neither of which can
# touch src/Guardrails.Cli/PlanGuardrailPhase.cs.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the verifier runs
# this script with the sample path as argv[0] AND in $env:GR_SUBJECT. Without the override a sample run
# scans the real repo instead, both halves see the same bytes, and BOTH exit the same way - the
# ValidHalfFailed shape, whose own diagnosis is "the guardrail may not be reading the sample at all".
#   $env:GR_SUBJECT='<plan>/tasks/18-implement-terminal-gate-names-supply/samples/03-appends-the-shared-reader.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/18-implement-terminal-gate-names-supply/samples/03-appends-the-shared-reader.invalid.cs' -> expect 1
# RE-RUN BOTH after ANY edit to this file, not just the clause you touched.
$rel = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Cli/PlanGuardrailPhase.cs' }
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

# --- REQUIRED: the shipped reader, and BOTH of its halves ------------------------------------------
if ($code -cnotmatch '\bUnauthoredContentNote\b') {
    $failures += "$rel never names UnauthoredContentNote. Design 41 §6 says the terminal gate appends 'the same reader's headline suffix and detail lines' that Scheduler.BuildGateHalt already appends at a wave gate. Call the shipped reader (Guardrails.Core.Journal.UnauthoredContentNote); do not re-derive the rendering here."
}

if ($code -cnotmatch '\bHeadlineSuffix\b') {
    $failures += "$rel never calls UnauthoredContentNote.HeadlineSuffix. That is the half that puts 'supplied by overwatcher at <sha10>' on the halt headline. It returns null when neither section has an entry, which is precisely what keeps a run with no unauthored content byte-identical - so calling it is also how the never-weaker floor is satisfied, not something in tension with it."
}

if ($code -cnotmatch '\bDetailLines\b') {
    $failures += "$rel never calls UnauthoredContentNote.DetailLines. The headline suffix alone truncates every sha to 10 characters and names no paths; the detail lines are where the full commit and the supplied paths reach the operator. Write them to the heartbeatOut TextWriter this method already takes (RunCommand passes the run's own console), guarding on null."
}

# --- FORBIDDEN: no parallel implementation over the journal's two sections ---------------------------
# Reads $scan (comments AND string literals gone), anchored on a property ACCESS rather than a mention,
# so a doc comment or a message string naming a section is invisible here while a read is not.
if ($scan -cmatch '\.Supplied\b') {
    $failures += "$rel reads the journal's Supplied section directly. Hand the WHOLE JournalDocument to UnauthoredContentNote instead: it merges supplied[] and refreshed[] oldest-first across both sections, and a reader that reaches into one of them is the defect its own doc comment says it exists to prevent. It already returns null / an empty list when there is nothing to disclose, so no early-out over this property is needed."
}

if ($scan -cmatch '\.Refreshed\b') {
    $failures += "$rel reads the journal's Refreshed section directly. Same rule as Supplied above - the ordering across the two sections is the reader's job, and duplicating it here is what guarantees the two renderings drift."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== terminal-gate disclosure shape: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The terminal gate appends the SHIPPED reader's headline suffix and detail lines - the same call Scheduler.BuildGateHalt makes - and never reads supplied[]/refreshed[] itself."
    exit 1
}

Write-Output "Disclosure shape sound: both halves of UnauthoredContentNote are called, and neither journal section is read directly."
exit 0
