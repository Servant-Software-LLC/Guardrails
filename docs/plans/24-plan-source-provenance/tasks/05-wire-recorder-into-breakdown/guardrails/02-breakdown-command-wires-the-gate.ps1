# catches: half B never reaching the production path - DeclaredCountGate implemented, unit-tested and
#          green, but BreakdownCommand never calls it, so a breakdown that under-records delegated
#          decisions still exits 0 from the CLI. The gate would be dead code reachable only from xUnit.
#
# Why this is a SOURCE GREP and not a test (#468 demotion order, and dotnet.md 10c - the weakest wiring
# form, used here because the stronger ones are structurally unavailable): the only test project this
# task may write into is tests/Guardrails.Core.Tests, which references Guardrails.Core ONLY and cannot
# see Guardrails.Cli. Adding a project reference or an integration-test file is outside this task's
# writeScope. So no test in this plan can drive BreakdownCommand, and the honest choice is a grep that
# says exactly what it proves. It proves the text is there; it does NOT prove the call is reached on the
# production path. /guardrails-review should re-check that residual.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/24-plan-source-provenance/tasks/05-wire-recorder-into-breakdown/samples/02-breakdown-command-wires-the-gate.valid.cs';   ./02-...ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/24-plan-source-provenance/tasks/05-wire-recorder-into-breakdown/samples/02-breakdown-command-wires-the-gate.invalid.cs'; ./02-...ps1  # expect 1
#
# baseline counts on the untouched tree - MEASURED with Select-String -CaseSensitive over this exact
# subject, not assumed:
#   (new\s+DeclaredCountGate\b|\bDeclaredCountGate\s*\.)   0   (both alternatives measured 0 separately)
#   No ancestor task's prompt or writeScope writes that token into this subject: tasks 02 and 04 write
#   only under src/Guardrails.Core/Breakdown/, never src/Guardrails.Cli/.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Cli/Commands/BreakdownCommand.cs" }

# PRECONDITION - the only early exit.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - cannot verify the declared-count gate is wired into the breakdown command"
    exit 1
}

$raw  = Get-Content $f -Raw                                  # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

# Anchored on a USE (a construction or a member access), never the bare word, and read from $scan so a
# mention inside an operator-facing message string cannot satisfy it (#470/#75). DeclaredCountGateResult
# and friends do not satisfy it either - the \b and the trailing '.' both require the type itself.
if ($scan -cnotmatch '(new\s+DeclaredCountGate\b|\bDeclaredCountGate\s*\.)') {
    Write-Output "$f does not USE DeclaredCountGate - the declared-count gate is not wired into the breakdown command, so a breakdown that records fewer delegated decisions than the plan declared still succeeds from the CLI (naming the type in a comment or a message string does not count)"
    exit 1
}
exit 0
