# catches: the false claims surviving the edit - either the unconditional "works under the default
#          read-only git permissions" assertion or the "writeScope-enforced at write time" rationale
#          still shipping in the salvage advice.
$f = 'src/Guardrails.Core/Execution/RetryPolicy.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f
$bad = @()
# NOTE: the claim is emitted across TWO AppendLine calls ("... This works under the" /
# "default read-only git permissions; ..."), so a single-line regex for the whole sentence
# NEVER matches and silently false-passes. Author-time smoke test (#302) caught exactly that.
# Match the distinctive tail fragment, which does sit on one source line.
if ($c -match 'default read-only git permissions') { $bad += "the unconditional 'works under the default read-only git permissions' claim is still present (false: a real plan in this repo grants no git and still fires salvage)" }
if ($c -match 'writeScope-enforced at write time')                      { $bad += "the 'writeScope-enforced at write time' rationale is still present (false: WriteScopeCheck is retroactive for BOTH routes)" }
if ($bad.Count -gt 0) { Write-Output ($bad -join '; '); exit 1 }
exit 0
