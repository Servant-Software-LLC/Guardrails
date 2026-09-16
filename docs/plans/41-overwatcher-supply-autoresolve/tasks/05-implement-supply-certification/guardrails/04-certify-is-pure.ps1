# catches: a Certify that reaches for the world. Design 41 §3.1 makes the gate a PURE function - no
#          git, no journal, no filesystem - and every test passes without that property: a Certify
#          that opened the journal to double-check 'already auto-resolved', or stat'ed the candidate
#          to be sure it is really there, returns the same verdicts on every pinned row.
#          The property is what makes the gate trustworthy rather than merely correct today. Purity
#          means everything it decides on was established by the harness BEFORE it ran, so there is
#          nothing for it to look up, nothing to be slow, nothing to throw inside the integration
#          lock, and no second reader of git whose answer could disagree with MissingResourceFacts'.
#          The shipped Resolve had all four of those problems, which is how it came to be gated on a
#          staged-file premise no production path reached.
#          FAIL-ON-PRESENT, anchored on a USE (a member access or a type position), never a bare word.
#
# Author-time smoke test (#302): this script is parameterised on the same GR_SUBJECT as its sibling
# 03, and was smoke-tested against 03's committed sample pair - the .valid sample (a post-deletion
# OverwatchDecision.cs) must exit 0 here, and the .invalid sample exits 0 here too, because its one
# defect is a surviving Resolve DECLARATION, not an impure call. That is the pair behaving correctly:
# these two scripts check independent properties and neither stands in for the other.
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/05-implement-supply-certification/samples/03-deleted-members-are-gone.valid.cs'; ./04-certify-is-pure.ps1  # expect 0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/OverwatchDecision.cs" }

$failures = @()
if (-not (Test-Path $f)) { Write-Output "$f does not exist"; exit 1 }   # PRECONDITION - the only early exit

# Variable discipline: $raw is NEVER matched against; the REQUIRED clause reads $code; the FORBIDDEN
# clauses read $scan. Literals are neutralized BEFORE comments are stripped (#561).
$raw  = Get-Content $f -Raw
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')

# baseline counts on the untouched tree - MEASURED with Select-String -CaseSensitive:
#   static\s+SupplyCertification\s+Certify\s*\(   0   (the one required clause)
# The ban clauses are NOT censused: forbidden-present polarity. For the record they measured, RAW,
# RunJournal 2 / SuppliedDrain 2 / PlanDefinition 2 - and in each case one of the two hits is a <see
# cref> doc comment that $scan strips, so the clause reads 1, 1 and 2 respectively. All are RED on
# arrival BY DESIGN: removing them is this task's deliverable (the ordinary TDD story), not the #470
# unsatisfiable-clause collision. Checked: no required clause contains any banned token.

# --- required: there is still a gate here ---------------------------------------------------------
if ($code -cnotmatch 'static\s+SupplyCertification\s+Certify\s*\(') {
    $failures += "$f declares no 'static SupplyCertification Certify(...)' — a file with no gate in it is trivially pure, which is not the property being checked."
}

# --- forbidden: the three things a pure gate cannot touch -----------------------------------------
if ($scan -cmatch '\bRunJournal\b') {
    $failures += "$f names RunJournal — Certify is a pure function (design 41 §3.1) and takes no journal. 'Already auto-resolved' is a tier-2 stop the Scheduler reads from decisions[] BEFORE the consult; re-reading it here would be a second, disagreeing answer to a question already settled."
}
if ($scan -cmatch '\bSuppliedDrain\b') {
    $failures += "$f names SuppliedDrain — the gate CERTIFIES, it never commits. The Scheduler performs the commit under the integration lock, against the integration worktree; draining from here is the shipped Resolve's defect (#712) returning."
}
if ($scan -cmatch '\bPlanDefinition\b') {
    $failures += "$f names PlanDefinition — it carries Workspace and PlanDirectory, and §3.4 deletes the gate's plan.Workspace target specifically. The gate's inputs are the dial, the candidate facts and the parsed proposal; nothing else."
}
if ($scan -cmatch '\bFile\s*\.' -or $scan -cmatch '\bDirectory\s*\.') {
    $failures += "$f touches the filesystem (File. / Directory.) — a pure gate decides on facts the harness already established. Whether the file is really there was settled by MissingResourceFacts, and re-checking it here would be a second reader of the same truth."
}
if ($scan -cmatch '\bProcessStartInfo\b' -or $scan -cmatch '\bProcess\s*\.\s*Start\b') {
    $failures += "$f starts a process — the gate runs no git. Every git fact it needs arrived as a MissingResourceCandidate, and a git call here would run inside the settle path where it can be slow or throw."
}

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output $_ }; exit 1 }
# ${f}, not "$f:" — PowerShell reads '$f:' in a double-quoted string as a scope-qualified variable
# reference and fails to parse the script (#473: an unparseable guardrail cannot report anything).
Write-Output "${f}: Certify is declared and the file reaches no journal, no drain, no plan, no filesystem and no process."
exit 0
