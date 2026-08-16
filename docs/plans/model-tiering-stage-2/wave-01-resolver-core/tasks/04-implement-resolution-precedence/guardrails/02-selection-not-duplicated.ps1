# catches: a Resolve() that grows its OWN candidate-filtering path instead of delegating to
#          SelectCandidate. The precedence tests pass either way, so only a structural check catches
#          it. It matters because the costly floor and the never-weaker climb live in SelectCandidate.
#
# ==> This check is FILE-WIDE on purpose, and that is the second design of this guardrail. The first
#     tried to extract Resolve's own body with `(?ms)...Resolve\s*\(...\)\s*\{(.*?)^\s{0,8}\}` and was
#     MEASURED to be worse than useless: this repo is 197/197 file-scoped namespaces, so methods sit
#     at 4 spaces and every nested block closes at 8 - the regex truncated at the FIRST nested `}`.
#     It false-GREENED a Resolve carrying a decoy SelectCandidate call in its opening `if` followed by
#     an inline `.Where(...Tiers.Contains...).OrderByDescending(...)` inversion (verbatim the thing
#     this guardrail exists to catch), and false-RED a correct delegating Resolve whose `if`s were
#     braced - i.e. the verdict on a correct implementation was decided by brace style.
#     File-wide is SOUND here because SelectCandidate must not contain these constructs either.
$path = "src/Guardrails.Core/Prompts/TierResolver.cs"
if (-not (Test-Path $path)) {
    Write-Output "$path does not exist"
    exit 1
}

$raw    = Get-Content -Raw -Path $path
$noRaw  = [regex]::Replace($raw, '"""[\s\S]*?"""', '""')
$noVerb = [regex]::Replace($noRaw, '@"(?:[^"]|"")*"', '""')
$noStr  = [regex]::Replace($noVerb, '"(\\.|[^"\\])*"', '""')
$code   = [regex]::Replace($noStr, '/\*[\s\S]*?\*/', '')
$code   = [regex]::Replace($code, '(?m)//.*$', '')
$failed = $false

# POSITIVE: SelectCandidate must be CALLED, not merely declared. Keyed on an OCCURRENCE COUNT
# (declaration + at least one call) rather than on a call-site prefix alternation: the prefix form
# omitted ':' and '?' and false-RED a correct ternary
# `return eff is null ? Legacy(c) : SelectCandidate(c, eff.Value);` (measured).
$occurrences = [regex]::Matches($code, '\bSelectCandidate\s*\(').Count
if ($occurrences -lt 2) {
    Write-Output "$path declares SelectCandidate but never CALLS it ($occurrences occurrence(s); a declaration plus at least one call is expected). Resolve MUST delegate selection rather than re-derive it, or the costly floor and the never-weaker climb are bypassed (6.1 item 2 / 6.2)."
    $failed = $true
}

# NEGATIVE: descending ordering anywhere in the resolver inverts the cost thesis of the whole epic
# (6.2 orders ASCENDING so the WEAKEST capable model wins) while every behavioural test still passes.
# Note this does NOT require `OrderBy(` to be present - a correct implementation may use MinBy,
# List.Sort with a comparer, or a single-pass scan, and an earlier version false-RED all three.
if ($code -cmatch 'OrderByDescending\s*\(') {
    Write-Output "$path orders candidates DESCENDING - 6.2 orders by ASCENDING strength so the WEAKEST capable model wins. Descending inverts the cost thesis of the whole epic."
    $failed = $true
}

# NEGATIVE: the candidacy predicate must not be re-inlined anywhere in this file (D22a). This task
# edits the same file task 02 did, so a duplication introduced now must fail the same way.
if ($code -cmatch '\.Tiers\s*\.\s*(?:Contains|Any|Exists|IndexOf)\s*\(') {
    Write-Output "$path tests routing.Tiers membership directly - that IS the candidacy predicate. Call ServesTier(tier) so validate's GR2048 check and the runtime resolver can never disagree (D22a)."
    $failed = $true
}

if ($failed) { exit 1 }
exit 0
