# catches: this task solving "print the model that actually ran" by REACHING BACK to the stream instead
#          of reading the fact wave 2 already folded onto the provenance - or by forcing `--model` onto
#          the runner so the two strings agree by construction. Both make the tests in
#          01-disclosure-tests-pass go green while destroying the thing the wave is for:
#
#          - a second parse is a second derivation of a decision that must have exactly one, and it will
#            drift from the first the moment either changes (the same argument JournalModel.cs makes for
#            refusing a `resolvedModel` key: "two fields claiming the same fact is how they drift");
#          - forcing `--model` pins the zero-setup user who deliberately passes nothing, and makes
#            `requestedModel` permanently null - so the mismatch signal can never fire and the surface
#            reports the model we ASKED for, which is the weaker fact #349 exists to stop reporting.
#
#          The action prompt states both prohibitions in prose. Prose alone binds nothing (#221); this
#          is the structural backing.
#
# FAIL-ON-PRESENT only - no required-present clause in this file, so there is no self-collision to
# reconcile (#470). Scanned over STRIPPED source (comments AND string literals removed) and anchored on a
# USE, never a mention: the action prompt names `ClaudeStreamParser` and `--model` three times between
# them, so a ban on the bare word would red an agent that merely echoed the prohibition into a comment
# explaining why it did not do the thing (#470/#76).
#
# MEASURED BASELINE 2026-08-23 against the merged wave-2 HEAD, case-sensitively, against the exact file
# this scans: both clauses are 0. A forbidden-present clause is SUPPOSED to be green on arrival (#478) -
# that is what a healthy ban looks like before its task runs.
$ErrorActionPreference = 'Continue'
$failures = @()

$path = 'src/Guardrails.Core/Execution/TaskExecutor.cs'
if (-not (Test-Path $path -PathType Leaf)) {
    # PRECONDITION: the file this task's whole deliverable lives in is gone, so every clause below would
    # scan a null read. Report it once rather than emitting two misleading "no violation found" passes.
    Write-Output "$path does not exist - this task's only writeScope entry is missing, so nothing was scanned"
    exit 1
}

$raw = Get-Content -Raw -Path $path

# Strip XML-doc comments, line comments, block comments and then string literals, in that order.
# Comments first: a `// "--model"` would otherwise survive the literal strip as a bare word. Literals
# second: the ban is on the argv token, and TaskExecutor.cs legitimately carries many other literals.
$scan = $raw -replace '(?m)^\s*///.*$', ''
$scan = $scan -replace '(?s)/\*.*?\*/', ''
$scan = $scan -replace '(?m)//.*$', ''
$scan = $scan -replace '"(?:[^"\\\r\n]|\\.)*"', '""'

# --- the stream is not re-parsed here -------------------------------------------------------------
# Anchored on CONSTRUCTION or STATIC ACCESS, the only two ways this type can be used, rather than on the
# type name: a `using` line or a doc reference is not a second parse.
if ($scan -match '(new\s+ClaudeStreamParser\b|ClaudeStreamParser\s*\.)') {
    $failures += "$path USES ClaudeStreamParser (constructed, or accessed statically). Wave 2 owns capture: the observed model is already on the attempt's AttemptProvenance by the time this file needs it. Read provenance.Model / provenance.RequestedModel instead of parsing the stream a second time"
}

# --- the runner is not pinned to a model ----------------------------------------------------------
# The literal strip above replaced every string with `""`, so a `--model` argv token is invisible to a
# plain scan. Re-run this ONE clause against the RAW text with comments stripped but literals intact -
# a different subject variable on purpose, which is why the pair does not trip GR2057.
$rawNoComments = $raw -replace '(?m)^\s*///.*$', ''
$rawNoComments = $rawNoComments -replace '(?s)/\*.*?\*/', ''
$rawNoComments = $rawNoComments -replace '(?m)//.*$', ''
if ($rawNoComments -match '"--model"') {
    $failures += "$path spells the argv token `"--model`". Do not force a model onto the runner invocation: it pins the zero-setup user who deliberately passes nothing, and it makes the observed and requested strings agree by construction so provenance.RequestedModel can never be written. The mechanism is to parse the echo, never to force the flag"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== $($failures.Count) forbidden construct(s) in TaskExecutor.cs ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This wave CONSUMES what wave 2 persisted. Both surfaces read the folded provenance object; neither re-derives the model."
    exit 1
}
exit 0
