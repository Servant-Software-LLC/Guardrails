# catches: a `samples verify` verb that re-implements pair discovery, the two-way sample binding and
#          the mismatch classification INSIDE the CLI instead of driving the shared SampleVerifier.
#          A private copy passes this task's reachability smoke perfectly well - and then the verb and
#          the preflight phase (task 04) are two implementations of one policy, which drift, and the
#          two disagreeing about whether a pair is sound is the exact failure #510 exists to end.
#
# Why this is a SOURCE GREP and not a test (#468 demotion order, and dotnet.md 10c - the weakest wiring
# form, used here because the stronger ones are structurally unavailable). Guardrail 03 proves the verb
# WORKS from the real entry point, which is the runtime proxy for reachability - so no grep is spent on
# that. But "works" is exactly what an inlined copy also does: the property here is that ONE
# implementation is shared, and no runtime observation at the CLI boundary can distinguish that from a
# faithful duplicate. This task may write no test project (writeScope is three Cli files), so there is
# no rung to demote into. It proves the text is there; it does NOT prove the call is on the hot path.
# /guardrails-review should re-check that residual.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/26-guardrail-quality-gate/tasks/03-add-samples-verify-command/samples/01-verb-drives-the-shared-verifier.valid.cs';   ./01-...ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/26-guardrail-quality-gate/tasks/03-add-samples-verify-command/samples/01-verb-drives-the-shared-verifier.invalid.cs'; ./01-...ps1  # expect 1
#
# baseline counts on the untouched tree - MEASURED, not assumed:
#   (new\s+SampleVerifier\b|\bSampleVerifier\s*\.\s*\w+\s*\()   n/a - SamplesCommand.cs is CREATED by this task.
#   MEASURED separately: the token "SampleVerifier" occurs ZERO times across all of src/ and tests/ on
#   the untouched tree, so no ancestor's edit can pre-satisfy this clause. Task 01 creates the type and
#   task 02 fills it, but both write only under src/Guardrails.Core/Samples/ and
#   tests/Guardrails.Core.Tests/Samples/ - never src/Guardrails.Cli/.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Cli/Commands/SamplesCommand.cs" }

# PRECONDITION - the only early exit: the clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - the 'samples verify' verb was never written"
    exit 1
}

$raw  = Get-Content $f -Raw                                  # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

# ANCHORED ON THE CALL, NOT THE NAME (issue #521, measured 2026-08-28). A clause ending at the dotted
# NAME is satisfied by `nameof(SampleVerifier.VerifyAsync)` - valid C# containing that exact text, which
# survives the $scan strip because nameof is not a string literal - and a hollow file with two dead
# nameof references was MEASURED to exit 0 against exactly that shape. The trailing `\s*\(` is the whole
# rule. The member alternative is a METHOD by construction (the type's entry point is
# `VerifyAsync(...)`, and the alternation also admits an instance verifier constructed with `new`), so
# requiring the paren cannot false-red a correct call. No `nameof` BAN is added: requiring the call
# already kills the operator, and a ban would false-red a legitimate nameof() inside a message string.
# Do NOT relax this back to a bare dotted name.
if ($scan -cnotmatch '(new\s+SampleVerifier\b|\bSampleVerifier\s*\.\s*[A-Za-z_]\w*\s*\()') {
    Write-Output "$f does not USE SampleVerifier - the 'samples verify' verb is not driving the shared verifier, so whatever it reports is a second implementation of the same policy and will drift from the one the preflight phase runs. Construct it (new SampleVerifier(...)) or CALL a member on it (SampleVerifier.VerifyAsync(...)); naming the type in a comment, in an operator-facing message string, or in a nameof() does not count."
    exit 1
}
exit 0
