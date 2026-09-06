# catches: a shim wired into `pwsh` only, leaving `PowershellTemplate` - the Windows fallback used when
#          pwsh is absent - still invoking the guardrail directly, so the exit-0 fail-open stays armed on
#          exactly the boxes least likely to notice. The authored tests run on whichever interpreter the
#          machine resolves, so they CANNOT observe the fallback template; this is a structural fact
#          about the build/wiring graph with no runtime proxy, which is why it survives the source-shape
#          demotion gate (#468). Sample pair: samples/03-*.valid.cs / .invalid.cs.
# PURE FUNCTION OF ITS SUBJECT, deliberately: every clause below reads $subject and nothing else. A
#          guardrail carrying a committed sample pair is RUN AGAINST THAT SAMPLE by the pre-DAG
#          samples-verify gate, so a clause reading any OTHER file can never be satisfied by a sample
#          and false-REDs the valid half. That is not hypothetical - the abort-code pin that used to
#          live here read GuardrailRunner.cs and halted the first real run before task 1. It now lives
#          in guardrail 04, which has no sample pair and proves the same fact behaviourally.
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

if ($problems.Count -gt 0) {
    Write-Output "=== Shim routing incomplete ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "Both pwsh interpreter templates route through ShimScript."
exit 0
