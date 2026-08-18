# catches: the half-wire - a change that COMPUTES the judge route and then still executes the prompt
#          on frontmatter-or-default. That compiles, passes unit tests, and is dead in production:
#          the classic #120 built-but-unwired shape one layer in. Plus its two neighbours - a judge
#          route DERIVED here instead of received (a second resolution site that will drift), and a
#          route parameter that exists but is never actually handed the actor's resolution.
#
# SOUND ABSENCE / FAIL-ON-PRESENT ONLY (#468). Each positive probe's FAILURE is conclusive: if the
# file never names the symbol, the wiring cannot have happened. Presence proves nothing on its own -
# CORRECTNESS is the sibling 02-conformance-judge-tests-pass guardrail's job, driving the REAL seam.
$ErrorActionPreference = 'Continue'
$file = 'src/Guardrails.Core/Execution/GuardrailRunner.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist"
    exit 1
}
$raw = Get-Content -Raw $file
# Comment-stripped: a comment naming the call is not a call (#97/#98).
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')

# The dotted CALL, not a bare name (#76) - a local of the same name, or a mention, must not satisfy it.
if ($code -cnotmatch 'TierResolver\s*\.\s*ResolveJudge\s*\(') {
    $failures += 'GuardrailRunner never CALLS TierResolver.ResolveJudge in real code - the judge is still resolved from frontmatter-or-default with no tier awareness, which is the state this task exists to change'
}

# The resolved datum must be exposed, or task 08 has nothing to carry to the journal (#474/#475).
if ($code -cnotmatch 'JudgeResolution') {
    $failures += 'GuardrailRunner never mentions JudgeResolution in real code - even a correct resolution is useless if it stays a local: task 08 carries it to the journal and cannot invent what this task does not expose. Wave 2 shipped a live instance of exactly this (#475: AttemptRecord.Usage is declared, is READ by the per-tier spend aggregation, and is assigned by none of its twelve construction sites - structurally dead, every guardrail green).'
}

# --- the SECOND-DERIVATION ban, fail-on-present over stripped source (#176) ----------------------
# GuardrailRunner must RECEIVE the actor route, never compute one. Two resolution sites drift: one
# gets a fix, the other does not, and the judge is graded against a rung the actor never ran at. The
# ACTOR entry point is Resolve( ; the judge one is ResolveJudge( and is REQUIRED above - the pattern
# below cannot match it, because after "Resolve" it demands optional whitespace then "(", and
# ResolveJudge( has "J" there. Verified against both spellings, not assumed.
$actorCalls = [regex]::Matches($code, 'TierResolver\s*\.\s*Resolve\s*\(')
if ($actorCalls.Count -gt 0) {
    $failures += "GuardrailRunner CALLS TierResolver.Resolve( $($actorCalls.Count) time(s) - FORBIDDEN. The ACTOR route must be THREADED in from TaskExecutor (which already resolves it once into a route local and hands it to _actionRunner.RunAsync), not re-derived here. A second derivation is the divergence wave 2 severed once already."
}

# --- the THREADING itself, proven in the CALLER --------------------------------------------------
# Without this the route parameter can exist and be passed null forever: the judge would resolve
# against nothing, every 6.5 rule would fall to its default, and every probe above would stay green.
$exec = 'src/Guardrails.Core/Execution/TaskExecutor.cs'
if (-not (Test-Path $exec)) {
    $failures += "$exec does not exist - it is the caller that must hand GuardrailRunner the actor route"
} else {
    $execRaw  = Get-Content -Raw $exec
    $execCode = [regex]::Replace($execRaw, '/\*[\s\S]*?\*/', '')
    $execCode = [regex]::Replace($execCode, '(?m)//.*$', '')

    # The route local wave 2 established, still resolved exactly once.
    if ($execCode -cnotmatch 'ResolveRoute\s*\(') {
        $failures += 'TaskExecutor no longer resolves the actor route via ResolveRoute( - that local is what BOTH the action path and (now) the guardrail path must share; do not remove or rename it'
    }

    # Matched across the multi-line argument list up to the closing paren, so argument ORDER and line
    # breaks are unconstrained. One nesting level of parens inside the arg list is tolerated.
    $callSites = [regex]::Matches($execCode, '_guardrailRunner\s*\.\s*RunAsync\s*\((?:[^()]|\([^()]*\))*\)')
    if ($callSites.Count -eq 0) {
        $failures += 'no _guardrailRunner.RunAsync( call site found in TaskExecutor - the guardrail path was restructured in a way this wave did not anticipate; halt rather than guess'
    } else {
        # RevalidateAsync has no attempt and no actor route and correctly passes null, so ONE call
        # site carrying the route is the bar - not all of them.
        $carrying = @($callSites | Where-Object { $_.Value -cmatch '\broute\b' }).Count
        if ($carrying -lt 1) {
            $failures += "found $($callSites.Count) _guardrailRunner.RunAsync( call site(s), NONE of which passes the route local. A route parameter never handed the actor resolution resolves the judge against nothing: every 6.5 rule falls to its default and every other probe here stays green. Pass the SAME route local the action path already gets."
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== judge wiring: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Note what this check CANNOT tell you: that the resolved block is the one the invocation actually EXECUTES on, and that EffectiveSettings(isGuardrail: true) is called on the RESOLVED judge block rather than the actor's or the frontmatter's. Get that wrong and rule 7 silently mis-profiles every bumped judge with another block's permissions and turn budget. The conformance clauses are what prove it."
    exit 1
}
exit 0
