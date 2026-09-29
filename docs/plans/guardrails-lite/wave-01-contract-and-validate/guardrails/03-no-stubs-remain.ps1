# catches: a wave that ends with a TDD stub still standing in for a kernel script - an author-tests task's
#          `{"script":…,"ok":false,"stub":true}` / `exit 99` / `throw 'stub'` skeleton that no implement
#          task replaced (a dropped task, a mis-scoped writeScope, or an implement task that wrote a NEW
#          file beside the stub instead of over it). Each implement task's own tests would catch its OWN
#          stub; this catches the one no test selects - and a kernel script the wave was meant to produce
#          and never did (the 8-file manifest below), which no per-task filter can see.
#          Source-shape, not a test, because "no stub text is left in the shipped tree" is a property of the
#          FILES a security reviewer will read, not of any runtime path a test drives (#468 ledger line).
#          Scans with the PowerShell TOKENIZER and ignores COMMENT tokens, so a comment saying "the stub
#          used to exit 99" is not a false red (#97/#98); string literals ARE scanned - the stub marker IS a
#          string literal.
# Measured baselines (#478), against this repo at breakdown time (scripts/lite/ absent):
#          required-present manifest: 0 of 8 files present (EXPECTED - this is a wave-EXIT gate; the wave
#          produces them). Forbidden-present clauses: 0 hits (nothing to scan) - a ban green on arrival is
#          a correct ban.
# Subject: GR_SUBJECT (sample-pair verification) > GUARDRAILS_WORKSPACE > cwd. The subject is a WORKSPACE
#          root; the scan target is <subject>/scripts/lite/.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$ws = if ($env:GR_SUBJECT) { $env:GR_SUBJECT }
      elseif ($args.Count -gt 0 -and $args[0]) { $args[0] }
      elseif ($env:GUARDRAILS_WORKSPACE) { $env:GUARDRAILS_WORKSPACE }
      else { (Get-Location).Path }
$lite = Join-Path $ws 'scripts/lite'

# PRECONDITION - the one early exit: with no scripts/lite/ every clause below is meaningless.
if (-not (Test-Path -LiteralPath $lite -PathType Container)) {
    Write-Output "scripts/lite/ does not exist under '$ws' - wave 01 was meant to produce the Lite validate/hash/lock kernel there and produced nothing."
    exit 1
}

$failures = @()

# Clause 1 (required-present): the kernel files wave 01 exists to deliver.
$expected = @(
    'validate.ps1', 'validate/Load.psm1', 'validate/Graph.psm1', 'validate/Subset.psm1',
    'lib/Hash.psm1', 'plan-hash.ps1', 'mark-reviewed.ps1', 'lock.ps1'
)
foreach ($rel in $expected) {
    if (-not (Test-Path -LiteralPath (Join-Path $lite $rel) -PathType Leaf)) {
        $failures += "MISSING  scripts/lite/$rel - a wave-01 deliverable was never written."
    }
}

# Clauses 2-4 (forbidden-present), over every .ps1/.psm1 with COMMENT tokens blanked out.
$banned = [ordered]@{
    'stub JSON marker ("stub":true / stub = $true)' = '"stub"\s*:\s*true|\bstub\s*=\s*\$true'
    'stub exit code (exit 99)'                      = '\bexit\s+99\b'
    "stub throw (throw 'stub')"                     = "\bthrow\s+['""]stub['""]"
}
$files = @(Get-ChildItem -LiteralPath $lite -Recurse -File -Include '*.ps1', '*.psm1' | Sort-Object FullName)
foreach ($f in $files) {
    $tokens = $null; $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$parseErrors)
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $chars = $text.ToCharArray()
    foreach ($t in $tokens) {
        if ($t.Kind -eq [System.Management.Automation.Language.TokenKind]::Comment) {
            for ($i = $t.Extent.StartOffset; $i -lt $t.Extent.EndOffset; $i++) {
                if ($chars[$i] -ne "`n") { $chars[$i] = ' ' }
            }
        }
    }
    $code = -join $chars
    $rel = [System.IO.Path]::GetRelativePath($ws, $f.FullName) -replace '\\', '/'
    foreach ($label in $banned.Keys) {
        if ($code -cmatch $banned[$label]) {
            $failures += "STUB     $rel still carries the $label - the implement task that owns it never replaced the TDD stub."
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== wave-01 kernel is incomplete: $($failures.Count) problem(s) under scripts/lite/ ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "scripts/lite/: all $($expected.Count) wave-01 kernel files present, no stub marker in $($files.Count) script(s)."
exit 0
