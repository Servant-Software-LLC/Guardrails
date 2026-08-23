# Two-sided sample probe for guardrails/03-wave-deliverables-present.ps1 (#468/#302). See README.md.
#
#   VALID   half - a tree carrying a realistic DELIVERED form of all 12 required clauses -> expect exit 0.
#   INVALID half - the same tree where every token appears ONLY in a comment           -> expect exit 1,
#                  with all 12 clauses still failing. This is the half that caught the live defect.
#
# The clause list is lifted from the guardrail itself, so adding a clause tests it here automatically.
# Read-only against the repo: everything happens in a %TEMP% copy.

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..')).Path
$gate = Join-Path $PSScriptRoot '..\guardrails\03-wave-deliverables-present.ps1'

$src = Get-Content -Raw -Path $gate
$m = [regex]::Match($src, '(?ms)^\$required = @\((.*?)^\)\s*$')
if (-not $m.Success) { Write-Output 'ABORT: could not lift $required from the guardrail'; exit 9 }
$required = Invoke-Expression ('@(' + $m.Groups[1].Value + ')')
$files = $required | ForEach-Object { $_[0] } | Sort-Object -Unique

# The DELIVERED form of each clause: real code / real prose, never a mention.
$delivered = @{
    'src/Guardrails.Core/Prompts/ClaudeStreamParser.cs'   = 'public string? Model { get; init; }' + [Environment]::NewLine + 'if (type == "system") { }'
    'src/Guardrails.Core/Prompts/PromptInvocation.cs'     = 'public string? ObservedModel { get; init; }'
    'src/Guardrails.Core/Prompts/ClaudePromptRunner.cs'   = 'ObservedModel = parsed.Model,'
    'src/Guardrails.Core/Journal/JournalModel.cs'         = 'public string? RequestedModel { get; init; }'
    'src/Guardrails.Core/Execution/ActionRunner.cs'       = 'ObservedModel = result.ObservedModel,'
    'src/Guardrails.Core/Execution/TaskExecutor.cs'       = 'RequestedModel = requested,'
    'tests/Guardrails.Core.Tests/ModelTiering/ObservedModelCaptureTests.cs'        = 'public sealed class ObservedModelCaptureTests { }'
    'tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs'    = '[Trait("Category", "ObservedModelProvenance")]'
    'docs/plans/02-schemas-and-contracts.md'              = '`"requestedModel": "claude-opus-4"` - written only on disagreement.'
    'docs/plans/17-model-tiering.md'                      = 'Amended: provenance carries requestedModel only when it differs.'
    '.claude/skills/guardrails-domain-knowledge/SKILL.md' = 'provenance.model is best-known-actual; requestedModel appears only on mismatch.'
}

function New-Tree([bool]$AsComment) {
    $t = Join-Path ([System.IO.Path]::GetTempPath()) ('gr-s3w2-' + [guid]::NewGuid().ToString('N'))
    foreach ($f in $files) {
        $dst = Join-Path $t $f
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        $seed = Join-Path $repo $f
        if (Test-Path $seed) { Copy-Item $seed $dst } else { Set-Content -Path $dst -Value '' -NoNewline }
        if ($delivered.ContainsKey($f)) {
            $add = $delivered[$f]
            if ($AsComment) {
                # The lazy-implementer edit: the same tokens, in a comment. A markdown file has no
                # comment syntax, so its clauses are expected to clear in BOTH halves - the 12-of-12
                # assertion below is what pins that the C# ones do not.
                $prefix = if ($f -like '*.cs') { '// ' } else { '' }
                $add = ($add -split [Environment]::NewLine | ForEach-Object { $prefix + $_ }) -join [Environment]::NewLine
            }
            Add-Content -Path $dst -Value ([Environment]::NewLine + $add)
        }
    }
    return $t
}

function Invoke-Gate($tree) {
    Push-Location $tree
    $o = & pwsh -NoProfile -File $gate 2>&1
    $e = $LASTEXITCODE
    Pop-Location
    return @{ Exit = $e; Fired = @($o | Where-Object { $_ -match '^\s+- ' }).Count }
}

$fail = 0

$t = New-Tree $false
$v = Invoke-Gate $t
Remove-Item $t -Recurse -Force
if ($v.Exit -eq 0) { Write-Output "VALID   (delivered)      -> exit 0, 0 clauses firing   OK" }
else { Write-Output "VALID   (delivered)      -> exit $($v.Exit), $($v.Fired) firing   *** FALSE RED ***"; $fail++ }

$t = New-Tree $true
$i = Invoke-Gate $t
Remove-Item $t -Recurse -Force
$csClauses = @($required | Where-Object { $_[0] -like '*.cs' }).Count
if ($i.Exit -ne 0 -and $i.Fired -ge $csClauses) {
    Write-Output "INVALID (comments only)  -> exit $($i.Exit), $($i.Fired) clauses firing   OK (>= $csClauses C# clauses)"
}
else {
    Write-Output "INVALID (comments only)  -> exit $($i.Exit), only $($i.Fired) firing   *** COMMENT-SATISFIABLE, expected >= $csClauses ***"
    $fail++
}

Write-Output ''
if ($fail -eq 0) { Write-Output "RESULT: both halves behave. $($required.Count) required clauses, $csClauses of them C#."; exit 0 }
Write-Output "RESULT: $fail half/halves DEFECTIVE."; exit 1
