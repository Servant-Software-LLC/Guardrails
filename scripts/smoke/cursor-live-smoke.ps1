<#
.SYNOPSIS
    Manual live smoke for the `kind: "cursor"` prompt runner (#764): runs a one-task Guardrails plan on a REAL
    Cursor Agent CLI and asserts it goes green.

.DESCRIPTION
    NOT a test, and never run by CI: it launches the real `agent`, which spends Cursor account credit. Run it by
    hand after a change to CursorPromptRunner, StreamJsonCliSession, or the Cursor parts of SSOT section 9.9.

    It builds a throwaway plan under %TEMP% (serial mode, so no git repository is needed) whose single
    `promptRunners` block is `kind: "cursor"`:
      - one PROMPT action that must write `hello.txt` containing exactly `hello from cursor`;
      - one SCRIPT guardrail that checks that file byte-for-byte.
    It then runs the plan through the harness BUILT FROM THIS SOURCE TREE (`dotnet run --project
    src/Guardrails.Cli`), never the globally installed `guardrails` tool, and asserts:
      - the run exits 0;
      - hello.txt holds the expected text;
      - the attempt's stream log contains the prompt echo (Cursor received the composed prompt on stdin).

    What a green run proves: the argv parses on this Cursor build, stdin delivery works, the stream parses to
    a completed result, and the harness's own guardrail passed on what the agent wrote.

.PARAMETER AgentPath
    Optional path to the Cursor `agent` executable/shim (e.g. %LOCALAPPDATA%\cursor-agent\agent.cmd). Default:
    `agent`, resolved on PATH by the harness exactly as a real plan would.

.PARAMETER Model
    Optional `--model` for the block. Default: none (Cursor's own default, "Auto").

.PARAMETER ValidateOnly
    Build the plan and run `guardrails validate` on it instead of `run` — spends nothing, launches no agent.
    Useful to check the generated plan itself after a schema change.

.PARAMETER Keep
    Keep the temp plan folder afterward (it is printed either way) instead of deleting it on success.

.EXAMPLE
    pwsh scripts/smoke/cursor-live-smoke.ps1
.EXAMPLE
    pwsh scripts/smoke/cursor-live-smoke.ps1 -AgentPath "$env:LOCALAPPDATA\cursor-agent\agent.cmd" -Keep
#>
[CmdletBinding()]
param(
    [string] $AgentPath = 'agent',
    [string] $Model,
    [switch] $Keep,
    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$planDir = Join-Path ([IO.Path]::GetTempPath()) ("guardrails-cursor-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$taskDir = Join-Path $planDir 'tasks/01-write-hello'
New-Item -ItemType Directory -Force -Path (Join-Path $taskDir 'guardrails') | Out-Null

$block = [ordered]@{ kind = 'cursor'; command = $AgentPath }
if ($Model) { $block.model = $Model }
$config = [ordered]@{
    version               = 1
    maxParallelism        = 1
    defaultRetries        = 0
    defaultTimeoutSeconds = 600
    workspace             = '.'
    promptRunners         = [ordered]@{ default = 'cursor'; cursor = $block }
}
$config | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8NoBOM (Join-Path $planDir 'guardrails.json')

@'
{
  "description": "Write hello.txt in the workspace root containing exactly: hello from cursor",
  "dependsOn": [],
  "writeScope": ["hello.txt"]
}
'@ | Set-Content -Encoding utf8NoBOM (Join-Path $taskDir 'task.json')

@'
Create a file named `hello.txt` in the workspace root (your current working directory). Its entire content
must be exactly the text `hello from cursor` followed by a single newline. Do not create or modify any other
file. Do not run any shell command.
'@ | Set-Content -Encoding utf8NoBOM (Join-Path $taskDir 'action.prompt.md')

@'
# catches: the agent claimed success but hello.txt is missing or does not hold exactly "hello from cursor"
if (-not (Test-Path "hello.txt")) { Write-Output "hello.txt does not exist in the workspace"; exit 1 }
$content = (Get-Content "hello.txt" -Raw).Trim()
if ($content -ne "hello from cursor") { Write-Output "hello.txt holds '$content', expected 'hello from cursor'"; exit 1 }
exit 0
'@ | Set-Content -Encoding utf8NoBOM (Join-Path $taskDir 'guardrails/01-hello-exact.ps1')

Write-Host "Plan folder: $planDir"

if ($ValidateOnly) {
    & dotnet run --project (Join-Path $repoRoot 'src/Guardrails.Cli') -- validate $planDir
    $validateExit = $LASTEXITCODE
    if (-not $Keep) { Remove-Item -Recurse -Force $planDir }
    exit $validateExit
}

Write-Host "Running the harness from source: dotnet run --project $repoRoot/src/Guardrails.Cli -- run <plan> --no-ui --fresh"

& dotnet run --project (Join-Path $repoRoot 'src/Guardrails.Cli') -- run $planDir --no-ui --fresh
$exit = $LASTEXITCODE

$failures = @()
if ($exit -ne 0) { $failures += "guardrails run exited $exit (expected 0)" }

$hello = Join-Path $planDir 'hello.txt'
if (-not (Test-Path $hello)) { $failures += 'hello.txt was not written' }
elseif ((Get-Content $hello -Raw).Trim() -ne 'hello from cursor') { $failures += "hello.txt holds '$((Get-Content $hello -Raw).Trim())'" }

$stream = Get-ChildItem -Path (Join-Path $planDir 'logs') -Recurse -Filter '*stream*.jsonl' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $stream) { $failures += 'no stream log was written under logs/' }
elseif (-not (Select-String -Path $stream.FullName -Pattern '"type":"user"' -SimpleMatch -Quiet)) {
    $failures += "the stream log ($($stream.FullName)) carries no prompt echo"
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'CURSOR LIVE SMOKE: FAILED' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Plan folder kept for inspection: $planDir"
    exit 1
}

Write-Host ''
Write-Host 'CURSOR LIVE SMOKE: GREEN' -ForegroundColor Green
if ($Keep) { Write-Host "Plan folder kept: $planDir" }
else { Remove-Item -Recurse -Force $planDir }
exit 0
