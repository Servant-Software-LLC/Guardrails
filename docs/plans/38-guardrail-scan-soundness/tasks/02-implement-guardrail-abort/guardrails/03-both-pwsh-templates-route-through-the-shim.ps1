# catches: a shim wired into `pwsh` only, leaving `PowershellTemplate` - the Windows fallback used when
#          pwsh is absent - still invoking the guardrail directly, so the exit-0 fail-open stays armed on
#          exactly the boxes least likely to notice. The authored tests run on whichever interpreter the
#          machine resolves, so they CANNOT observe the fallback template; this is a structural fact
#          about the build/wiring graph with no runtime proxy, which is why it survives the source-shape
#          demotion gate (#468). Sample pair: samples/03-*.valid.cs / .invalid.cs.
# Measured baseline (#478): `ShimScript` occurs 0 times in InterpreterMap.cs on the starting tree
#          (verified 2026-09-06 on master a3f3e977).
$ErrorActionPreference = 'Stop'

$problems = New-Object System.Collections.Generic.List[string]
$subject  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Execution/InterpreterMap.cs' }

if (-not (Test-Path -LiteralPath $subject -PathType Leaf)) {
    Write-Output "PRECONDITION: $subject does not exist - every clause below would be vacuous."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $subject

# Build a length-preserving scan copy. ORDER IS LOAD-BEARING and is the whole point of this plan (#561):
# string literals are neutralized FIRST, so a delimiter spelled inside one can never open a phantom
# block comment and blank the region between it and the next `*/`. Every substitution is 1:1, so offsets
# stay valid. Neutralize `/` and `*` as well as braces - braces alone leave the delimiters intact.
$neutralize = { param($m) ($m.Value -replace '[{}/*]', '_') }
$blank      = { param($m) ($m.Value -replace '[^\r\n]', ' ') }

$scan = [regex]::Replace($raw,  '"""[\s\S]*?"""',  $neutralize)   # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', $neutralize)   # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', $neutralize)   # ordinary strings
$scan = [regex]::Replace($scan, '/\*[\s\S]*?\*/',  $blank)        # block comments
$scan = [regex]::Replace($scan, '(?m)//[^\r\n]*',  $blank)        # line comments

if ($scan.Length -ne $raw.Length) {
    Write-Output "PRECONDITION: the scan copy is $($scan.Length) chars against a $($raw.Length)-char source - a substitution was not length-preserving, so every offset below is unreliable."
    exit 1
}

foreach ($member in @('PwshTemplate', 'PowershellTemplate')) {
    # The member's body runs from its name to the terminating semicolon of its expression body.
    $m = [regex]::Match($scan, [regex]::Escape($member) + '\s*=>[\s\S]*?;')
    if (-not $m.Success) {
        $problems.Add("[$member] no expression-bodied member found in $subject. Either it was renamed or restructured (this guardrail binds to the name - reconcile the two), OR the scan copy blanked it: if BOTH members report this and the file plainly declares them, suspect the preprocessing above, not the file (#561).")
        continue
    }
    if ($m.Value -notmatch 'ShimScript') {
        $problems.Add("[$member] does not reference ShimScript - this template still invokes the guardrail script directly, so an aborted guardrail run through it exits 0 and the harness records a PASS (#608). Both pwsh templates must route through the shim, not just the primary one.")
    }
}

# The abort exit code is a DATUM with a downstream consumer: task 08's guardrail requires the literal
# `exit 97` in the SSOT. Nothing gated its CARRIER, so task 02 could ship `exit 90` and make task 08's
# clause honestly unsatisfiable - a #474 severed hop. Pin it here, at the file that produces it.
$runner = 'src/Guardrails.Core/Execution/GuardrailRunner.cs'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    $problems.Add("[$runner] does not exist - the abort verdict has no home.")
}
else {
    $rc = Get-Content -Raw -LiteralPath $runner
    if ($rc -notmatch '(?<![0-9#])97(?![0-9])') {
        $problems.Add("[$runner] never mentions the abort exit code 97. The shim emits it and the SSOT (task 08) is required to document `exit 97`; if this task chose a different number, all three must change together, not two of them.")
    }
}

if ($problems.Count -gt 0) {
    Write-Output "=== Shim routing incomplete ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "Both pwsh interpreter templates route through ShimScript."
exit 0
