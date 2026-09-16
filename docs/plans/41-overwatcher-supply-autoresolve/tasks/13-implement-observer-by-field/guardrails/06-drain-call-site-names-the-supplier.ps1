# catches: the operator's own boundary drain announcing itself as somebody else. Deleting the
#          two-argument member forces this call site to grow a third argument, and the compiler accepts
#          ANY string — including "overwatcher", which is the exact false provenance design 41 was
#          written to close ("Any file an operator had staged would be committed as
#          Supplied-By: overwatcher ... false provenance, in the opposite direction from #712's second
#          defect"). The supplied[] record written five lines above this call already says
#          By = "operator"; the event must agree with the record it announces, and no test in this pair
#          drives the Scheduler, so nothing else can see a disagreement.
#          The second clause catches a SECOND call site left on the old two-argument shape — possible
#          the moment someone re-adds an overload rather than migrating.
#
# LOCAL scope (no `scope` key), deliberately: the equality clause is a statement about THIS file at
# THIS task's attempt. The wiring task adds an auto-resolve call site passing "overwatcher", which is
# correct there and would trip this clause; promoting it to scope:"integration" would red-halt unions
# it was never written for.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/13-implement-observer-by-field/samples/06-drain-call-site-names-the-supplier.valid.cs';   ./06-drain-call-site-names-the-supplier.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/13-implement-observer-by-field/samples/06-drain-call-site-names-the-supplier.invalid.cs'; ./06-drain-call-site-names-the-supplier.ps1  # expect 1
#
# baseline counts on src/Guardrails.Core/Execution/Scheduler.cs — MEASURED with
# Select-String -CaseSensitive against the UNTOUCHED tree, each hit re-read to confirm it is code:
#   SuppliedResourcesCommitted\(                                      1  (the one call, at :4927)
#   SuppliedResourcesCommitted\([^;]*"operator"\s*\)                  0  (required-present)
#   No other task writes this subject BEFORE this one: only task 14 shares it, and it dependsOn this
#   task, so nothing can pre-satisfy the clause at this attempt.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/Scheduler.cs" }

# PRECONDITION — the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist"
    exit 1
}

$raw  = Get-Content $f -Raw                                  # NEVER matched against
# #561 / GR2037 — neutralize string literals BEFORE stripping comments. Deriving $code by stripping
# comments straight from $raw lets a literal that spells '/*' forge a delimiter and blank real code before
# any literal pass runs. Only / and * INSIDE literals are replaced, so a literal's CONTENT still satisfies
# a required-present clause — load-bearing here, where the clause looks for the "operator" argument.
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })   # raw strings
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })  # verbatim
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })  # ordinary
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // and /// line comments — this file
                                                             # discusses the member in prose, so the
                                                             # clauses below must not read comments
# ACCUMULATE, never exit 1 per clause (#478).
$failures = @()

# [^;] spans newlines (unlike '.'), so a call wrapped across lines still matches, and the ';' bound
# keeps the match inside ONE statement.
$calls    = ([regex]::Matches($code, 'SuppliedResourcesCommitted\s*\(')).Count
$operator = ([regex]::Matches($code, 'SuppliedResourcesCommitted\s*\([^;]*"operator"\s*\)')).Count

if ($calls -lt 1) {
    $failures += "$f no longer raises SuppliedResourcesCommitted at all — the task-boundary drain must still announce itself (design 40 §2 step 3). A run whose base changed underneath it has to say so."
}
elseif ($operator -lt 1) {
    $failures += "$f raises SuppliedResourcesCommitted without naming `operator` as the supplier. DrainSuppliedAtTaskBoundary commits what an OPERATOR staged through `guardrails supply`, and the supplied[] record written in the same method already says By = `operator` — the event must agree with the record it announces. Passing `overwatcher` here is the false provenance design 41 exists to close; the overwatcher's own call site belongs to the wiring task."
}
elseif ($calls -ne $operator) {
    $failures += "$f raises SuppliedResourcesCommitted $calls time(s) but only $operator of them name `operator` as the supplier. Every call site in this file is the operator's boundary drain at this point in the plan; a call left on the old two-argument shape, or naming another supplier, is either an un-migrated site or an auto-resolve that belongs to the wiring task."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
