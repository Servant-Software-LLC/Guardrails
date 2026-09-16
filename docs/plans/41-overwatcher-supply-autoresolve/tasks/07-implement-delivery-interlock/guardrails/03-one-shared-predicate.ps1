# catches: the ONE wrong implementation the tests cannot see - two token lists that happen to AGREE.
#          Design 41 fact 11: SuppressingDecision (run end) and SuppressingDecisionForDelivery (every
#          wave barrier) each listed the suppressing tokens separately, and adding auto-supplied to
#          BOTH lists passes every test in this pair while leaving the defect exactly where it was.
#          The next token then leaks machine-decided work onto the user's branch at a barrier, and no
#          proof run that passes --no-merge-on-success would notice. Only the SHARED definition removes
#          it, and only a source-shape check can assert "spelled once" - no test can (#468: a property
#          about the SHAPE of the code, not its behaviour).
#
# TWO-LEVEL STRIP (dotnet.md 11a): $raw is NEVER matched against and never reassigned. Every clause here
#          is REQUIRED-present / a COUNT over C# identifiers, so all of them read $code (comments gone,
#          string literals intact). There is no forbidden-present clause, so no $scan level is derived.
#
# Author-time smoke test (#302), re-runnable (#468) - run BOTH halves after ANY edit to this file:
#   $D = 'docs/plans/41-overwatcher-supply-autoresolve/tasks/07-implement-delivery-interlock/samples'
#   $env:GR_SUBJECT="$D/03-one-shared-predicate.valid.cs";   ./03-one-shared-predicate.ps1   # expect 0
#   $env:GR_SUBJECT="$D/03-one-shared-predicate.invalid.cs"; ./03-one-shared-predicate.ps1   # expect 1
#   Remove-Item Env:\GR_SUBJECT
#
# MEASURED BASELINES on the untouched tree, against the exact subject each clause scans, with the SAME
# case sensitivity as the operator (#478). Counted over $code (comments stripped), not raw:
#   private\s+static\s+bool\s+\w+\s*\(\s*DecisionEntry\s+\w+\s*\)   0  (no shared predicate exists yet)
#   DecisionTokens\.AutoSupplied                                    0  (the token is added by task 06)
#   DecisionTokens\.ProceededBestGuess                              2  <- DECLARED NONZERO: this IS fact
#        11. Two occurrences = the two separate token lists, so the "exactly once" clause below is RED on
#        arrival and becomes green only when both spellings share one predicate. Recorded here because a
#        reviewer must be able to re-measure it rather than take the clause on trust.
#   No ancestor task's prompt or writeScope writes these tokens into this subject: task 06 is the only
#        ancestor touching RunOutcomePolicy.cs, and it ADDS the throwing predicate this task implements.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# GR_SUBJECT is the `guardrails samples verify` contract: the verifier runs this script with the sample
# path in $env:GR_SUBJECT. It arrives ABSOLUTE, so joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Execution/RunOutcomePolicy.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the only early exit: every clause below would read an empty string and report the whole
# implementation missing, a confident wrong message aimed at code that may be perfectly fine.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. This is NOT a finding about the interlock."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                 # NEVER matched against, never reassigned
# #561 / GR2037 — neutralize string literals BEFORE stripping comments. Deriving $code by stripping
# comments straight from $raw lets a literal that spells '/*' forge a delimiter and blank real code before
# any literal pass runs. Only / and * INSIDE literals are replaced, so a literal's CONTENT still satisfies
# a required-present clause (a [Trait("Category", ...)] attribute value is the measured case, #470).
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })   # raw strings
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })  # verbatim
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })  # ordinary
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', ' ')      # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')      # // and /// line comments

# ACCUMULATE (#478): one distinguishable message per clause, dumped once - never an exit-1 chain that
# reports one gap per attempt.
$failures = @()

# CLAUSE 1 - the shared predicate exists. -cmatch/-cnotmatch throughout: C# identifiers are
# case-SENSITIVE and PowerShell -match is not, so a case-insensitive required clause false-GREENS on
# text C# would never compile (taxonomy 3).
$decl = [regex]::Match($code, 'private\s+static\s+bool\s+(?<n>\w+)\s*\(\s*DecisionEntry\s+\w+\s*\)')
if (-not $decl.Success) {
    $failures += "$rel declares no shared predicate. Expected one private static bool taking a DecisionEntry - the single place the delivery-interlock token set is written (design 41 section 6). Both SuppressingDecision and SuppressingDecisionForDelivery must be defined in terms of it."
}
else {
    # CLAUSE 2 - and BOTH spellings actually use it: declaration + 2 call sites = 3 occurrences of the
    # name. A predicate declared and called from only one spelling is fact 11 with an extra step.
    $pred = $decl.Groups['n'].Value
    $uses = ([regex]::Matches($code, '\b' + [regex]::Escape($pred) + '\b')).Count
    if ($uses -lt 3) {
        $failures += "$rel declares the shared predicate '$pred' but its name appears only $uses time(s) - expected at least 3 (the declaration, plus a call from SuppressingDecision AND a call from SuppressingDecisionForDelivery). A predicate only one spelling calls leaves the other spelling with its own token list, which is the defect this task exists to close."
    }
}

# CLAUSE 3 - auto-supplied is spelled ONCE. More than once means it was pasted into both spellings:
# the tests all pass, and the NEXT token added is the one that leaks.
$autoSupplied = ([regex]::Matches($code, 'DecisionTokens\.AutoSupplied')).Count
if ($autoSupplied -ne 1) {
    $failures += "$rel references DecisionTokens.AutoSupplied $autoSupplied time(s); expected exactly 1, inside the shared predicate. 0 means the new token never reaches the interlock at all; 2+ means it was listed in both spellings separately - which passes every test in this pair and leaves fact 11 exactly where it was."
}

# CLAUSE 4 - and so is proceeded-best-guess. This is the clause that is RED on arrival (measured: 2),
# because those two occurrences ARE the duplicated token lists.
$bestGuess = ([regex]::Matches($code, 'DecisionTokens\.ProceededBestGuess')).Count
if ($bestGuess -ne 1) {
    $failures += "$rel references DecisionTokens.ProceededBestGuess $bestGuess time(s); expected exactly 1, inside the shared predicate. Two occurrences is the shipped defect (fact 11): the run-end and wave-barrier spellings each list the tokens themselves. Move the set into the predicate rather than keeping two lists that agree today. (DecisionTokens.ProceededUnreviewed legitimately appears twice - once in the predicate and once in ProceededUnreviewedWaveCount, which is NOT part of the interlock.)"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== one-shared-predicate: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Design 41 section 6: ONE private predicate ('this decision holds delivery'), true for proceeded-best-guess, proceeded-unreviewed and auto-supplied, with BOTH SuppressingDecision and SuppressingDecisionForDelivery defined in terms of it."
    exit 1
}

Write-Output "One shared predicate: declared over DecisionEntry, used by both spellings, and each interlock token spelled exactly once."
exit 0
